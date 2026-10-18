using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Portal.Common;
using Portal.Common.Helpers;
using Portal.Common.Models;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace Portal.Host.Services;

public record UnlockTestResult(
    bool IsSuccess,
    string Message,
    long LatencyMs,
    string? Transport,
    string DeviceName);

/// <summary>
/// Service for verifying connectivity, latency, and biometric confirmation
/// from paired mobile devices directly within the Host UI, without locking Windows.
/// </summary>
public class UnlockTestService
{
    private readonly CertificateManager _certManager;
    private readonly MdnsAnnouncer _mdns = new();

    public UnlockTestService(CertificateManager certManager)
    {
        _certManager = certManager;
    }

    public async Task<UnlockTestResult> TestDeviceAsync(
        DeviceModel device,
        Action<string>? statusCallback,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        var stopwatch = Stopwatch.StartNew();

        statusCallback?.Invoke(Localization.T("Preparing connection test..."));
        Logger.Log($"[UnlockTestService] Starting test for device: {device.IdsSafe()} (Transport: {device.TransportType})");

        var config = PortalWinConfig.Load();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var effectiveCt = linkedCts.Token;

        var tcs = new TaskCompletionSource<UnlockTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeSockets = new System.Collections.Concurrent.ConcurrentBag<WebSocket>();
        var activeBtSockets = new System.Collections.Concurrent.ConcurrentBag<StreamSocket>();

        void CompleteTest(UnlockTestResult testResult)
        {
            if (tcs.TrySetResult(testResult))
            {
                try { timeoutCts.Cancel(); } catch { }
                foreach (var s in activeSockets)
                {
                    try { s.Abort(); } catch { }
                }
                foreach (var s in activeBtSockets)
                {
                    try { s.Dispose(); } catch { }
                }
            }
        }

        using var reg = effectiveCt.Register(() =>
        {
            if (ct.IsCancellationRequested)
            {
                CompleteTest(new UnlockTestResult(false, Localization.T("Test cancelled."), stopwatch.ElapsedMilliseconds, null, device.Name));
            }
            else
            {
                CompleteTest(new UnlockTestResult(false, Localization.T("Test timed out. The phone did not respond in time."), stopwatch.ElapsedMilliseconds, null, device.Name));
            }
        });

        WebApplication? app = null;
        RfcommServiceProvider? btProvider = null;
        StreamSocketListener? btListener = null;

        var isBluetoothSupported = device is BluetoothDeviceModel ||
                                   device.TransportType == TransportType.Bluetooth ||
                                   device.TransportType == TransportType.Both;
        var isNetworkSupported = device.TransportType == TransportType.Network ||
                                 device.TransportType == TransportType.Both ||
                                 !(device is BluetoothDeviceModel);

        var testRequestId = Guid.NewGuid().ToString("N");

        try
        {
            X509Certificate2? cert = null;
            if (isNetworkSupported)
            {
                cert = _certManager.CreateOrLoadCertificate(config);
                if (cert == null && !isBluetoothSupported)
                {
                    return new UnlockTestResult(false, Localization.T("No host certificate found."), 0, null, device.Name);
                }
            }

            if (cert != null && isNetworkSupported)
            {
                // Start mDNS advertising as 'locked' so the phone's auto-discovery picks up the PC
                _mdns.Start(config, mode: "locked");

                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseKestrel(options =>
                {
                    options.ListenAnyIP(config.Port, listenOptions =>
                    {
                        listenOptions.UseHttps(adapterOptions =>
                        {
                            adapterOptions.ServerCertificate = cert;
                            adapterOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                            adapterOptions.AllowAnyClientCertificate();
                        });
                    });
                });

                builder.Services.AddLogging();
                app = builder.Build();
                app.UseWebSockets(new WebSocketOptions
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(2)
                });

            // 1. Direct REST unlock test endpoint
            app.MapPost("/api/unlock", (UnlockRequest request, HttpContext context) =>
            {
                var clientCert = context.Connection.ClientCertificate;
                if (clientCert == null)
                {
                    Logger.LogWarning("[UnlockTestService] REST rejected: no client certificate.");
                    return Results.Json(new UnlockResponse(false, "Unauthorized"), statusCode: 401);
                }

                var clientCertHash = CertificateService.GetCertHash(clientCert);
                var clientThumbprint = clientCert.Thumbprint ?? string.Empty;

                Logger.Log($"[UnlockTestService] Received /api/unlock request from client: {request.ClientId}, CertHash: {clientCertHash} (SHA1: {clientThumbprint})");

                var targetCertClean = (device.CertHash ?? string.Empty).Replace("-", "").Replace(":", "").Trim();
                var clientSha256Clean = clientCertHash.Replace("-", "").Replace(":", "").Trim();
                var clientSha1Clean = clientThumbprint.Replace("-", "").Replace(":", "").Trim();

                bool isCertMatched = string.IsNullOrEmpty(targetCertClean) ||
                                     string.Equals(targetCertClean, clientSha256Clean, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(targetCertClean, clientSha1Clean, StringComparison.OrdinalIgnoreCase);

                if (isCertMatched && (string.Equals(device.ClientId, request.ClientId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(request.ClientId)))
                {
                    stopwatch.Stop();
                    var latency = stopwatch.ElapsedMilliseconds;
                    Logger.Log($"[UnlockTestService] REST unlock test successful for {device.Name} in {latency} ms");

                    CompleteTest(new UnlockTestResult(
                        true,
                        string.Format(Localization.T("Connection verified successfully! Latency: {0} ms"), latency),
                        latency,
                        "Wi-Fi (REST)",
                        device.Name));

                    return Results.Ok(new UnlockResponse(true, null));
                }

                Logger.LogWarning($"[UnlockTestService] Certificate mismatch or invalid client ID. Expected: {device.CertHash}, Got SHA256={clientCertHash}");
                return Results.Json(new UnlockResponse(false, "Unauthorized"), statusCode: 403);
            });

            // 2. WebSocket bidirectional test endpoint
            app.Map("/ws", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                var clientCert = context.Connection.ClientCertificate;
                if (clientCert == null)
                {
                    Logger.LogWarning("[UnlockTestService] WS rejected: no client certificate.");
                    context.Response.StatusCode = 401;
                    return;
                }

                var clientCertHash = CertificateService.GetCertHash(clientCert);
                var clientThumbprint = clientCert.Thumbprint ?? string.Empty;

                var targetCertClean = (device.CertHash ?? string.Empty).Replace("-", "").Replace(":", "").Trim();
                var clientSha256Clean = clientCertHash.Replace("-", "").Replace(":", "").Trim();
                var clientSha1Clean = clientThumbprint.Replace("-", "").Replace(":", "").Trim();

                bool isCertMatched = string.IsNullOrEmpty(targetCertClean) ||
                                     string.Equals(targetCertClean, clientSha256Clean, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(targetCertClean, clientSha1Clean, StringComparison.OrdinalIgnoreCase);

                if (!isCertMatched)
                {
                    Logger.LogWarning($"[UnlockTestService] WS rejected: cert hash mismatch. Got SHA256={clientCertHash} (SHA1={clientThumbprint}), expected {device.CertHash}");
                    context.Response.StatusCode = 403;
                    return;
                }

                var ws = await context.WebSockets.AcceptWebSocketAsync();
                activeSockets.Add(ws);
                Logger.Log($"[UnlockTestService] WebSocket accepted from {device.Name}. Sending test unlock request...");

                // Send test unlock request over WS (matching Credential Provider format, Status is null)
                var reqMsg = new WsMessage("unlock_request", device.ClientId, null, testRequestId);
                var sendBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reqMsg));
                await ws.SendAsync(new ArraySegment<byte>(sendBytes), WebSocketMessageType.Text, true, effectiveCt);
                Logger.Log($"[UnlockTestService] Sent unlock_request to {device.Name} (ClientId: {device.ClientId}, RequestId: {testRequestId})");

                statusCallback?.Invoke(Localization.T("Please approve the unlock prompt on your phone..."));

                // Await response using chunk-safe reader
                while (ws.State == WebSocketState.Open && !effectiveCt.IsCancellationRequested)
                {
                    var (messageType, json) = await ReceiveTextMessageAsync(ws, effectiveCt);
                    if (messageType == WebSocketMessageType.Close || string.IsNullOrWhiteSpace(json))
                    {
                        break;
                    }

                    Logger.Log($"[UnlockTestService] WS message received: {json}");

                    try
                    {
                        WsMessage? wsMsg = null;
                        try
                        {
                            wsMsg = JsonSerializer.Deserialize<WsMessage>(json, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });
                        }
                        catch { }

                        var status = wsMsg?.Status;
                        var type = wsMsg?.Type;

                        // Fallback case-insensitive check in case of custom/unmatched properties
                        if (string.IsNullOrEmpty(status) && string.IsNullOrEmpty(type))
                        {
                            using var doc = JsonDocument.Parse(json);
                            foreach (var prop in doc.RootElement.EnumerateObject())
                            {
                                if (string.Equals(prop.Name, "status", StringComparison.OrdinalIgnoreCase))
                                    status = prop.Value.GetString();
                                else if (string.Equals(prop.Name, "type", StringComparison.OrdinalIgnoreCase))
                                    type = prop.Value.GetString();
                            }
                        }

                        Logger.Log($"[UnlockTestService] Parsed WS message: type='{type}' status='{status}' requestId='{wsMsg?.RequestId}'");

                        if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(type, "unlock_approved", StringComparison.OrdinalIgnoreCase))
                        {
                            stopwatch.Stop();
                            var latency = stopwatch.ElapsedMilliseconds;
                            Logger.Log($"[UnlockTestService] Unlock approved on mobile device for {device.Name} in {latency} ms");

                            CompleteTest(new UnlockTestResult(
                                true,
                                string.Format(Localization.T("Connection verified successfully! Latency: {0} ms"), latency),
                                latency,
                                "Wi-Fi (WebSocket)",
                                device.Name));
                            break;
                        }
                        else if (string.Equals(status, "denied", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(type, "unlock_denied", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(type, "unlock_cancelled", StringComparison.OrdinalIgnoreCase))
                        {
                            stopwatch.Stop();
                            var latency = stopwatch.ElapsedMilliseconds;
                            Logger.LogWarning($"[UnlockTestService] Unlock rejected on mobile device for {device.Name} (status='{status}', type='{type}')");

                            CompleteTest(new UnlockTestResult(
                                false,
                                Localization.T("Biometric authentication was rejected or cancelled on the device."),
                                latency,
                                "Wi-Fi (WebSocket)",
                                device.Name));
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"[UnlockTestService] Failed to parse WS message: {ex.Message}");
                    }
                }
            });
            }

            if (isBluetoothSupported)
            {
                try
                {
                    btProvider = await RfcommServiceProvider.CreateAsync(
                        RfcommServiceId.FromUuid(BtProtocol.ServiceUuid));
                    btListener = new StreamSocketListener();
                    btListener.ConnectionReceived += (sender, args) =>
                    {
                        var socket = args.Socket;
                        activeBtSockets.Add(socket);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await HandleBtConnectionAsync(socket, device, config, testRequestId, stopwatch, statusCallback, CompleteTest, effectiveCt);
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError("[UnlockTestService] Unhandled error in BT connection handler", ex);
                            }
                        });
                    };

                    await btListener.BindServiceNameAsync(
                        btProvider.ServiceId.AsString(),
                        SocketProtectionLevel.BluetoothEncryptionWithAuthentication);

                    InitSdpAttributes(btProvider, "locked");
                    btProvider.StartAdvertising(btListener, true);
                    Logger.Log($"[UnlockTestService] Bluetooth RFCOMM listener started and advertising as 'locked' for {device.Name}");
                }
                catch (Exception btEx)
                {
                    Logger.LogWarning($"[UnlockTestService] Could not start Bluetooth RFCOMM listener: {btEx.Message}");
                    if (!isNetworkSupported || app == null)
                    {
                        return new UnlockTestResult(
                            false,
                            string.Format(Localization.T("Failed to start Bluetooth service: {0}. Ensure Bluetooth is turned on."), btEx.Message),
                            stopwatch.ElapsedMilliseconds,
                            "Bluetooth",
                            device.Name);
                    }
                }
            }

            if (isBluetoothSupported && app != null)
            {
                statusCallback?.Invoke(Localization.T("Waiting for device response over Wi-Fi or Bluetooth..."));
            }
            else if (isBluetoothSupported)
            {
                statusCallback?.Invoke(Localization.T("Waiting for device response over Bluetooth..."));
            }
            else
            {
                statusCallback?.Invoke(Localization.T("Waiting for device response over Wi-Fi..."));
            }

            if (app != null)
            {
                await app.StartAsync(effectiveCt);
            }

            var result = await tcs.Task;

            ActivityJournal.Record(
                "unlock_test",
                result.IsSuccess ? "⚡" : "⚠️",
                result.IsSuccess ? "Connection test passed" : "Connection test failed",
                $"{device.Name}: {result.Message}",
                isSuccess: result.IsSuccess,
                deviceName: device.Name,
                transport: result.Transport);

            return result;
        }
        catch (OperationCanceledException)
        {
            var timeoutResult = new UnlockTestResult(false, Localization.T("Test timed out. The phone did not respond in time."), stopwatch.ElapsedMilliseconds, null, device.Name);
            ActivityJournal.Record("unlock_test", "⌛", "Connection test timed out", $"{device.Name} did not respond.", false, device.Name);
            return timeoutResult;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[UnlockTestService] Test failed for {device.Name}", ex);
            var errResult = new UnlockTestResult(false, $"{Localization.T("Error")}: {ex.Message}", stopwatch.ElapsedMilliseconds, null, device.Name);
            ActivityJournal.Record("unlock_test", "❌", "Connection test error", ex.Message, false, device.Name);
            return errResult;
        }
        finally
        {
            _mdns.Stop();
            if (btProvider != null)
            {
                try { btProvider.StopAdvertising(); } catch { }
            }
            if (btListener != null)
            {
                try { btListener.Dispose(); } catch { }
            }
            foreach (var s in activeSockets)
            {
                try { s.Abort(); s.Dispose(); } catch { }
            }
            foreach (var s in activeBtSockets)
            {
                try { s.Dispose(); } catch { }
            }
            if (app != null)
            {
                try
                {
                    using var shutdownCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await app.StopAsync(shutdownCts.Token);
                    await app.DisposeAsync();
                }
                catch { }
            }
        }
    }

    private static async Task HandleBtConnectionAsync(
        StreamSocket socket,
        DeviceModel device,
        PortalWinConfig config,
        string testRequestId,
        Stopwatch stopwatch,
        Action<string>? statusCallback,
        Action<UnlockTestResult> completeTest,
        CancellationToken ct)
    {
        string rawBtAddr = "Unknown BT";
        try
        {
            rawBtAddr = socket.Information.RemoteHostName?.DisplayName ?? "Unknown BT";
        }
        catch { }

        Logger.Log($"[UnlockTestService] Incoming RFCOMM connection from {rawBtAddr}");

        var btDevice = device as BluetoothDeviceModel;
        var expectedBtAddr = PortalWinConfig.NormalizeBluetoothAddress(btDevice?.BluetoothAddress);
        var incomingBtAddr = PortalWinConfig.NormalizeBluetoothAddress(rawBtAddr);

        bool macMatches = string.IsNullOrEmpty(expectedBtAddr) ||
                          string.IsNullOrEmpty(incomingBtAddr) ||
                          string.Equals(expectedBtAddr, incomingBtAddr, StringComparison.OrdinalIgnoreCase);

        try
        {
            var readStream = socket.InputStream.AsStreamForRead();
            var writeStream = socket.OutputStream.AsStreamForWrite();
            using var stream = new BtDuplexStream(readStream, writeStream);

            statusCallback?.Invoke(Localization.T("Bluetooth device connected, awaiting confirmation..."));

            BtMessage? firstMsg = null;
            try
            {
                using var idCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idCts.CancelAfter(TimeSpan.FromSeconds(3));
                firstMsg = await BtProtocol.ReceiveRawMessageAsync(stream, idCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.LogWarning($"[UnlockTestService] Initial BT read from {device.Name} ({rawBtAddr}): {ex.Message}");
            }

            var currentMsg = firstMsg;
            bool hostRequestSent = false;

            while (!ct.IsCancellationRequested)
            {
                if (currentMsg is BtUnlockRequest unlockReq)
                {
                    Logger.Log($"[UnlockTestService] Received BtUnlockRequest from client: {unlockReq.ClientId}");

                    bool clientMatches = string.IsNullOrWhiteSpace(unlockReq.ClientId) ||
                                         string.IsNullOrWhiteSpace(device.ClientId) ||
                                         string.Equals(unlockReq.ClientId, device.ClientId, StringComparison.OrdinalIgnoreCase);

                    if (clientMatches || macMatches)
                    {
                        var account = device.Accounts.FirstOrDefault();
                        var effectiveMode = account?.ResolveEffectiveUnlockMode(config.UnlockMode) ?? config.UnlockMode;

                        if (effectiveMode == UnlockMode.HostInitiated)
                        {
                            Logger.LogWarning($"[UnlockTestService] Client-initiated unlock rejected: account is configured for Host-Initiated only.");
                            await BtProtocol.SendMessageAsync(stream,
                                new BtUnlockResponse { Success = false, Error = "Client-initiated unlock disabled for this account" },
                                ct);

                            completeTest(new UnlockTestResult(
                                false,
                                Localization.T("Client-initiated unlock is disabled for this account (configured for Host-Initiated only)."),
                                stopwatch.ElapsedMilliseconds,
                                "Bluetooth",
                                device.Name));
                            return;
                        }

                        await BtProtocol.SendMessageAsync(stream, new BtUnlockResponse { Success = true }, ct);
                        stopwatch.Stop();
                        var latency = stopwatch.ElapsedMilliseconds;
                        Logger.Log($"[UnlockTestService] Bluetooth unlock test successful (Client-Initiated) for {device.Name} in {latency} ms");

                        completeTest(new UnlockTestResult(
                            true,
                            string.Format(Localization.T("Connection verified successfully! Latency: {0} ms"), latency),
                            latency,
                            "Bluetooth (Client-Initiated)",
                            device.Name));
                        return;
                    }
                    else
                    {
                        Logger.LogWarning($"[UnlockTestService] Client mismatch in BtUnlockRequest. Expected: {device.ClientId}, Got: {unlockReq.ClientId}");
                        await BtProtocol.SendMessageAsync(stream, new BtUnlockResponse { Success = false, Error = "Client mismatch" }, ct);
                        return;
                    }
                }
                else if (currentMsg is BtRegisterMessage regMsg)
                {
                    Logger.Log($"[UnlockTestService] Received BtRegisterMessage from client: {regMsg.ClientId}");
                    if (!hostRequestSent)
                    {
                        statusCallback?.Invoke(Localization.T("Please approve the unlock prompt on your phone..."));
                        var hostReq = new BtHostUnlockRequest
                        {
                            ClientId = device.ClientId,
                            RequestId = testRequestId
                        };
                        await BtProtocol.SendMessageAsync(stream, hostReq, ct);
                        hostRequestSent = true;
                        Logger.Log($"[UnlockTestService] Sent BtHostUnlockRequest to {device.Name} over Bluetooth");
                    }
                }
                else if (currentMsg is BtHostUnlockResponse hostResp)
                {
                    Logger.Log($"[UnlockTestService] Received BtHostUnlockResponse: status='{hostResp.Status}'");
                    var status = hostResp.Status;
                    if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                    {
                        stopwatch.Stop();
                        var latency = stopwatch.ElapsedMilliseconds;
                        Logger.Log($"[UnlockTestService] Bluetooth unlock approved by {device.Name} in {latency} ms");

                        completeTest(new UnlockTestResult(
                            true,
                            string.Format(Localization.T("Connection verified successfully! Latency: {0} ms"), latency),
                            latency,
                            "Bluetooth (Host-Initiated)",
                            device.Name));
                        return;
                    }
                    else
                    {
                        stopwatch.Stop();
                        var latency = stopwatch.ElapsedMilliseconds;
                        Logger.LogWarning($"[UnlockTestService] Bluetooth unlock rejected by {device.Name}: {status}");

                        completeTest(new UnlockTestResult(
                            false,
                            Localization.T("Biometric authentication was rejected or cancelled on the device."),
                            latency,
                            "Bluetooth",
                            device.Name));
                        return;
                    }
                }
                else if (currentMsg == null && !hostRequestSent)
                {
                    var account = device.Accounts.FirstOrDefault();
                    var effectiveMode = account?.ResolveEffectiveUnlockMode(config.UnlockMode) ?? config.UnlockMode;
                    if (effectiveMode != UnlockMode.ClientInitiated)
                    {
                        statusCallback?.Invoke(Localization.T("Please approve the unlock prompt on your phone..."));
                        var hostReq = new BtHostUnlockRequest
                        {
                            ClientId = device.ClientId,
                            RequestId = testRequestId
                        };
                        await BtProtocol.SendMessageAsync(stream, hostReq, ct);
                        hostRequestSent = true;
                        Logger.Log($"[UnlockTestService] Sent speculative BtHostUnlockRequest to {device.Name} over Bluetooth");
                    }
                }

                currentMsg = await BtProtocol.ReceiveRawMessageAsync(stream, ct);
                if (currentMsg == null)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogWarning($"[UnlockTestService] Bluetooth connection error for {device.Name}: {ex.Message}");
        }
        finally
        {
            try { socket.Dispose(); } catch { }
        }
    }

    private static void InitSdpAttributes(RfcommServiceProvider provider, string mode)
    {
        provider.SdpRawAttributes.Remove(0x100);
        provider.SdpRawAttributes.Remove(0x200);

        using var writer = new DataWriter();
        writer.WriteByte(0x25); // UTF-8 string type
        writer.WriteString(BtProtocol.SdpServiceName);
        provider.SdpRawAttributes.Add(0x100, writer.DetachBuffer());

        using var modeWriter = new DataWriter();
        modeWriter.WriteByte(0x25);
        modeWriter.WriteString(mode);
        provider.SdpRawAttributes.Add(0x200, modeWriter.DetachBuffer());
    }

    private static async Task<(WebSocketMessageType MessageType, string? Text)> ReceiveTextMessageAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            }
            catch
            {
                return (WebSocketMessageType.Close, null);
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, null);

            if (result.Count > 0)
                ms.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                var payload = Encoding.UTF8.GetString(ms.ToArray());
                return (result.MessageType, payload);
            }
        }

        return (WebSocketMessageType.Close, null);
    }
}
