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

        void CompleteTest(UnlockTestResult testResult)
        {
            if (tcs.TrySetResult(testResult))
            {
                try { timeoutCts.Cancel(); } catch { }
                foreach (var s in activeSockets)
                {
                    try { s.Abort(); } catch { }
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

        try
        {
            var cert = _certManager.CreateOrLoadCertificate(config);
            if (cert == null)
            {
                return new UnlockTestResult(false, Localization.T("No host certificate found."), 0, null, device.Name);
            }

            // Start mDNS advertising as 'locked' so the phone's auto-discovery picks up the PC
            _mdns.Start(config, mode: "locked");
            statusCallback?.Invoke(Localization.T("Waiting for device response..."));

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

            var testRequestId = Guid.NewGuid().ToString("N");

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

            await app.StartAsync(effectiveCt);

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
            foreach (var s in activeSockets)
            {
                try { s.Abort(); s.Dispose(); } catch { }
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
