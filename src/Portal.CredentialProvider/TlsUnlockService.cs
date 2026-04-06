using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Net.Security;
using System.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Portal.Common;
using Portal.Common.Models;
using Portal.CredentialProvider.Services;

namespace Portal.CredentialProvider;

public class TlsUnlockService : IDisposable
{
    private readonly PortalWinConfig _config;
    private readonly UnlockRequestHandler _unlockHandler;
    private readonly WebSocketConnectionManager _wsManager;
    private readonly MdnsAnnouncer _mdns;
    private readonly object _wsPolicyLock = new();

    private Microsoft.AspNetCore.Builder.WebApplication? _app;
    private bool _mdnsStarted;
    private string? _currentState;
    private X509Certificate2? _hostCert;
    private HashSet<string> _allowedWsClientCertHashes = new(StringComparer.OrdinalIgnoreCase);
    private string? _wsPolicyOwner;

    public string? StartupError { get; private set; }
    public bool IsRunning { get; private set; }

    public event Action<string, SecureString?, string>? UnlockRequested;
    public event Action<string, bool>? NetworkConnectionChanged;
    public event Action? StatusChanged;

    public TlsUnlockService(PortalWinConfig config)
        : this(config, new UnlockRequestHandler(config, new AttemptTracker()),
               new WebSocketConnectionManager(), new MdnsAnnouncer())
    {
    }

    public TlsUnlockService(
        PortalWinConfig config,
        UnlockRequestHandler unlockHandler,
        WebSocketConnectionManager wsManager,
        MdnsAnnouncer mdns)
    {
        _config = config;
        _unlockHandler = unlockHandler;
        _wsManager = wsManager;
        _mdns = mdns;

        _unlockHandler.UnlockRequested += (u, p, d) => UnlockRequested?.Invoke(u, p, d);
    }

    public void Start()
    {
        if (_app != null) return;

        try
        {
            var cert = LoadHostCertificate();
            if (cert == null)
            {
                StartupError = "No certificate found.";
                Logger.Log("[TlsUnlockService] No certificate found. Service aborting.");
                StatusChanged?.Invoke();
                return;
            }
            _hostCert = cert;

            var assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var rootPath = Path.GetDirectoryName(assemblyPath) ?? AppDomain.CurrentDomain.BaseDirectory;

            Logger.Log($"[TlsUnlockService] Starting Kestrel. RootPath: {rootPath}. Port: {_config.Port}");

            BuildApp(rootPath, cert);
            _ = StartKestrelWithRetryAsync();
        }
        catch (Exception ex)
        {
            StartupError = ex.Message;
            IsRunning = false;
            Logger.LogError("[TlsUnlockService] Failed to start TlsUnlockService", ex);
            StatusChanged?.Invoke();
        }
    }

    private void BuildApp(string rootPath, System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
    {
        var options = new WebApplicationOptions
        {
            ContentRootPath = rootPath,
            Args = Array.Empty<string>(),
            ApplicationName = "Portal.CredentialProvider"
        };

        var builder = WebApplication.CreateBuilder(options);

        builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions>(opts =>
        {
            opts.NoDelay = true;
        });

        _mdns.UseIpv4 = true;
        _mdns.UseIpv6 = true;

        ConfigureKestrel(builder, cert);
        builder.Services.AddLogging();
        _app = builder.Build();

        // НОВОЕ: Встроенный Middleware для моментальной блокировки неавторизованных сессий по отпечатку
        _app.Use(async (context, next) =>
        {
            var clientCert = context.Connection.ClientCertificate;
            if (clientCert == null)
            {
                Logger.LogWarning($"[Middleware] Connection rejected from {context.Connection.RemoteIpAddress}: No certificate.");
                context.Response.StatusCode = 401;
                return;
            }

            var certHash = CertificateService.GetCertHash(clientCert);
            var device = _config.FindDeviceByCertHash(certHash);

            if (device == null)
            {
                Logger.LogWarning($"[Middleware] Connection rejected from {context.Connection.RemoteIpAddress}: Unknown cert hash.");
                context.Response.StatusCode = 403;
                return;
            }

            if (string.Equals(context.Request.Path.Value, "/ws", StringComparison.OrdinalIgnoreCase)
                && !_config.DisableKestrelClientCertificateValidation
                && !IsWebSocketClientAllowed(certHash, out var policyOwner, out var allowedHashes))
            {
                LogWebSocketPolicyAdvisory(context, clientCert, certHash, device, policyOwner, allowedHashes);
            }

            context.Items["ValidatedDevice"] = device;
            await next(context);
        });

        MapEndpoints(_app);
    }

    private async Task StartKestrelWithRetryAsync()
    {
        const int maxRetries = 10;
        const int retryDelayMs = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (_app == null) return;

                await _app.StartAsync();
                IsRunning = true;
                StartupError = null;
                Logger.Log($"[TlsUnlockService] Kestrel started on port {_config.Port} (attempt {attempt})");
                StatusChanged?.Invoke();

                var advertiseIp = ResolveLocalIPv4();
                Logger.Log($"[TlsUnlockService] Resolved mDNS advertise IP: {advertiseIp ?? "(null)"}");

                if (advertiseIp != null)
                {
                    _mdns.Start(_config, "locked", advertiseIp);
                    Logger.Log($"[TlsUnlockService] mDNS started with IP {advertiseIp}");
                }
                else
                {
                    Logger.LogWarning("[TlsUnlockService] No valid IP yet (DHCP not ready?). mDNS deferred.");
                }

                _ = MonitorIpAndReAdvertiseAsync(advertiseIp);
                return;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[TlsUnlockService] Kestrel start attempt {attempt}/{maxRetries} failed", ex);
                if (attempt < maxRetries)
                {
                    try
                    {
                        if (_app != null)
                        {
                            await _app.DisposeAsync();
                            _app = null;
                        }
                    }
                    catch { }

                    await Task.Delay(retryDelayMs);
                    try
                    {
                        if (_hostCert != null)
                        {
                            var assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                            var rootPath = Path.GetDirectoryName(assemblyPath) ?? AppDomain.CurrentDomain.BaseDirectory;
                            BuildApp(rootPath, _hostCert);
                        }
                    }
                    catch (Exception rebuildEx)
                    {
                        Logger.LogError($"[TlsUnlockService] Failed to rebuild app on attempt {attempt}", rebuildEx);
                    }
                }
                else
                {
                    IsRunning = false;
                    StartupError = $"Failed to start Kestrel after {maxRetries} attempts: {ex.Message}";
                    Logger.LogError($"[TlsUnlockService] {StartupError}");
                    StatusChanged?.Invoke();
                }
            }
        }
    }

    public Task<string?> RequestUnlockFromClientAsync(Portal.Common.Models.DeviceModel device, string? requestId, bool correlationEnabled, CancellationToken ct)
    {
        if (!device.IsEnabled)
        {
            Logger.LogWarning($"[TlsUnlockService] Host-initiated unlock request blocked for disabled device: {device.IdsSafe()}");
            return Task.FromResult<string?>(null);
        }

        if (!_config.DisableKestrelClientCertificateValidation)
        {
            var certHash = device.CertHash?.Trim();
            if (string.IsNullOrWhiteSpace(certHash))
            {
                Logger.LogWarning($"[TlsUnlockService] Host-initiated request blocked: target device '{device.Name}' has empty cert hash.");
                return Task.FromResult<string?>(null);
            }

            if (!IsWebSocketClientAllowed(certHash, out var policyOwner, out var allowedHashes))
            {
                var usersByHash = GetUsersForDevice(device);
                var usersByTile = GetUsersByAllowedHashes(allowedHashes);
                Logger.LogWarning(
                    $"[TlsUnlockService] Host-initiated request blocked by strict tile policy. Device='{device.Name}' ClientId='{device.ClientId}' CertHash='{certHash}' TileOwner='{policyOwner ?? "none"}' UsersByHash='{FormatSetForLog(usersByHash)}' UsersByTile='{FormatSetForLog(usersByTile)}' AllowedTileHashes='{FormatSetForLog(allowedHashes)}' AvailablePinnedCertificates='{BuildAvailableCertificatesLog()}'.");
                return Task.FromResult<string?>(null);
            }
        }

        return _wsManager.RequestUnlockFromClientAsync(device, requestId, correlationEnabled, ct);
    }

    public bool IsNetworkClientConnected(string clientId)
    {
        return _wsManager.IsClientConnected(clientId);
    }

    public int GetConnectedClientCount()
    {
        return _wsManager.GetConnectedClientCount();
    }

    internal void NotifyConnectionChanged(string clientId, bool connected)
    {
        NetworkConnectionChanged?.Invoke(clientId, connected);
        StatusChanged?.Invoke();
    }

    public void SetHostInitiatedWebSocketPolicy(string ownerKey, IEnumerable<string> certHashes)
    {
        var normalizedOwner = string.IsNullOrWhiteSpace(ownerKey) ? "unknown" : ownerKey.Trim();
        var normalizedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var certHash in certHashes)
        {
            if (!string.IsNullOrWhiteSpace(certHash))
            {
                normalizedHashes.Add(certHash.Trim());
            }
        }

        var changed = false;
        lock (_wsPolicyLock)
        {
            changed = !string.Equals(_wsPolicyOwner, normalizedOwner, StringComparison.OrdinalIgnoreCase)
                || !_allowedWsClientCertHashes.SetEquals(normalizedHashes);
            if (!changed)
            {
                return;
            }

            _wsPolicyOwner = normalizedOwner;
            _allowedWsClientCertHashes = normalizedHashes;
        }

        var sessionHandlingText = _config.StrictSelectedTileWebSocketConnections
            ? "Mismatched existing WS sessions will be disconnected; request routing remains strict."
            : "Existing WS sessions are preserved; request routing remains strict.";

        if (_config.DisableKestrelClientCertificateValidation)
        {
            Logger.LogWarning($"[TlsUnlockService] WS tile policy updated for '{normalizedOwner}', legacy mode is enabled. {sessionHandlingText} Allowed hashes: {normalizedHashes.Count}. UsersByTile='{FormatSetForLog(GetUsersByAllowedHashes(normalizedHashes))}'. AvailablePinnedCertificates='{BuildAvailableCertificatesLog()}'.");
        }
        else
        {
            Logger.Log($"[TlsUnlockService] WS tile policy updated for '{normalizedOwner}'. {sessionHandlingText} Allowed hashes: {normalizedHashes.Count}. UsersByTile='{FormatSetForLog(GetUsersByAllowedHashes(normalizedHashes))}'. AvailablePinnedCertificates='{BuildAvailableCertificatesLog()}'.");
        }

        if (_config.StrictSelectedTileWebSocketConnections && normalizedHashes.Count > 0)
        {
            var allowedClientIds = GetClientIdsByAllowedHashes(normalizedHashes);
            _ = _wsManager.DisconnectClientsExceptAsync(
                allowedClientIds,
                CancellationToken.None,
                $"Strict selected-tile policy for '{normalizedOwner}'").ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        Logger.LogWarning($"[TlsUnlockService] Failed to disconnect mismatched WS clients for strict selected-tile policy. Owner='{normalizedOwner}'. Error='{t.Exception.GetBaseException().Message}'.");
                        return;
                    }

                    if (t.Status == TaskStatus.RanToCompletion && t.Result.Count > 0)
                    {
                        Logger.Log($"[TlsUnlockService] Strict selected-tile policy disconnected {t.Result.Count} mismatched WS client(s) for owner '{normalizedOwner}'.");
                    }
                }, TaskScheduler.Default);
        }
    }

    public void DisconnectAllWebSocketClients(string reason)
    {
        _wsManager.ResetPendingApprovals(reason);
        _ = _wsManager.DisconnectAllClientsAsync(reason, CancellationToken.None).ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                Logger.LogWarning($"[TlsUnlockService] Failed to disconnect all WS clients. Reason='{reason}'. Error: {t.Exception.GetBaseException().Message}");
            }
        }, TaskScheduler.Default);
    }

    public void UpdateState(string state)
    {
        try
        {
            if (_currentState == state && IsRunning && _mdnsStarted)
            {
                _ = _wsManager.BroadcastStateUpdate(state);
                return;
            }
            _currentState = state;
            _mdnsStarted = true;

            var ip = ResolveLocalIPv4();
            if (ip != null)
            {
                _mdns.Start(_config, state, ip);
                Logger.Log($"[TlsUnlockService] mDNS updated state: {state}");
            }
            _ = _wsManager.BroadcastStateUpdate(state);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[TlsUnlockService] Failed to update state: {ex.Message}");
        }
    }

    public void Dispose()
    {
        IsRunning = false;
        _mdns.Dispose();
        if (_app != null)
        {
            _app.StopAsync().Wait();
            _app.DisposeAsync().AsTask().Wait();
        }
        _hostCert?.Dispose();
        StatusChanged?.Invoke();
        Logger.Log("TlsUnlockService disposed.");
    }

    private void ConfigureKestrel(WebApplicationBuilder builder, X509Certificate2 cert)
    {
        builder.WebHost.UseKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(_config.Port, listenOptions =>
            {
                Logger.Log($"[TlsUnlockService] Configuring HTTPS on port {_config.Port}. Thumbprint: {cert.Thumbprint}");
                listenOptions.UseHttps(https =>
                {
                    https.ServerCertificate = cert;
                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;

                    if (_config.DisableKestrelClientCertificateValidation)
                    {
                        Logger.LogWarning("[TlsUnlockService] Kestrel client certificate validation is DISABLED by config (disableKestrelClientCertificateValidation=true). Middleware validation remains active.");
                        https.AllowAnyClientCertificate();
                    }
                    else
                    {
                        https.ClientCertificateValidation = ValidateClientCertificateForTls;
                    }
                });
            });
        });
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapPost("/api/unlock", async (UnlockRequest request, HttpContext context) =>
        {
            return _unlockHandler.HandleUnlockRequest(request, context);
        });

        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(2)
        });

        app.Map("/ws", async (HttpContext context) =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var device = _unlockHandler.ValidateWebSocketClient(context);
                if (device == null)
                {
                    context.Response.StatusCode = 403;
                    return;
                }

                if (_config.StrictSelectedTileWebSocketConnections
                    && !IsWebSocketConnectionAllowedForSelectedTile(device, out var rejectReason))
                {
                    Logger.LogWarning($"[TlsUnlockService] Rejected WS connection by strict selected-tile policy. Device='{device.Name}' ClientId='{device.ClientId}' Reason='{rejectReason}'.");
                    context.Response.StatusCode = 403;
                    return;
                }

                using var ws = await context.WebSockets.AcceptWebSocketAsync();
                _wsManager.RegisterClient(device.ClientId, ws);
                NotifyConnectionChanged(device.ClientId, true);
                Logger.Log($"[TlsUnlockService] WebSocket connected: {device.Name} ({device.ClientId})");

                try
                {
                    await _wsManager.HandleWebSocketAsync(ws, device.ClientId);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[TlsUnlockService] WebSocket error for {device.Name}", ex);
                }
                finally
                {
                    _wsManager.UnregisterClient(device.ClientId, ws);
                    NotifyConnectionChanged(device.ClientId, false);
                    Logger.Log($"[TlsUnlockService] WebSocket disconnected: {device.Name}");
                }
            }
            else
            {
                context.Response.StatusCode = 400;
            }
        });
    }

    private bool ValidateClientCertificateForTls(X509Certificate2? clientCert, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        try
        {
            if (clientCert == null)
            {
                Logger.LogWarning("[TlsUnlockService] TLS validation rejected: client certificate is missing.");
                return false;
            }

            if (!IsWithinValidityPeriod(clientCert))
            {
                Logger.LogWarning($"[TlsUnlockService] TLS validation rejected: certificate is outside validity period. Subject='{clientCert.Subject}'.");
                return false;
            }

            if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            {
                Logger.LogWarning($"[TlsUnlockService] TLS validation rejected: remote certificate not available. Errors='{sslPolicyErrors}'.");
                return false;
            }

            // Client certificates are self-signed in this flow.
            // Trust is enforced via explicit cert-hash pinning below, so we don't reject on CA/chain errors.
            if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            {
                var statuses = chain == null
                    ? "(no chain data)"
                    : string.Join(", ", chain.ChainStatus.Select(s => s.Status.ToString()));
                Logger.LogWarning($"[TlsUnlockService] TLS validation continuing despite chain errors for self-signed client cert. Subject='{clientCert.Subject}', Statuses='{statuses}'.");
            }

            var certHash = CertificateService.GetCertHash(clientCert);
            var pinnedDevice = _config.FindDeviceByCertHash(certHash);
            if (pinnedDevice == null)
            {
                Logger.LogWarning($"[TlsUnlockService] TLS validation rejected: cert hash '{certHash}' is not pinned in config. Available pinned certificates: {BuildAvailableCertificatesLog()}.");
                return false;
            }

            if (!IsClientCertificateSuitableForAuthentication(clientCert))
            {
                Logger.LogWarning($"[TlsUnlockService] TLS validation compatibility mode: EKU/usage is non-standard but cert hash is pinned. Allowing. Subject='{clientCert.Subject}', CertHash='{certHash}', Device='{pinnedDevice.Name}', UsersByHash='{BuildUsersByHashLog(certHash)}'.");
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError("[TlsUnlockService] TLS validation callback failed with exception.", ex);
            return false;
        }
    }

    private bool IsWebSocketClientAllowed(string certHash, out string? policyOwner, out HashSet<string> allowedHashesSnapshot)
    {
        lock (_wsPolicyLock)
        {
            policyOwner = _wsPolicyOwner;
            allowedHashesSnapshot = new HashSet<string>(_allowedWsClientCertHashes, StringComparer.OrdinalIgnoreCase);
            if (allowedHashesSnapshot.Count == 0)
            {
                return false;
            }

            return allowedHashesSnapshot.Contains(certHash);
        }
    }

    private bool IsWebSocketConnectionAllowedForSelectedTile(DeviceModel device, out string reason)
    {
        reason = "Allowed";

        var certHash = device.CertHash?.Trim();
        if (string.IsNullOrWhiteSpace(certHash))
        {
            reason = "Device cert hash is empty.";
            return false;
        }

        var allowed = IsWebSocketClientAllowed(certHash, out var policyOwner, out var allowedHashes);
        if (allowed)
        {
            return true;
        }

        if (allowedHashes.Count == 0)
        {
            reason = $"No active selected-tile certificate policy for owner '{policyOwner ?? "none"}'.";
            return true;
        }

        reason = $"Client certificate hash is not allowed for selected tile owner '{policyOwner ?? "none"}'.";
        return false;
    }

    private HashSet<string> GetClientIdsByAllowedHashes(HashSet<string> allowedHashes)
    {
        var allowedClientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hash in allowedHashes)
        {
            var device = _config.FindDeviceByCertHash(hash);
            if (device != null && !string.IsNullOrWhiteSpace(device.ClientId))
            {
                allowedClientIds.Add(device.ClientId);
            }
        }

        return allowedClientIds;
    }

    private void LogWebSocketPolicyAdvisory(
        HttpContext context,
        X509Certificate2 clientCert,
        string certHash,
        DeviceModel matchedByHashDevice,
        string? policyOwner,
        HashSet<string> allowedHashes)
    {
        var usersByHash = GetUsersForDevice(matchedByHashDevice);
        var usersByTile = GetUsersByAllowedHashes(allowedHashes);
        var usersDiffer = usersByHash.Count == 0 || usersByTile.Count == 0
            ? true
            : !usersByHash.Overlaps(usersByTile);

        var reason = allowedHashes.Count == 0
            ? "Strict tile policy has no allowed hashes (no active tile context or no mapped devices)."
            : "Client certificate hash is not in current tile policy set.";

        Logger.LogWarning(
            $"[TlsUnlockService] WS strict-policy mismatch (connection kept for stability; request routing remains strict). Reason='{reason}' Remote='{context.Connection.RemoteIpAddress}' Subject='{clientCert.Subject}' CertHash='{certHash}' TileOwner='{policyOwner ?? "none"}' HashInTileSet={allowedHashes.Contains(certHash)} UsersByHash='{FormatSetForLog(usersByHash)}' UsersByTile='{FormatSetForLog(usersByTile)}' UsersDiffer={usersDiffer} AllowedTileHashes='{FormatSetForLog(allowedHashes)}' AvailablePinnedCertificates='{BuildAvailableCertificatesLog()}'.");
    }

    private string BuildUsersByHashLog(string certHash)
    {
        var users = GetUsersForDevice(_config.FindDeviceByCertHash(certHash));
        return FormatSetForLog(users);
    }

    private HashSet<string> GetUsersForDevice(DeviceModel? device)
    {
        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (device == null)
        {
            return users;
        }

        foreach (var account in device.Accounts)
        {
            var identity = BuildAccountIdentity(account);
            if (!string.IsNullOrWhiteSpace(identity))
            {
                users.Add(identity);
            }
        }

        return users;
    }

    private HashSet<string> GetUsersByAllowedHashes(HashSet<string> allowedHashes)
    {
        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allowedHashes.Count == 0)
        {
            return users;
        }

        foreach (var device in _config.Devices)
        {
            if (string.IsNullOrWhiteSpace(device.CertHash) || !allowedHashes.Contains(device.CertHash))
            {
                continue;
            }

            foreach (var account in device.Accounts)
            {
                var identity = BuildAccountIdentity(account);
                if (!string.IsNullOrWhiteSpace(identity))
                {
                    users.Add(identity);
                }
            }
        }

        return users;
    }

    private string BuildAvailableCertificatesLog()
    {
        var entries = _config.Devices
            .Where(device => !string.IsNullOrWhiteSpace(device.CertHash))
            .Select(device =>
            {
                var users = GetUsersForDevice(device);
                return $"{device.Name}({device.ClientId})#{device.CertHash}=>[{FormatSetForLog(users)}]";
            })
            .ToList();

        return entries.Count == 0 ? "(none)" : string.Join("; ", entries);
    }

    private static string BuildAccountIdentity(DeviceAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.Username))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(account.Domain))
        {
            return account.Username.Trim();
        }

        return $"{account.Domain.Trim()}\\{account.Username.Trim()}";
    }

    private static string FormatSetForLog(IEnumerable<string> values)
    {
        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized.Count == 0 ? "(none)" : string.Join(", ", normalized);
    }

    private static bool IsWithinValidityPeriod(X509Certificate2 cert)
    {
        var nowUtc = DateTime.UtcNow;
        var notBeforeUtc = cert.NotBefore.ToUniversalTime();
        var notAfterUtc = cert.NotAfter.ToUniversalTime();
        return nowUtc >= notBeforeUtc && nowUtc <= notAfterUtc;
    }

    private static bool IsClientCertificateSuitableForAuthentication(X509Certificate2 cert)
    {
        const string clientAuthOid = "1.3.6.1.5.5.7.3.2";
        const string anyExtendedKeyUsageOid = "2.5.29.37.0";

        var ekuExtensions = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToList();
        if (ekuExtensions.Count > 0)
        {
            var hasAllowedEku = false;
            foreach (var ekuExtension in ekuExtensions)
            {
                if (ekuExtension.EnhancedKeyUsages
                    .Cast<System.Security.Cryptography.Oid>()
                    .Any(oid =>
                        string.Equals(oid.Value, clientAuthOid, StringComparison.Ordinal) ||
                        string.Equals(oid.Value, anyExtendedKeyUsageOid, StringComparison.Ordinal)))
                {
                    hasAllowedEku = true;
                    break;
                }
            }

            if (!hasAllowedEku)
            {
                return false;
            }
        }

        var keyUsageExtension = cert.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (keyUsageExtension == null)
        {
            return true;
        }

        var allowedUsages = X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement | X509KeyUsageFlags.KeyEncipherment;
        return (keyUsageExtension.KeyUsages & allowedUsages) != 0;
    }

    private static X509Certificate2? LoadHostCertificate()
    {
        try
        {
            var pfxPath = Path.Combine(PortalWinConfig.ConfigDir, "host_cert.pfx");
            if (!File.Exists(pfxPath))
            {
                Logger.Log("Host certificate PFX not found: " + pfxPath);
                return null;
            }
            return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, "portalwin-host",
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
        }
        catch (Exception ex)
        {
            Logger.LogError("LoadHostCertificate error", ex);
            return null;
        }
    }

    private string? ResolveLocalIPv4()
    {
        var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsAdapterEligibleForAdvertise)
            .ToList();

        var best = interfaces.FirstOrDefault(ni => ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet)
                   ?? interfaces.FirstOrDefault(ni => ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                   ?? interfaces.FirstOrDefault();

        if (best == null) return null;

        return best.GetIPProperties().UnicastAddresses
            .Where(ua => ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(ua => ua.Address)
            .Where(ip => !IsLinkLocalOrLoopback(ip))
            .Select(ip => ip.ToString())
            .FirstOrDefault();
    }

    private static bool IsLinkLocalOrLoopback(System.Net.IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return true;
        if (bytes[0] == 169 && bytes[1] == 254) return true;
        if (bytes[0] == 127) return true;
        return false;
    }

    private bool IsAdapterEligibleForAdvertise(System.Net.NetworkInformation.NetworkInterface ni)
    {
        if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
            ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
        {
            return false;
        }

        if (!_config.VpnCompatibilityModeEnabled)
        {
            return true;
        }

        if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        return !ContainsAny(ni.Name, ni.Description, ni.Id,
            "Virtual",
            "vEthernet",
            "VMware",
            "VirtualBox",
            "Hyper-V",
            "Tailscale",
            "ZeroTier",
            "GlobalProtect",
            "Fortinet",
            "AnyConnect",
            "OpenVPN",
            "WireGuard",
            "Nord",
            "ProtonVPN",
            "Radmin",
            "NDIS",
            "Wintun",
            "TAP",
            "PPP");
    }

    private static bool ContainsAny(string? name, string? description, string? id, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if ((!string.IsNullOrWhiteSpace(name) && name.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(description) && description.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(id) && id.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task MonitorIpAndReAdvertiseAsync(string? initialIp)
    {
        try
        {
            if (initialIp != null) return;

            int attempt = 0;
            while (true)
            {
                await Task.Delay(1000);
                attempt++;
                var currentIp = ResolveLocalIPv4();

                if (currentIp != null)
                {
                    Logger.Log($"[TlsUnlockService] Valid IP obtained: {currentIp}. Starting mDNS (after {attempt} checks).");
                    _mdns.Start(_config, "locked", currentIp);
                    return;
                }

                if (attempt % 12 == 0)
                    Logger.Log($"[TlsUnlockService] IP monitor: {attempt} checks, still no valid IP. Continuing...");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[TlsUnlockService] IP monitor error: {ex.Message}");
        }
    }
}
