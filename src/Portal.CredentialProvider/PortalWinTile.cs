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
    private string? _cancellationReason;
    private static readonly object _requestSync = new();
    private static readonly object _tilesSync = new();
    private static readonly HashSet<PortalWinTile> _tiles = new();
    private static CancellationTokenSource? _globalActiveRequestCts;
    private static string? _globalActiveOwner;
    private static volatile bool _isEmergencyRollbackActive;
    public static bool IsEmergencyRollbackActive => _isEmergencyRollbackActive;
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

        _cancellationReason = "tile_switch";
        _activeRequestCts.Cancel();
        UpdateStatus("Cancelled (user switch).");
        ShowRequestButton();
    }

    private void TryEarlyAutoRequestUnlock()
    {
        // Lock screen curtain can delay OnSelected; for likely default user tile we start early.
        if (!ShouldAttemptEarlyStart()) return;

        var isCredUi = Provider.UsageScenario == Lithnet.CredentialProvider.UsageScenario.CredUI;
        Logger.Log($"[PortalWinTile] Early auto-start candidate detected for '{User?.QualifiedUserName ?? User?.UserName ?? "Generic"}' (CredUI={isCredUi}).");
        ApplyHostInitiatedTlsPolicy(PortalWinConfig.Load(), isCredUi ? "credui_initialize" : "initialize");
        TryAutoRequestUnlock(forceTakeover: isCredUi, source: isCredUi ? "credui_initialize" : "initialize");
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
                || scenario == Lithnet.CredentialProvider.UsageScenario.UnlockWorkstation
                || scenario == Lithnet.CredentialProvider.UsageScenario.CredUI,
            _ => false
        };

        if (!shouldAutoRequest) return false;

        var config = PortalWinConfig.Load();

        if (scenario == Lithnet.CredentialProvider.UsageScenario.CredUI)
        {
            return FindHostInitiatedDevices(config).Count > 0;
        }

        if (User == null) return false;
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
        return IdentityHelper.GetShortUsername(userOrUpn);
    }

    private void TryAutoRequestUnlock(bool forceTakeover, string source)
    {
        if (_isEmergencyRollbackActive) return;
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
                                  || scenario == Lithnet.CredentialProvider.UsageScenario.UnlockWorkstation
                                  || scenario == Lithnet.CredentialProvider.UsageScenario.CredUI);
                break;
        }

        if (!shouldAutoRequest) return;

        if (_activeRequestCts != null && !_activeRequestCts.IsCancellationRequested) return;

        if (User == null && scenario != Lithnet.CredentialProvider.UsageScenario.CredUI) return;
        var config = PortalWinConfig.Load();
        var matchedDevices = FindHostInitiatedDevices(config);
        ApplyHostInitiatedTlsPolicy(config, source);
        if (matchedDevices.Count == 0) return;

        Logger.Log($"[PortalWinTile] Auto-triggering unlock request for {GetTileIdentityForLogging()} (source={source}, forceTakeover={forceTakeover}).");
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
        if (_isEmergencyRollbackActive)
        {
            return new CredentialResponseInsecure
            {
                IsSuccess = false,
                StatusText = Localization.T("Cancelled by shortcut (Left Ctrl + Left Alt)"),
                StatusIcon = StatusIcon.None
            };
        }

        if (_activeRequestCts == null || _activeRequestCts.IsCancellationRequested)
        {
            if (AllowsHostInitiated)
            {
                Logger.Log("[PortalWinTile] Enter key pressed on idle tile with empty password; starting remote unlock request (source=keyboard_enter).");
                StartUnlockRequest(forceTakeover: true, source: "keyboard_enter");
                return new CredentialResponseInsecure
                {
                    IsSuccess = false,
                    StatusText = Localization.T("Waiting for remote unlock command"),
                    StatusIcon = StatusIcon.None
                };
            }

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
        _isEmergencyRollbackActive = false;
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
        int effectiveTimeoutSeconds = config.EffectiveTimeoutSeconds;
        bool showProgress = config.ShowLockScreenProgress;
        // Host-Initiated flow must always carry requestId for cross-transport correlation.
        // Keep legacy acceptance on response side, but never omit requestId on request side.
        bool correlationEnabled = true;
        if (!config.HostRequestCorrelationEnabled)
        {
            Logger.LogWarning("[Tile] hostRequestCorrelationEnabled=false in config, but Host-Initiated requestId is forced ON for reliable routing.");
        }

        _cancellationReason = null;
        _activeRequestCts = effectiveTimeoutSeconds > 0
            ? new CancellationTokenSource(System.TimeSpan.FromSeconds(effectiveTimeoutSeconds))
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
            using var statusAggregator = new UnlockStatusAggregator(UpdateStatus, effectiveTimeoutSeconds, requestTimer, cts.Token, showProgress, config.CustomWaitingText);
            var anyRejection = false;
            Portal.Common.Models.DeviceModel? lastRejectionDevice = null;
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
                            ActivityJournal.Record(
                                "unlock",
                                "✨",
                                "PC unlock approved",
                                $"{device.Name} approved an unlock request over {GetTransportLabel(device)}.",
                                deviceName: device.Name,
                                transport: GetTransportLabel(device));
                            HandleApproval(config, device);
                            cts.Cancel();
                            return;
                        }
                        else if (result == "rejected" || result == "forbidden")
                        {
                            anyRejection = true;
                            lastRejectionDevice = device;
                            Logger.LogWarning($"[Tile] unlock_request_rejected_partial clientId='{device.ClientId}' elapsedMs={requestTimer.ElapsedMilliseconds} requestId='{correlationRequestId}'");
                        }
                    }
                }

                if (anyRejection && !approvalCompleted)
                {
                    var devName = lastRejectionDevice?.Name;
                    var statusMsg = !string.IsNullOrWhiteSpace(devName)
                        ? Localization.TF("Declined by '{0}'.", devName)
                        : Localization.T("Declined by device.");
                    Logger.LogWarning($"[Tile] unlock_request_denied elapsedMs={requestTimer.ElapsedMilliseconds} requestId='{correlationRequestId}'");
                    ActivityJournal.Record("unlock", "🚫", "Unlock request declined", $"Unlock request was declined by {devName ?? "device"}.", false);
                    UpdateStatus(statusMsg);
                }
                else if (cts.IsCancellationRequested && !approvalCompleted && !Provider.UnlockState.HasPendingUnlock)
                {
                    var expectedTimeoutMs = effectiveTimeoutSeconds > 0 ? effectiveTimeoutSeconds * 1000L : -1;
                    var isTimeout = (expectedTimeoutMs > 0 && requestTimer.ElapsedMilliseconds >= expectedTimeoutMs - 500);

                    string statusMsg;
                    string journalTitle;
                    string journalIcon;
                    string journalDetails;

                    if (isTimeout)
                    {
                        statusMsg = "Request timed out.";
                        journalTitle = "Unlock request timed out";
                        journalIcon = "⌛";
                        journalDetails = "No paired device responded before the request expired.";
                    }
                    else if (_cancellationReason == "manual_typing")
                    {
                        statusMsg = "Manual password input.";
                        journalTitle = "Unlock cancelled";
                        journalIcon = "⌨️";
                        journalDetails = "Remote unlock was cancelled due to manual password entry.";
                    }
                    else if (_cancellationReason == "user")
                    {
                        statusMsg = "Cancelled by user.";
                        journalTitle = "Unlock cancelled";
                        journalIcon = "↩️";
                        journalDetails = "The remote unlock request was cancelled by the user.";
                    }
                    else if (_cancellationReason == "tile_switch")
                    {
                        statusMsg = "Cancelled (user switch).";
                        journalTitle = "Unlock cancelled";
                        journalIcon = "👥";
                        journalDetails = "The unlock request was cancelled because another tile was selected.";
                    }
                    else
                    {
                        statusMsg = "Request cancelled.";
                        journalTitle = "Unlock request cancelled";
                        journalIcon = "↩️";
                        journalDetails = "The remote unlock request was cancelled.";
                    }

                    Logger.LogWarning($"[Tile] unlock_request_cancelled reason={_cancellationReason ?? (isTimeout ? "timeout" : "cancelled")} elapsedMs={requestTimer.ElapsedMilliseconds} requestId='{correlationRequestId}'");
                    ActivityJournal.Record("unlock", journalIcon, journalTitle, journalDetails, false);
                    UpdateStatus(statusMsg);
                }
                else if (!approvalCompleted && !Provider.UnlockState.HasPendingUnlock)
                {
                    UpdateStatus("Device unreachable.");
                    ActivityJournal.Record("unlock", "⚠️", "Device unreachable", "Could not establish connection to paired device.", false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.LogError("[Tile] Global request loop error", ex);
                if (!Provider.UnlockState.HasPendingUnlock)
                {
                    UpdateStatus("Error occurred.");
                }
            }
            finally
            {
                if (!approvalCompleted && !Provider.UnlockState.HasPendingUnlock)
                {
                    ShowRequestButton();
                }
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

                if (string.Equals(owner, "generic", StringComparison.OrdinalIgnoreCase) && !string.Equals(_globalActiveOwner, "generic", StringComparison.OrdinalIgnoreCase) && source.Contains("initialize"))
                {
                    Logger.Log($"[PortalWinTile] Keeping specific user tile request '{_globalActiveOwner}' over 'generic' (source={source}).");
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

    public static void CancelGlobalActiveRequest()
    {
        lock (_requestSync)
        {
            try { _globalActiveRequestCts?.Cancel(); } catch { }
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
        private readonly int _timeoutSeconds;
        private readonly Stopwatch _timer;
        private readonly CancellationToken _ct;
        private readonly bool _showProgress;
        private readonly string? _customWaitingText;
        private readonly CancellationTokenSource _tickerCts = new();
        private readonly object _sync = new();
        private UnlockTransportStage? _latestStage;
        private bool _disposed;

        public UnlockStatusAggregator(Action<string> publishStatus, int timeoutSeconds, Stopwatch timer, CancellationToken ct, bool showProgress = true, string? customWaitingText = null)
        {
            _publishStatus = publishStatus;
            _timeoutSeconds = timeoutSeconds;
            _timer = timer;
            _ct = ct;
            _showProgress = showProgress;
            _customWaitingText = customWaitingText;

            PublishCurrentStatus();
            if (_showProgress)
            {
                _ = RunTickerAsync();
            }
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
                PublishCurrentStatusLocked();
            }
        }

        private void PublishCurrentStatus()
        {
            lock (_sync)
            {
                if (_disposed) return;
                PublishCurrentStatusLocked();
            }
        }

        private void PublishCurrentStatusLocked()
        {
            var statusMessage = BuildStatusMessageLocked();
            if (!string.IsNullOrWhiteSpace(statusMessage))
            {
                _publishStatus(statusMessage);
            }
        }

        private async Task RunTickerAsync()
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, _tickerCts.Token);
            var token = linked.Token;

            try
            {
                while (!token.IsCancellationRequested && !_disposed)
                {
                    await Task.Delay(1000, token);
                    PublishCurrentStatus();
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped on cancellation or completion
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[UnlockStatusAggregator] Ticker error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _latestStage = null;
            }

            try
            {
                _tickerCts.Cancel();
                _tickerCts.Dispose();
            }
            catch { }
        }

        private string BuildStatusMessageLocked()
        {
            string baseKey = _latestStage switch
            {
                UnlockTransportStage.AwaitingApproval => !string.IsNullOrWhiteSpace(_customWaitingText) ? _customWaitingText.Trim() : "Awaiting approval...",
                UnlockTransportStage.Searching => "Searching device...",
                _ => "Requesting unlock..."
            };

            if (!_showProgress)
            {
                return baseKey;
            }

            int elapsedSeconds = (int)(_timer.ElapsedMilliseconds / 1000);

            if (_timeoutSeconds > 0)
            {
                int totalSeconds = _timeoutSeconds;
                int remainingSeconds = Math.Max(0, totalSeconds - elapsedSeconds);
                return $"{baseKey} [progress:countdown,{remainingSeconds},{totalSeconds},{elapsedSeconds}]";
            }
            else
            {
                return $"{baseKey} [progress:infinite,{elapsedSeconds}]";
            }
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

        if ((useNet && tls != null && tls.IsNetworkClientConnected(clientId)) ||
            (useBt && bt != null && bt.IsClientConnected(clientId)))
        {
            tcs.TrySetResult(true);
        }

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
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
        }

        return (null, device);
    }

    private void OnCancelUnlockClicked()
    {
        _cancellationReason = "user";
        _activeRequestCts?.Cancel();
        DisconnectAllTransportsFast("Request cancelled by user");
        UpdateStatus("Cancelled by user.");
        ShowRequestButton();
    }

    protected override void OnManualPasswordInputChanged()
    {
        base.OnManualPasswordInputChanged();

        if (_activeRequestCts != null && !_activeRequestCts.IsCancellationRequested)
        {
            _cancellationReason = "manual_typing";
            Logger.Log("[Tile] Manual password input detected; cancelling active remote unlock request.");
            _activeRequestCts.Cancel();
            DisconnectAllTransportsFast("Manual password input");
            UpdateStatus("Manual password input.");
            ShowRequestButton();
        }
    }

    private static void DisconnectAllTransportsFast(string reason)
    {
        CredentialProviderBootstrapper.TlsService?.DisconnectAllWebSocketClients(reason);
        CredentialProviderBootstrapper.BtService?.DisconnectAllClients(reason);
    }

    private void HandleApproval(PortalWinConfig config, Portal.Common.Models.DeviceModel targetDevice)
    {
        UpdateStatus("Approved! Loading credentials...");
        var targetAccount = CredentialProviderTilePolicy.ResolveApprovalAccount(
            targetDevice,
            User?.QualifiedUserName,
            User?.UserName,
            _usernameControl?.Text);

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
            var selected = User?.QualifiedUserName
                ?? _usernameControl?.Text
                ?? User?.UserName
                ?? "unknown";
            Logger.LogWarning($"[Tile] approval_account_match_failed selected='{selected}' device='{targetDevice.Name}' accounts='{targetDevice.Accounts.Count}'");
            UpdateStatus("Approved, but no account matched.");
        }

        ShowRequestButton();
    }

    private static string GetTransportLabel(Portal.Common.Models.DeviceModel device)
    {
        return device.TransportType switch
        {
            TransportType.Network => "Wi-Fi",
            TransportType.Bluetooth => "Bluetooth",
            _ => "Wi-Fi or Bluetooth"
        };
    }

    private void ShowRequestButton()
    {
        if (_requestButton != null)
        {
            _requestButton.Label = Localization.T("Retry");
            _requestButton.OnClick = OnRequestUnlockClicked;
            _requestButton.AsPushButton();
            _requestButton.State = AllowsHostInitiated
                ? (Provider.UsageScenario == UsageScenario.CredUI ? FieldState.DisplayInBoth : FieldState.DisplayInSelectedTile)
                : FieldState.Hidden;
        }
        if (_cancelButton != null) _cancelButton.State = FieldState.Hidden;
    }

    private void ShowCancelButton()
    {
        if (_requestButton != null)
        {
            _requestButton.Label = Localization.T("Cancel Request");
            _requestButton.OnClick = OnCancelUnlockClicked;
            _requestButton.AsPushButton();
            _requestButton.State = Provider.UsageScenario == UsageScenario.CredUI
                ? FieldState.DisplayInBoth
                : FieldState.DisplayInSelectedTile;
        }
        else if (_cancelButton != null)
        {
            _cancelButton.Label = Localization.T("Cancel Request");
            _cancelButton.OnClick = OnCancelUnlockClicked;
            _cancelButton.AsPushButton();
            _cancelButton.State = Provider.UsageScenario == UsageScenario.CredUI
                ? FieldState.DisplayInBoth
                : FieldState.DisplayInSelectedTile;
        }
        if (_cancelButton != null && _requestButton != null) _cancelButton.State = FieldState.Hidden;
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

    public static void TriggerEmergencyRollback()
    {
        Logger.LogWarning("[PortalWinTile] TriggerEmergencyRollback invoked by emergency shortcut (Left Ctrl + Left Alt).");
        _isEmergencyRollbackActive = true;

        lock (_requestSync)
        {
            if (_globalActiveRequestCts != null)
            {
                try
                {
                    _globalActiveRequestCts.Cancel();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[PortalWinTile] Error cancelling global request CTS: {ex.Message}");
                }
            }
        }

        DisconnectAllTransportsFast("Emergency rollback (Left Ctrl + Left Alt)");

        try
        {
            CredentialProviderBootstrapper.CurrentProvider?.CancelPendingAutoLogon();
        }
        catch (Exception ex)
        {
            Logger.LogError("[PortalWinTile] Error clearing provider unlock state", ex);
        }

        PortalWinTile[] snapshot;
        lock (_tilesSync)
        {
            snapshot = _tiles.ToArray();
        }

        foreach (var tile in snapshot)
        {
            try
            {
                tile._activeRequestCts?.Cancel();
                tile.UpdateStatus("Emergency rollback: request cancelled");
                tile.ShowRequestButton();
            }
            catch (Exception ex)
            {
                Logger.LogError("[PortalWinTile] Error updating tile on emergency rollback", ex);
            }
        }

        ActivityJournal.Record(
            "unlock",
            "🚨",
            Localization.T("Emergency rollback"),
            Localization.T("The authorization process was cancelled via emergency shortcut (Left Ctrl + Left Alt)."),
            false);
    }

    protected override void OnLogonStatusReported(int ntStatusCode, int ntSubstatusCode, out string optionalStatusText, out Lithnet.CredentialProvider.StatusIcon optionalStatusIcon)
    {
        base.OnLogonStatusReported(ntStatusCode, ntSubstatusCode, out optionalStatusText, out optionalStatusIcon);

        try
        {
            if (ntStatusCode == 0)
            {
                Logger.Log("[Tile] Windows logon succeeded (STATUS_SUCCESS 0x00000000).");
                ActivityJournal.Record("logon", "✅", "Windows logon succeeded", "Windows accepted the submitted credentials.", true);
                UpdateStatus("Logon successful.");
                return;
            }

            uint uCode = (uint)ntStatusCode;
            string hex = $"0x{uCode:X8}";
            string subHex = $"0x{(uint)ntSubstatusCode:X8}";

            string friendlyHeadline;
            string journalTitle;
            string journalDetails;
            string icon;

            switch (uCode)
            {
                case 0xC000006A: // STATUS_WRONG_PASSWORD
                case 0xC000006D: // STATUS_LOGON_FAILURE
                    friendlyHeadline = "Incorrect Windows password.";
                    journalTitle = "Windows logon failed: incorrect password";
                    journalDetails = $"The password stored in LSA Secret was rejected by Windows ({hex}).";
                    icon = "🔑";
                    break;

                case 0xC0000234: // STATUS_ACCOUNT_LOCKED_OUT
                    friendlyHeadline = "Account is locked out.";
                    journalTitle = "Windows logon failed: account locked";
                    journalDetails = "The Windows user account has been locked out due to failed logon attempts.";
                    icon = "🔒";
                    break;

                case 0xC0000071: // STATUS_PASSWORD_EXPIRED
                case 0xC0000224: // STATUS_PASSWORD_MUST_CHANGE
                case 0xC0000193: // STATUS_ACCOUNT_EXPIRED
                    friendlyHeadline = "Windows password has expired.";
                    journalTitle = "Windows logon failed: password expired";
                    journalDetails = "The password for this Windows account has expired and must be updated.";
                    icon = "⌛";
                    break;

                case 0xC0000072: // STATUS_ACCOUNT_DISABLED
                    friendlyHeadline = "Account is disabled.";
                    journalTitle = "Windows logon failed: account disabled";
                    journalDetails = "The Windows user account is currently disabled.";
                    icon = "🚫";
                    break;

                case 0xC0000064: // STATUS_NO_SUCH_USER
                    friendlyHeadline = "User account not found.";
                    journalTitle = "Windows logon failed: user not found";
                    journalDetails = "The specified Windows user account does not exist.";
                    icon = "👤";
                    break;

                case 0xC000005E: // STATUS_NO_LOGON_SERVERS
                    friendlyHeadline = "No logon servers available.";
                    journalTitle = "Windows logon failed: no servers";
                    journalDetails = "Domain controller or authentication server is currently unreachable.";
                    icon = "🌐";
                    break;

                default:
                    friendlyHeadline = Localization.TF("Windows logon failed ({0}).", hex);
                    journalTitle = "Windows logon failed";
                    journalDetails = $"Windows returned logon error {hex} (substatus {subHex}).";
                    icon = "⚠️";
                    break;
            }

            Logger.LogError($"[Tile] Windows logon failed: ntStatus={hex} ntSubstatus={subHex} ({friendlyHeadline})");
            ActivityJournal.Record("logon", icon, journalTitle, journalDetails, false);
            UpdateStatus(friendlyHeadline);
            ShowRequestButton();

            optionalStatusText = Localization.T(friendlyHeadline);
            optionalStatusIcon = Lithnet.CredentialProvider.StatusIcon.Error;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Tile] Error handling OnLogonStatusReported: {ex.Message}");
        }
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
