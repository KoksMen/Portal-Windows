using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Portal.Common;
using Portal.Host.Models;
using Portal.Host.Services;

namespace Portal.Host.Services;

public class NetworkPairingService
{
    private WebApplication? _app;
    private readonly MdnsAnnouncer _mdns = new();
    private readonly AttemptTracker _attemptTracker = new();
    private readonly NetworkService _networkService;
    private PairingContext? _pairingContext;
    private Action<string>? _pairingStatusCallback;
    private TaskCompletionSource<PairingResult?>? _pairingTcs;

    public NetworkPairingService(NetworkService networkService)
    {
        _networkService = networkService;
    }

    public async Task StartListener(PortalWinConfig config, X509Certificate2 cert)
    {
        if (_app != null) return;

        Logger.Log($"[NetworkPairingService] Starting Kestrel listener on port {config.Port}...");
        Logger.Log("[NetworkPairingService] Preparing mDNS advertise (mode=pair)...");
        Logger.Log($"[NetworkPairingService] Using certificate: {cert.Thumbprint}");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.ListenAnyIP(config.Port, listenOptions =>
            {
                Logger.Log($"[NetworkPairingService] Configuring HTTPS on port {config.Port} with mTLS (RequireCertificate).");
                listenOptions.UseHttps(adapterOptions =>
                {
                    adapterOptions.ServerCertificate = cert;
                    adapterOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    adapterOptions.AllowAnyClientCertificate();
                });
            });
        });

        builder.Services.AddLogging();
        _app = builder.Build();

        // ONLY the Pairing Endpoint
        _app.MapPost("/api/pair", async (PairRequest request, HttpContext context) =>
        {
            return HandlePairRequest(request, context, config);
        });

        await _app.StartAsync();
        Logger.Log($"[NetworkPairingService] Listening on {config.Port}");

        var ips = await _networkService.GetLocalIPsAsync(config.VpnCompatibilityModeEnabled);
        // Prefer an explicit IPv4 for mDNS advertise to avoid interface ambiguity in user session.
        var advertiseIp = ips.FirstOrDefault(ip => System.Net.IPAddress.TryParse(ip, out var addr) && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        _mdns.Start(config, "pair", advertiseIp);
    }

    public async Task StopListener()
    {
        _mdns.Stop();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
            Logger.Log("[NetworkPairingService] Stopped.");
        }
    }

    public Task<PairingResult?> StartPairing(PairingContext context, Action<string> statusCallback, CancellationToken ct)
    {
        _pairingContext = context;
        _pairingStatusCallback = statusCallback;
        _pairingTcs = new TaskCompletionSource<PairingResult?>(TaskCreationOptions.RunContinuationsAsynchronously);

        ct.Register(() => _pairingTcs.TrySetCanceled());

        return _pairingTcs.Task;
    }

    public void StopPairing()
    {
        _pairingContext = null;
        _pairingTcs?.TrySetCanceled();
    }

    public string GeneratePairingCode()
    {
        return Random.Shared.Next(100000, 999999).ToString();
    }

    private IResult HandlePairRequest(PairRequest request, HttpContext context, PortalWinConfig config)
    {
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
        Logger.Log($"Pairing request received from {clientIp}...");

        if (_attemptTracker.IsBlocked(clientIp))
        {
            Logger.LogWarning($"Pairing rejected for {clientIp}: Too many failed attempts.");
            return Results.Json(new { error = "Too many requests" }, statusCode: 429);
        }

        if (_pairingContext == null || string.IsNullOrEmpty(_pairingContext.PairingCode) || request.Code != _pairingContext.PairingCode)
        {
            Logger.LogWarning($"Pairing rejected for {clientIp}: Invalid code.");
            _attemptTracker.RecordFailure(clientIp);
            return Results.Json(new { error = "Invalid code or pairing not active" }, statusCode: 403);
        }

        try
        {
            var clientCert = context.Connection.ClientCertificate;
            if (clientCert == null)
            {
                Logger.LogWarning($"Pairing rejected for {clientIp}: No client certificate.");
                _attemptTracker.RecordFailure(clientIp);
                return Results.Json(new { error = "No client certificate" }, statusCode: 401);
            }

            _attemptTracker.RecordSuccess(clientIp);

            var clientCertHash = CertificateService.GetCertHash(clientCert);
            var clientId = Guid.NewGuid().ToString();

            _pairingStatusCallback?.Invoke($"Connection from {clientIp}");

            var device = new Portal.Common.Models.NetworkDeviceModel
            {
                ClientId = clientId,
                Name = $"Device {config.Devices.Count + 1}",
                CertHash = clientCertHash,
                IpAddress = clientIp,
                ClientPort = 29171,
                PairedAt = DateTime.Now,
                TransportType = TransportType.Network
            };

            if (_pairingContext != null && !string.IsNullOrEmpty(_pairingContext.TargetUsername))
            {
                if (config.EnforceUniqueAccountPerTransport &&
                    config.HasPairedAccountForTransport(_pairingContext.TargetUsername, _pairingContext.TargetDomain, TransportType.Network))
                {
                    Logger.LogWarning($"Pairing rejected for {clientIp}: Account already linked to another device.");
                    _pairingStatusCallback?.Invoke("Pairing rejected: account already linked.");
                    return Results.Json(new { error = "Account already paired" }, statusCode: 409);
                }
                if (config.EnforceUniqueAccountPerTransport &&
                    config.EnforceUniqueAccountAcrossTransports &&
                    config.HasPairedAccountOnOtherTransport(_pairingContext.TargetUsername, _pairingContext.TargetDomain, TransportType.Network))
                {
                    Logger.LogWarning($"Pairing rejected for {clientIp}: Account already linked on another transport.");
                    _pairingStatusCallback?.Invoke("Pairing rejected: account already linked on another transport.");
                    return Results.Json(new { error = "Account already paired on another transport" }, statusCode: 409);
                }

                var newAccount = new Portal.Common.Models.DeviceAccount
                {
                    Username = _pairingContext.TargetUsername,
                    Domain = _pairingContext.TargetDomain ?? ""
                };
                newAccount.SetPassword(_pairingContext.TargetPassword);
                device.Accounts.Add(newAccount);
            }

            config.Devices.Add(device);
            config.Save();

            var selectedHostIp = _pairingContext?.HostIpAddress;
            var hostMacAddress = _networkService.GetMacAddressForIp(selectedHostIp, config.VpnCompatibilityModeEnabled);

            if (string.IsNullOrWhiteSpace(hostMacAddress))
            {
                Logger.LogWarning($"[NetworkPairingService] Pair success for clientId '{clientId}', but MAC address could not be resolved for selected host IP '{selectedHostIp ?? "<null>"}'.");
            }
            else
            {
                Logger.Log($"[NetworkPairingService] Pair success for clientId '{clientId}'. Selected host IP '{selectedHostIp}', returning MAC '{hostMacAddress}'.");
            }

            Logger.Log($"Pairing successful! Device: {device.Name} ({clientId})");
            _pairingStatusCallback?.Invoke($"Pairing successful! Device: {device.Name}");
            _pairingTcs?.TrySetResult(new PairingResult { Device = device, Success = true });
            _pairingContext = null;

            return Results.Ok(new PairResponse(clientId, hostMacAddress));
        }
        catch (Exception ex)
        {
            Logger.LogError("Pairing error", ex);
            return Results.Json(new { error = "Internal error" }, statusCode: 500);
        }
    }
}
