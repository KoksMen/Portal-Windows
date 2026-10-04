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
        var tcs = new TaskCompletionSource<UnlockTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetResult(
            new UnlockTestResult(false, Localization.T("Test cancelled or timed out."), stopwatch.ElapsedMilliseconds, null, device.Name)));

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
            app.UseWebSockets();

            var testRequestId = Guid.NewGuid().ToString("N");

            // 1. Direct REST unlock test endpoint
            app.MapPost("/api/unlock", (UnlockRequest request, HttpContext context) =>
            {
                var clientCert = context.Connection.ClientCertificate;
                var clientThumbprint = clientCert?.Thumbprint ?? string.Empty;

                Logger.Log($"[UnlockTestService] Received /api/unlock request from client: {request.ClientId}, Cert: {clientThumbprint}");

                bool isCertMatched = string.IsNullOrEmpty(device.CertHash) ||
                                     string.Equals(device.CertHash, clientThumbprint, StringComparison.OrdinalIgnoreCase);

                if (isCertMatched && (string.Equals(device.ClientId, request.ClientId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(request.ClientId)))
                {
                    stopwatch.Stop();
                    var latency = stopwatch.ElapsedMilliseconds;
                    Logger.Log($"[UnlockTestService] REST unlock test successful for {device.Name} in {latency} ms");

                    tcs.TrySetResult(new UnlockTestResult(
                        true,
                        string.Format(Localization.T("Connection verified successfully! Latency: {0} ms"), latency),
                        latency,
                        "Wi-Fi (REST)",
                        device.Name));

                    return Results.Ok(new UnlockResponse(true, null));
                }

                Logger.LogWarning($"[UnlockTestService] Certificate mismatch or invalid client ID. Expected: {device.CertHash}");
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
                var clientThumbprint = clientCert?.Thumbprint ?? string.Empty;

                bool isCertMatched = string.IsNullOrEmpty(device.CertHash) ||
                                     string.Equals(device.CertHash, clientThumbprint, StringComparison.OrdinalIgnoreCase);

                if (!isCertMatched)
                {
                    Logger.LogWarning($"[UnlockTestService] WS rejected: cert hash mismatch. Got {clientThumbprint}, expected {device.CertHash}");
                    context.Response.StatusCode = 403;
                    return;
                }

                using var ws = await context.WebSockets.AcceptWebSocketAsync();
                Logger.Log($"[UnlockTestService] WebSocket accepted from {device.Name}. Sending test unlock request...");

                // Send test unlock request over WS
                var reqMsg = new WsMessage("unlock_request", device.ClientId, "test", testRequestId);
                var sendBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reqMsg));
                await ws.SendAsync(new ArraySegment<byte>(sendBytes), WebSocketMessageType.Text, true, ct);

                // Await response
                var buffer = new byte[4096];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var recvResult = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (recvResult.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    var json = Encoding.UTF8.GetString(buffer, 0, recvResult.Count);
                    Logger.Log($"[UnlockTestService] WS message received: {json}");

                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        var status = root.TryGetProperty("status", out var sProp) ? sProp.GetString() : null;
                        var type = root.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;

                        if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(type, "unlock_approved", StringComparison.OrdinalIgnoreCase))
                        {
                            stopwatch.Stop();
                            var latency = stopwatch.ElapsedMilliseconds;

                            tcs.TrySetResult(new UnlockTestResult(
                                true,
                                string.Format(Localization.T("Connection verified successfully! Latency: {0} ms"), latency),
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

            await app.StartAsync(ct);
            statusCallback?.Invoke(Localization.T("Please approve the unlock prompt on your phone..."));

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
            if (app != null)
            {
                try
                {
                    await app.StopAsync(CancellationToken.None);
                    await app.DisposeAsync();
                }
                catch { }
            }
        }
    }
}
