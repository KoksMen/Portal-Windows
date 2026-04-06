using Lithnet.CredentialProvider;
using Microsoft.Win32;
using Portal.Common;
using Portal.Common.Helpers;
using Portal.CredentialProvider.Base;
using Portal.CredentialProvider.Services;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Portal.CredentialProvider;

public class PortalWinTile : PortalWinTileBase
{
    private CancellationTokenSource? _activeRequestCts;
    private static readonly object _requestSync = new();
    private static readonly object _tilesSync = new();
    private static readonly HashSet<PortalWinTile> _tiles = new();
    private static CancellationTokenSource? _globalActiveRequestCts;
    private static string? _globalActiveOwner;
    private bool _isRegisteredInTiles;

    private PortalWinProvider Provider => (PortalWinProvider)_providerBase;
    public bool AllowsHostInitiated => Provider.UnlockMode == UnlockMode.HostInitiated || Provider.UnlockMode == UnlockMode.Both;

    public PortalWinTile(PortalWinProviderBase provider) : base(provider) { }
    public PortalWinTile(PortalWinProviderBase provider, CredentialProviderUser user) : base(provider, user) { }

    public override void Initialize()
    {
        base.Initialize();
        RegisterTileInstance();

        if (_requestButton != null)
        {
            _requestButton.OnClick = OnRequestUnlockClicked;
        }

        if (_cancelButton != null)
            _cancelButton.OnClick = OnCancelUnlockClicked;

        ShowRequestButton();
        TryEarlyAutoRequestUnlock();
    }

    protected override void OnSelected()
    {
        base.OnSelected();
        CancelHostInitiatedRequestsOnOtherTiles();
        ApplyHostInitiatedTlsPolicy(PortalWinConfig.Load(), "selected");
        // Explicit tile selection must win over any provisional/early request
        // started before LogonUI finished selecting the user tile.
        TryAutoRequestUnlock(forceTakeover: true, source: "selected");
    }

    private void RegisterTileInstance()
    {
        if (_isRegisteredInTiles)
        {
            return;
        }

        lock (_tilesSync)
        {
            _tiles.Add(this);
        }

        _isRegisteredInTiles = true;
    }

    private void CancelHostInitiatedRequestsOnOtherTiles()
    {
        PortalWinTile[] snapshot;

        lock (_tilesSync)
        {
            snapshot = _tiles.ToArray();
        }

        foreach (var tile in snapshot)
        {
            if (ReferenceEquals(tile, this))
            {
                continue;
            }

            tile.CancelHostInitiatedRequestByTileSwitch();
        }
    }

    private void CancelHostInitiatedRequestByTileSwitch()
    {
        if (_activeRequestCts == null || _activeRequestCts.IsCancellationRequested)
        {
            return;
        }

        _activeRequestCts.Cancel();
        UpdateStatus("Request cancelled.");
        ShowRequestButton();
    }

    private void TryEarlyAutoRequestUnlock()
    {
        // Lock screen curtain can delay OnSelected; for likely default user tile we start early.
        if (!ShouldAttemptEarlyStart()) return;

        Logger.Log($"[PortalWinTile] Early auto-start candidate detected for '{User?.QualifiedUserName ?? User?.UserName ?? "Unknown"}'.");
        ApplyHostInitiatedTlsPolicy(PortalWinConfig.Load(), "initialize");
        TryAutoRequestUnlock(forceTakeover: false, source: "initialize");
    }

    private bool ShouldAttemptEarlyStart()
    {
        if (!AllowsHostInitiated) return false;

        var trigger = Provider.HostRequestTrigger;
        if (trigger == HostRequestTrigger.OnClick) return false;

        var scenario = Provider.UsageScenario;
        bool shouldAutoRequest = trigger switch
        {
            HostRequestTrigger.OnClickAndStartup => scenario == Lithnet.CredentialProvider.UsageScenario.Logon,
            HostRequestTrigger.OnClickAndAnyLockScreen =>
                scenario == Lithnet.CredentialProvider.UsageScenario.Logon
                || scenario == Lithnet.CredentialProvider.UsageScenario.UnlockWorkstation,
            _ => false
        };

        if (!shouldAutoRequest) return false;
        if (User == null) return false;

        var config = PortalWinConfig.Load();
        if (FindAllDevicesForCurrentUser(config).Count == 0) return false;

        // Primary signal from framework; fallback to registry for environments where selection is delayed.
        return this.IsDefaultTile || IsLikelyLastLoggedOnUser(User);
    }

    private static bool IsLikelyLastLoggedOnUser(CredentialProviderUser user)
    {
        var registrySid = ReadLastLoggedOnUserSid();
        if (!string.IsNullOrWhiteSpace(registrySid) && !string.IsNullOrWhiteSpace(user.Sid))
        {
            return string.Equals(user.Sid, registrySid, StringComparison.OrdinalIgnoreCase);
        }

        var tileCanonical = IdentityHelper.ToCanonical(user.QualifiedUserName) ?? IdentityHelper.ToCanonical(user.UserName);
        var tileShort = GetShortUserName(user.QualifiedUserName) ?? GetShortUserName(user.UserName);

        foreach (var candidate in ReadLastLoggedOnUserCandidates())
        {
            var candCanonical = IdentityHelper.ToCanonical(candidate);
            var candShort = GetShortUserName(candidate);

            if (IdentityHelper.EqualsIgnoreCase(tileCanonical, candCanonical))
            {
                return true;
            }

            if (IdentityHelper.EqualsIgnoreCase(tileShort, candShort))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadLastLoggedOnUserSid()
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI";

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            var value = key?.GetValue("LastLoggedOnUserSID") as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[PortalWinTile] Failed to read LastLoggedOnUserSID from registry: {ex.Message}");
            return null;
        }
    }

    private static IEnumerable<string> ReadLastLoggedOnUserCandidates()
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI";
        var result = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key == null) return result;

            foreach (var name in new[] { "LastLoggedOnUser", "LastLoggedOnSAMUser" })
            {
                var value = key.GetValue(name) as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[PortalWinTile] Failed to read LogonUI last user from registry: {ex.Message}");
        }

        return result;
    }

    private static string? GetShortUserName(string? userOrUpn)
    {
        if (string.IsNullOrWhiteSpace(userOrUpn))
        {
            return null;
        }

        var value = userOrUpn.Trim();

        if (value.Contains("\\"))
        {
            return IdentityHelper.GetShortUsername(value);
        }

        var atIndex = value.IndexOf('@');
        if (atIndex > 0)
        {
            return value[..atIndex];
        }

        return value;
    }

    private void TryAutoRequestUnlock(bool forceTakeover, string source)
    {
        if (!AllowsHostInitiated) return;

        var trigger = Provider.HostRequestTrigger;
        if (trigger == HostRequestTrigger.OnClick) return;

        var scenario = Provider.UsageScenario;
        bool shouldAutoRequest = false;

        switch (trigger)
        {
            case HostRequestTrigger.OnClickAndStartup:
                shouldAutoRequest = (scenario == Lithnet.CredentialProvider.UsageScenario.Logon);
                break;
            case HostRequestTrigger.OnClickAndAnyLockScreen:
                shouldAutoRequest = (scenario == Lithnet.CredentialProvider.UsageScenario.Logon
                                  || scenario == Lithnet.CredentialProvider.UsageScenario.UnlockWorkstation);
                break;
        }

        if (!shouldAutoRequest) return;

        if (_activeRequestCts != null && !_activeRequestCts.IsCancellationRequested) return;

        if (User == null) return;
        var config = PortalWinConfig.Load();
        var matchedDevices = FindAllDevicesForCurrentUser(config);
        ApplyHostInitiatedTlsPolicy(config, source);
        if (matchedDevices.Count == 0) return;

        Logger.Log($"[PortalWinTile] Auto-triggering unlock request for {User?.UserName ?? "Unknown"} (source={source}, forceTakeover={forceTakeover}).");
        Task.Run(() => StartUnlockRequest(forceTakeover, source));
    }

    private List<Portal.Common.Models.DeviceModel> FindHostInitiatedDevices(PortalWinConfig config)
    {
        var userDevices = FindAllDevicesForCurrentUser(config);
        if (userDevices.Count > 0)
        {
            return userDevices;
        }

        if (UsageScenario == Lithnet.CredentialProvider.UsageScenario.CredUI && User == null)
        {
            var typedUsername = _usernameControl?.Text;
            if (!string.IsNullOrWhiteSpace(typedUsername))
            {
                var typedDevices = config.FindAllDevicesForUser(typedUsername);
                if (typedDevices.Count > 0)
                {
                    return typedDevices;
                }
            }

            return config.Devices
                .Where(device => device.Accounts.Count > 0)
                .ToList();
        }

        return userDevices;
    }

    protected override CredentialResponseBase GetFallbackCredentials()
    {
        if (_activeRequestCts == null || _activeRequestCts.IsCancellationRequested)
        {
            UpdateStatus("No unlock request pending. Waiting...");
        }

        return new CredentialResponseInsecure
        {
            IsSuccess = false,
            StatusText = "Waiting for remote unlock command",
            StatusIcon = StatusIcon.Warning
        };
    }

    private System.Collections.Generic.List<Portal.Common.Models.DeviceModel> FindAllDevicesForCurrentUser(PortalWinConfig config)
    {
        var tileUsername = User?.UserName;
        var tileQualifiedName = User?.QualifiedUserName;

        if (string.IsNullOrEmpty(tileUsername) && string.IsNullOrEmpty(tileQualifiedName))
        {
            return new System.Collections.Generic.List<Portal.Common.Models.DeviceModel>();
        }

        return config.FindAllDevicesForUser(tileUsername ?? "", tileQualifiedName);
    }

    private void ApplyHostInitiatedTlsPolicy(PortalWinConfig config, string source)
    {
        var tlsService = CredentialProviderBootstrapper.TlsService;
        if (tlsService == null)
        {
            return;
        }

        if (!AllowsHostInitiated)
        {
            tlsService.SetHostInitiatedWebSocketPolicy(GetTileOwnerKey(), Array.Empty<string>());
            return;
        }

        var owner = GetTileOwnerKey();
        var allowedCertHashes = FindHostInitiatedDevices(config)
            .Select(device => device.CertHash)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        tlsService.SetHostInitiatedWebSocketPolicy(owner, allowedCertHashes);
        Logger.Log($"[PortalWinTile] Applied WS TLS policy for owner '{owner}' from source '{source}'. Allowed hashes: {allowedCertHashes.Count}.");
    }

    private void OnRequestUnlockClicked()
    {
        StartUnlockRequest(forceTakeover: true, source: "button");
    }

    private void StartUnlockRequest(bool forceTakeover, string source)
    {
        if (!AllowsHostInitiated) return;

        if (_activeRequestCts != null && !_activeRequestCts.IsCancellationRequested) return;

        var config = PortalWinConfig.Load();
        var targetDevices = FindHostInitiatedDevices(config);
        if (targetDevices.Count == 0)
        {
            Logger.LogWarning($"[PortalWinTile] No host-initiated target devices available (source={source}, scenario={UsageScenario}).");
            UpdateStatus("No paired device available.");
            ShowRequestButton();
            return;
        }

        ApplyHostInitiatedTlsPolicy(config, source);
        int timeoutMinutes = config.HostRequestTimeoutMinutes;
        // Host-Initiated flow must always carry requestId for cross-transport correlation.
        // Keep legacy acceptance on response side, but never omit requestId on request side.
        bool correlationEnabled = true;
        if (!config.HostRequestCorrelationEnabled)
        {
            Logger.LogWarning("[Tile] hostRequestCorrelationEnabled=false in config, but Host-Initiated requestId is forced ON for reliable routing.");
        }

        _activeRequestCts = timeoutMinutes > 0
            ? new CancellationTokenSource(System.TimeSpan.FromMinutes(timeoutMinutes))
            : new CancellationTokenSource();

        var cts = _activeRequestCts;
        var owner = GetTileOwnerKey();

        if (!TryClaimActiveRequest(owner, cts, forceTakeover, source))
        {
            _activeRequestCts = null;
            cts.Dispose();
            return;
        }

        UpdateStatus("Searching device...");
        ShowCancelButton();

        var requestTimer = Stopwatch.StartNew();
        var correlationRequestId = correlationEnabled ? Guid.NewGuid().ToString("N") : null;
        Logger.Log($"[Tile] unlock_request_start source={source} user='{GetTileIdentityForLogging()}' requestId='{correlationRequestId ?? "none"}' correlationEnabled={correlationEnabled}");

        Task.Run(async () =>
        {
            using var statusAggregator = new UnlockStatusAggregator(UpdateStatus);
            var anyRejection = false;
            var approvalCompleted = false;

            try
            {
                var deviceNames = string.Join(", ", targetDevices.Select(d => d.Name));
                Logger.Log($"[Tile] Starting transport tasks for: {deviceNames}. requestId='{correlationRequestId}'");

                var transportTasks = new List<Task<(string? result, Portal.Common.Models.DeviceModel device)>>();

                foreach (var device in targetDevices)
                {
                    transportTasks.Add(Task.Run(() => RunSingleTransportUnlockAsync(device, "net", correlationRequestId, correlationEnabled, statusAggregator, cts.Token), cts.Token));
                    transportTasks.Add(Task.Run(() => RunSingleTransportUnlockAsync(device, "bt", correlationRequestId, correlationEnabled, statusAggregator, cts.Token), cts.Token));
                }

                while (transportTasks.Count > 0)
                {
                    var completedTask = await Task.WhenAny(transportTasks);
                    transportTasks.Remove(completedTask);

                    if (completedTask.IsCompletedSuccessfully)
                    {
                        var (result, device) = completedTask.Result;
                        if (result == "ok")
                        {
                            approvalCompleted = true;
                            Logger.Log($"[Tile] unlock_request_approved elapsedMs={requestTimer.ElapsedMilliseconds} requestId='{correlationRequestId}'");
                            HandleApproval(config, device);
                            cts.Cancel();
                            return;
                        }
                        else if (result == "rejected" || result == "forbidden")
                        {
                            anyRejection = true;
                            Logger.LogWarning($"[Tile] unlock_request_rejected_partial clientId='{device.ClientId}' elapsedMs={requestTimer.ElapsedMilliseconds} requestId='{correlationRequestId}'");
                        }
                    }
                }

                if (anyRejection && !approvalCompleted)
                {
                    Logger.LogWarning($"[Tile] unlock_request_denied elapsedMs={requestTimer.ElapsedMilliseconds} requestId='{correlationRequestId}'");
                    UpdateStatus("Denied by device.");
                }
                else if (cts.IsCancellationRequested && !approvalCompleted)
                {
                    var expectedTimeoutMs = timeoutMinutes > 0 ? timeoutMinutes * 60_000L : -1;
                    var reason = (expectedTimeoutMs > 0 && requestTimer.ElapsedMilliseconds >= expectedTimeoutMs - 250)
                        ? "timeout"
                        : "cancelled";
                    Logger.LogWarning($"[Tile] unlock_request_cancelled reason={reason} elapsedMs={requestTimer.ElapsedMilliseconds} requestId='{correlationRequestId}'");
                    UpdateStatus("Request cancelled or timed out.");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.LogError("[Tile] Global request loop error", ex);
                UpdateStatus("Error occurred.");
            }
            finally
            {
                ShowRequestButton();
                ReleaseActiveRequest(owner, cts);
                if (_activeRequestCts == cts)
                {
                    _activeRequestCts = null;
                }
            }
        });
    }

    private string GetTileOwnerKey()
    {
        return User?.Sid
            ?? User?.QualifiedUserName
            ?? User?.UserName
            ?? "generic";
    }

    private static bool TryClaimActiveRequest(string owner, CancellationTokenSource cts, bool forceTakeover, string source)
    {
        lock (_requestSync)
        {
            if (_globalActiveRequestCts != null && !_globalActiveRequestCts.IsCancellationRequested)
            {
                if (string.Equals(_globalActiveOwner, owner, StringComparison.OrdinalIgnoreCase))
                {
                    // Suppress duplicate auto-triggers when LogonUI is re-shown (e.g. clock -> LogonUI).
                    // Allow only explicit button re-request to replace the current in-flight request.
                    if (!string.Equals(source, "button", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log($"[PortalWinTile] Duplicate request suppressed for owner '{owner}' (source={source}). Active request is still in progress.");
                        return false;
                    }

                    Logger.Log($"[PortalWinTile] Re-request requested by button for '{owner}'. Replacing active request.");
                    try { _globalActiveRequestCts.Cancel(); } catch { }
                    _globalActiveOwner = owner;
                    _globalActiveRequestCts = cts;
                    return true;
                }

                if (!forceTakeover)
                {
                    Logger.Log($"[PortalWinTile] Active request already owned by '{_globalActiveOwner}'. Tile '{owner}' waits (source={source}).");
                    return false;
                }

                Logger.Log($"[PortalWinTile] Taking over active request from '{_globalActiveOwner}' to '{owner}' (source={source}).");
                try { _globalActiveRequestCts.Cancel(); } catch { }
            }

            _globalActiveOwner = owner;
            _globalActiveRequestCts = cts;
            return true;
        }
    }

    private static void ReleaseActiveRequest(string owner, CancellationTokenSource cts)
    {
        lock (_requestSync)
        {
            if (ReferenceEquals(_globalActiveRequestCts, cts))
            {
                _globalActiveRequestCts = null;
                _globalActiveOwner = null;
            }
        }
    }

    private enum UnlockTransportStage
    {
        Searching,
        AwaitingApproval
    }

    private sealed class UnlockStatusAggregator : IDisposable
    {
        private readonly Action<string> _publishStatus;
        private readonly object _sync = new();
        private UnlockTransportStage? _latestStage;
        private bool _disposed;

        public UnlockStatusAggregator(Action<string> publishStatus)
        {
            _publishStatus = publishStatus;
        }

        public void Report(UnlockTransportStage stage)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                if (_latestStage == UnlockTransportStage.AwaitingApproval && stage == UnlockTransportStage.Searching)
                {
                    return;
                }

                _latestStage = stage;

                var statusMessage = BuildStatusMessageLocked();
                if (!string.IsNullOrWhiteSpace(statusMessage))
                {
                    _publishStatus(statusMessage);
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _latestStage = null;
            }
        }

        private string BuildStatusMessageLocked()
        {
            if (_latestStage == UnlockTransportStage.AwaitingApproval)
            {
                return "Awaiting approval...";
            }

            if (_latestStage == UnlockTransportStage.Searching)
            {
                return "Searching device...";
            }

            return "Requesting unlock...";
        }
    }

    private async Task WaitUntilConnectedAsync(string clientId, bool useNet, bool useBt, CancellationToken ct)
    {
        var tls = CredentialProviderBootstrapper.TlsService;
        var bt = CredentialProviderBootstrapper.BtService;

        if ((useNet && tls != null && tls.IsNetworkClientConnected(clientId)) ||
            (useBt && bt != null && bt.IsClientConnected(clientId)))
        {
            return;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetResult(false));

        System.Action<string, bool> onNet = (cid, connected) => { if (cid == clientId && connected) tcs.TrySetResult(true); };
        System.Action<string, bool> onBt = (cid, connected) => { if (cid == clientId && connected) tcs.TrySetResult(true); };

        if (useNet && tls != null) tls.NetworkConnectionChanged += onNet;
        if (useBt && bt != null) bt.BtConnectionChanged += onBt;

        try { await tcs.Task; }
        finally
        {
            if (useNet && tls != null) tls.NetworkConnectionChanged -= onNet;
            if (useBt && bt != null) bt.BtConnectionChanged -= onBt;
        }
    }

    private async Task<(string? result, Portal.Common.Models.DeviceModel device)> RunSingleTransportUnlockAsync(
        Portal.Common.Models.DeviceModel device,
        string transport,
        string? requestId,
        bool correlationEnabled,
        UnlockStatusAggregator statusAggregator,
        CancellationToken ct)
    {
        bool useNetwork = transport == "net" && (device.TransportType == TransportType.Network || device.TransportType == TransportType.Both);
        bool useBluetooth = transport == "bt" && (device.TransportType == TransportType.Bluetooth || device.TransportType == TransportType.Both);

        if (!useNetwork && !useBluetooth) return (null, device);

        var tls = CredentialProviderBootstrapper.TlsService;
        var bt = CredentialProviderBootstrapper.BtService;

        while (!ct.IsCancellationRequested)
        {
            statusAggregator.Report(UnlockTransportStage.Searching);
            Logger.Log($"[Tile] waiting_connection clientId={device.ClientId} transport={transport}");
            await WaitUntilConnectedAsync(device.ClientId, useNetwork, useBluetooth, ct);
            if (ct.IsCancellationRequested) break;

            if (useNetwork && tls != null && tls.IsNetworkClientConnected(device.ClientId))
            {
                Logger.Log($"[Tile] {device.Name} (NET): Connected. Sending unlock request. requestId='{requestId}'");
                statusAggregator.Report(UnlockTransportStage.AwaitingApproval);
                var sendTimer = Stopwatch.StartNew();
                var res = await tls.RequestUnlockFromClientAsync(device, requestId, correlationEnabled, ct);
                Logger.Log($"[Tile] net_request_done clientId={device.ClientId} result={res ?? "null"} elapsedMs={sendTimer.ElapsedMilliseconds} requestId='{requestId}'");
                if (res != null) return (res, device);
            }

            if (useBluetooth && bt != null && bt.IsClientConnected(device.ClientId))
            {
                Logger.Log($"[Tile] {device.Name} (BT): Connected. Sending unlock request. requestId='{requestId}'");
                statusAggregator.Report(UnlockTransportStage.AwaitingApproval);
                var sendTimer = Stopwatch.StartNew();
                var res = await bt.RequestUnlockFromClientAsync(device.ClientId, requestId, correlationEnabled, ct);
                Logger.Log($"[Tile] bt_request_done clientId={device.ClientId} result={res ?? "null"} elapsedMs={sendTimer.ElapsedMilliseconds} requestId='{requestId}'");
                if (res != null) return (res, device);
            }

            if (!ct.IsCancellationRequested)
            {
                Logger.LogWarning($"[Tile] transport_waiting clientId={device.ClientId} transport={transport} reason=no_connected_client");
                statusAggregator.Report(UnlockTransportStage.Searching);
            }
        }

        return (null, device);
    }

    private void OnCancelUnlockClicked()
    {
        _activeRequestCts?.Cancel();
        DisconnectAllTransportsFast("Request cancelled by user");
        UpdateStatus("Request cancelled.");
        ShowRequestButton();
    }

    private static void DisconnectAllTransportsFast(string reason)
    {
        CredentialProviderBootstrapper.TlsService?.DisconnectAllWebSocketClients(reason);
        CredentialProviderBootstrapper.BtService?.DisconnectAllClients(reason);
    }

    private void HandleApproval(PortalWinConfig config, Portal.Common.Models.DeviceModel targetDevice)
    {
        UpdateStatus("Approved! Loading credentials...");
        var canonicalUser = IdentityHelper.ToCanonical(User?.QualifiedUserName)
            ?? IdentityHelper.ToCanonical(_usernameControl?.Text)
            ?? IdentityHelper.ToCanonical(User?.UserName);
        var shortUser = IdentityHelper.GetShortUsername(User?.QualifiedUserName)
            ?? IdentityHelper.GetShortUsername(_usernameControl?.Text)
            ?? IdentityHelper.GetShortUsername(User?.UserName);

        Portal.Common.Models.DeviceAccount? targetAccount = targetDevice.Accounts.FirstOrDefault(a =>
            IdentityHelper.EqualsIgnoreCase(
                IdentityHelper.ToCanonical(a.Username, a.Domain),
                canonicalUser));

        if (targetAccount == null && !string.IsNullOrEmpty(shortUser))
        {
            targetAccount = targetDevice.Accounts.FirstOrDefault(a =>
                IdentityHelper.EqualsIgnoreCase(IdentityHelper.GetShortUsername(a.Username), shortUser));

            if (targetAccount != null)
            {
                Logger.LogWarning($"[Tile] approval_account_fallback reason=identity_mismatch requested='{canonicalUser ?? shortUser}' matchedShort='{targetAccount.Username}'");
            }
        }

        if (targetAccount != null)
        {
            using var securePassword = targetAccount.GetDecryptedSecurePassword();
            if (securePassword != null && securePassword.Length > 0)
            {
                var submitUser = IdentityHelper.GetShortUsername(targetAccount.Username) ?? targetAccount.Username;
                var submitDomain = IdentityHelper.GetDomainFromIdentity(targetAccount.Username, targetAccount.Domain)
                    ?? System.Environment.MachineName;
                Provider.OnUnlockRequested(submitUser, securePassword, submitDomain);
            }
            else
            {
                UpdateStatus("Approved, but no credentials found.");
            }
        }
        else
        {
            Logger.LogWarning($"[Tile] approval_account_match_failed selected='{canonicalUser ?? shortUser ?? "unknown"}' device='{targetDevice.Name}' accounts='{targetDevice.Accounts.Count}'");
            UpdateStatus("Approved, but no account matched.");
        }

        ShowRequestButton();
    }

    private void ShowRequestButton()
    {
        if (_requestButton != null)
        {
            var config = PortalWinConfig.Load();
            _requestButton.State = AllowsHostInitiated && FindHostInitiatedDevices(config).Count > 0
                ? FieldState.DisplayInSelectedTile
                : FieldState.Hidden;
        }
        if (_cancelButton != null) _cancelButton.State = FieldState.Hidden;
    }

    private void ShowCancelButton()
    {
        if (_requestButton != null) _requestButton.State = FieldState.Hidden;
        if (_cancelButton != null) _cancelButton.State = FieldState.DisplayInSelectedTile;
    }

    private bool IsForUser(string username)
    {
        if (User == null) return false;
        return string.Equals(User.UserName, username, System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(User.QualifiedUserName, username, System.StringComparison.OrdinalIgnoreCase);
    }

    private string GetTileIdentityForLogging()
    {
        var usernameText = string.IsNullOrWhiteSpace(_usernameControl?.Text) ? null : _usernameControl?.Text;

        return User?.QualifiedUserName
            ?? User?.UserName
            ?? usernameText
            ?? "generic";
    }

    internal static void ResetAutoRequestClaim()
    {
        lock (_requestSync)
        {
            // Keep live requests across temporary LogonUI reloads (clock <-> LogonUI),
            // otherwise auto-trigger may re-send duplicates.
            if (_globalActiveRequestCts != null && !_globalActiveRequestCts.IsCancellationRequested)
            {
                Logger.Log("[PortalWinTile] ResetAutoRequestClaim skipped: active request is still in progress.");
                return;
            }

            _globalActiveRequestCts = null;
            _globalActiveOwner = null;
        }
    }
}
