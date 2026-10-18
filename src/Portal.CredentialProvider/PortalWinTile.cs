using Lithnet.CredentialProvider;
using Microsoft.Win32;
using Portal.Common;
using Portal.Common.Helpers;
using Portal.Common.Models;
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
    private static PortalWinTile? _selectedTile;
    private static CancellationTokenSource? _globalActiveRequestCts;
    private static string? _globalActiveOwner;
    private static volatile bool _isEmergencyRollbackActive;
    public static bool IsEmergencyRollbackActive => _isEmergencyRollbackActive;
    public static bool HasActiveUnlockRequest
    {
        get
        {
            lock (_requestSync)
            {
                return _globalActiveRequestCts != null && !_globalActiveRequestCts.IsCancellationRequested;
            }
        }
    }
    private bool _isRegisteredInTiles;
    private DateTime _requestStartedTimestamp = DateTime.MinValue;

    private static volatile bool _globalIsAwaiting2FaPin;
    public static bool IsAnyTileAwaitingTwoFactorPin
    {
        get
        {
            if (_globalIsAwaiting2FaPin) return true;
            lock (_tilesSync)
            {
                return _tiles.Any(t => t._isAwaiting2FaPin);
            }
        }
    }

    public static PortalWinTile? GetTileAwaitingTwoFactorPin()
    {
        lock (_tilesSync)
        {
            return _tiles.FirstOrDefault(t => t._isAwaiting2FaPin);
        }
    }

    private volatile bool _isAwaiting2FaPin;
    public bool IsAwaitingTwoFactorPin => _isAwaiting2FaPin;
    private string? _pending2FaUser;
    private string? _pending2FaDomain;
    private string? _pending2FaUserSid;
    private System.Security.SecureString? _pending2FaPassword;
    private Portal.Common.Models.DeviceAccount? _pending2FaAccount;
    private int _pinFailedAttempts;
    private CancellationTokenSource? _twoFactorTimeoutCts;
    private CancellationTokenSource? _autoResetCts;

    public void FocusPasswordField()
    {
        if (_passwordControl != null)
        {
            try
            {
                if (_passwordControl.InteractiveState != FieldInteractiveState.Focused)
                {
                    _passwordControl.InteractiveState = FieldInteractiveState.Focused;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[PortalWinTile] Failed to set password control focus: {ex.Message}");
            }
        }
    }

    public static void OnTypingKeyDetected()
    {
        if (IsAnyTileAwaitingTwoFactorPin)
        {
            // During 2FA PIN entry on lock screen, keyboard typing is expected.
            var pinTile = GetTileAwaitingTwoFactorPin();
            pinTile?.FocusPasswordField();
            return;
        }

        PortalWinTile? targetTile;
        lock (_tilesSync)
        {
            targetTile = _selectedTile
                ?? _tiles.FirstOrDefault(t => t.IsSelected)
                ?? _tiles.FirstOrDefault(t => t.AllowsHostInitiated)
                ?? _tiles.FirstOrDefault();
        }

        if (targetTile == null) return;

        targetTile.EnsureManualTypingMode();
    }

    public void EnsureManualTypingMode()
    {
        if (_isAwaiting2FaPin || IsAnyTileAwaitingTwoFactorPin)
        {
            // During 2FA PIN entry on lock screen, keyboard typing is expected.
            FocusPasswordField();
            return;
        }

        if (_activeRequestCts != null && !_activeRequestCts.IsCancellationRequested)
        {
            _cancellationReason = "manual_typing";
            Logger.Log("[PortalWinTile] Typing key detected; cancelling active remote unlock request.");
            try { _activeRequestCts.Cancel(); } catch { }
            DisconnectAllTransportsFast("Manual typing detected");
            UpdateStatus("Manual password input.");
            ShowRequestButton();
        }

        FocusPasswordField();
    }

    private PortalWinProvider Provider => (PortalWinProvider)_providerBase;

    public Portal.Common.Models.DeviceAccount? ResolveCurrentTileAccount(PortalWinConfig config)
    {
        var devices = FindAllDevicesForCurrentUser(config);
        foreach (var device in devices)
        {
            var acc = CredentialProviderTilePolicy.ResolveApprovalAccount(
                device,
                User?.QualifiedUserName,
                User?.UserName,
                _usernameControl?.Text,
                User?.Sid);
            if (acc != null)
                return acc;
        }
        return null;
    }

    public bool IsAccountExpired
    {
        get
        {
            var config = PortalWinConfig.Load();
            var account = ResolveCurrentTileAccount(config);
            return account != null && account.IsExpired;
        }
    }

    public bool AllowsHostInitiated
    {
        get
        {
            var config = PortalWinConfig.Load();
            var account = ResolveCurrentTileAccount(config);
            if (account != null)
            {
                if (account.IsExpired)
                    return false;

                var mode = account.ResolveEffectiveUnlockMode(Provider.UnlockMode);
                return mode == UnlockMode.HostInitiated || mode == UnlockMode.Both;
            }

            return Provider.UnlockMode == UnlockMode.HostInitiated || Provider.UnlockMode == UnlockMode.Both;
        }
    }

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

        ShowRequestButton(isInitial: true);
        FocusPasswordField();

        var config = PortalWinConfig.Load();
        var account = ResolveCurrentTileAccount(config);
        if (account != null && account.IsExpired)
        {
            UpdateStatus(Localization.T("Account Inactive"), Localization.T("Account inactivity period exceeded. Re-enter password in Portal.Host to reactivate."));
        }
        else
        {
            var pwdStatus = account != null ? PasswordStatusStore.GetAccountExpiryStatus(account.Username, account.Domain, account.UserSid) : null;
            if (pwdStatus != null && pwdStatus.IsExpired)
            {
                UpdateStatus(Localization.T("Password Expired"), Localization.T("Windows password has expired. Update password in Windows and Portal."));
            }
            else if (account != null && account.ResolveEffectiveUnlockMode(Provider.UnlockMode) == UnlockMode.ClientInitiated)
            {
                var subtitle = pwdStatus?.DaysUntilExpiry is <= 7 and > 0
                    ? string.Format(Localization.T("Windows password expires in {0} day(s)."), pwdStatus.DaysUntilExpiry)
                    : Localization.T("Waiting for unlock command from phone app.");
                UpdateStatus(Localization.T("Waiting for remote command"), subtitle);
            }
            else
            {
                TryEarlyAutoRequestUnlock();
            }
        }
    }

    protected override void OnSelected()
    {
        base.OnSelected();
        lock (_tilesSync)
        {
            _selectedTile = this;
        }
        CancelHostInitiatedRequestsOnOtherTiles();
        ApplyHostInitiatedTlsPolicy(PortalWinConfig.Load(), "selected");
        // Explicit tile selection must win over any provisional/early request
        // started before LogonUI finished selecting the user tile.
        TryAutoRequestUnlock(forceTakeover: true, source: "selected");
        FocusPasswordField();
    }

    protected override void OnDeselected()
    {
        base.OnDeselected();
        lock (_tilesSync)
        {
            if (ReferenceEquals(_selectedTile, this))
            {
                _selectedTile = null;
            }
        }
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

    public void EnterTwoFactorPinMode(
        string username,
        System.Security.SecureString? password,
        string domain,
        string? userSid,
        Portal.Common.Models.DeviceAccount account)
    {
        CancelScheduledAutoReset();
        ClearTwoFactorPinMode("Replacing with new 2FA PIN session");

        _isAwaiting2FaPin = true;
        lock (_tilesSync)
        {
            _globalIsAwaiting2FaPin = true;
            _selectedTile = this;
        }
        _pending2FaUser = username;
        _pending2FaDomain = domain;
        _pending2FaUserSid = userSid;
        _pending2FaAccount = account;
        _pinFailedAttempts = 0;

        if (password != null && password.Length > 0)
        {
            _pending2FaPassword = password.Copy();
            _pending2FaPassword.MakeReadOnly();
        }

        // Clear any characters in the password box so tile is clean and focused for PIN
        if (_passwordControl != null)
        {
            try
            {
                _passwordControl.Password = new System.Security.SecureString();
                _passwordControl.InteractiveState = FieldInteractiveState.Focused;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[PortalWinTile] Failed to reset password field for 2FA PIN: {ex.Message}");
            }
        }

        _lastStatusRaw = "Enter 2FA PIN";
        UpdateStatus("Enter 2FA PIN");
        ShowCancelButton();
        FocusPasswordField();

        ActivityJournal.Record(
            "pin_prompt",
            "🔐",
            "2FA PIN requested",
            $"Phone approved unlock for {username}. Awaiting 2FA PIN on Windows lock screen.",
            true);

        // 60-second timeout to avoid leaving pending credentials in memory
        var timeoutCts = new CancellationTokenSource();
        _twoFactorTimeoutCts = timeoutCts;
        Task.Delay(TimeSpan.FromSeconds(60), timeoutCts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            if (_isAwaiting2FaPin)
            {
                Logger.Log("[PortalWinTile] 2FA PIN timeout expired (60s). Aborting session.");
                ClearTwoFactorPinMode("Timeout expired");
                UpdateStatus(Localization.T("PIN timed out. Please retry."), Localization.T("Unlock session expired."));
                ShowRequestButton();
            }
        }, TaskScheduler.Default);
    }

    public void ClearTwoFactorPinMode(string reason = "Cleared")
    {
        _isAwaiting2FaPin = false;
        lock (_tilesSync)
        {
            _globalIsAwaiting2FaPin = _tiles.Any(t => t._isAwaiting2FaPin);
        }
        _pending2FaUser = null;
        _pending2FaDomain = null;
        _pending2FaUserSid = null;
        _pending2FaAccount = null;
        _pinFailedAttempts = 0;

        if (_twoFactorTimeoutCts != null)
        {
            try { _twoFactorTimeoutCts.Cancel(); } catch { }
            _twoFactorTimeoutCts.Dispose();
            _twoFactorTimeoutCts = null;
        }

        if (_pending2FaPassword != null)
        {
            _pending2FaPassword.Dispose();
            _pending2FaPassword = null;
        }

        Logger.Log($"[PortalWinTile] 2FA PIN mode cleared (reason: {reason}).");
    }

    protected override CredentialResponseBase GetCredentials()
    {
        if (_isAwaiting2FaPin)
        {
            return HandleTwoFactorPinSubmission();
        }

        return base.GetCredentials();
    }

    private CredentialResponseBase HandleTwoFactorPinSubmission()
    {
        if (!_isAwaiting2FaPin || _pending2FaAccount == null)
        {
            return GetFallbackCredentials();
        }

        string? enteredPin = GetPlaintextPassword(_passwordControl?.Password);

        if (string.IsNullOrWhiteSpace(enteredPin))
        {
            _lastStatusRaw = "Enter 2FA PIN";
            UpdateStatus("Enter 2FA PIN");
            FocusPasswordField();
            return new CredentialResponseInsecure
            {
                IsSuccess = false,
                StatusText = Localization.T("PIN cannot be empty. Please enter your PIN."),
                StatusIcon = StatusIcon.Warning
            };
        }

        bool isPinValid = _pending2FaAccount.VerifyTwoFactorPin(enteredPin);

        if (isPinValid)
        {
            Logger.Log($"[PortalWinTile] 2FA PIN verified successfully for user '{_pending2FaUser}'.");
            string userToSubmit = !string.IsNullOrEmpty(_pending2FaUser)
                ? _pending2FaUser
                : (User != null ? User.UserName : "");
            string domainToSubmit = _pending2FaDomain ?? System.Environment.MachineName;
            string passToSubmit = GetPlaintextPassword(_pending2FaPassword) ?? "";

            ActivityJournal.Record(
                "pin_success",
                "✅",
                "2FA PIN verified",
                $"PIN successfully verified for {userToSubmit}. Proceeding with Windows sign-in.",
                true);

            // Record successful unlock for inactivity timer and 2FA grace period
            try
            {
                var config = PortalWinConfig.Load();
                config.RecordTwoFactorSuccessActivity(userToSubmit, domainToSubmit, _pending2FaUserSid ?? _pending2FaAccount?.UserSid);
                config.RecordAccountUnlockActivity(userToSubmit, domainToSubmit, _pending2FaUserSid ?? _pending2FaAccount?.UserSid);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[PortalWinTile] Failed to record unlock activity: {ex.Message}");
            }

            CredentialProviderBootstrapper.TlsService?.DisconnectAllWebSocketClients("Unlock serialization (2FA PIN verified)");
            ClearTwoFactorPinMode("PIN verified successfully");

            return new CredentialResponseInsecure
            {
                IsSuccess = true,
                Username = userToSubmit,
                Domain = domainToSubmit,
                Password = passToSubmit
            };
        }
        else
        {
            _pinFailedAttempts++;
            Logger.LogWarning($"[PortalWinTile] Invalid 2FA PIN entered for user '{_pending2FaUser}' (attempt {_pinFailedAttempts}/3).");

            // Invalidate any active 2FA grace period immediately on failed PIN entry
            try
            {
                var targetUser = _pending2FaUser ?? _pending2FaAccount?.Username;
                if (!string.IsNullOrEmpty(targetUser))
                {
                    var config = PortalWinConfig.Load();
                    config.InvalidateTwoFactorGracePeriodActivity(targetUser, _pending2FaDomain, _pending2FaUserSid ?? _pending2FaAccount?.UserSid);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[PortalWinTile] Failed to invalidate 2FA grace period: {ex.Message}");
            }

            if (_passwordControl != null)
            {
                try
                {
                    _passwordControl.Password = new System.Security.SecureString();
                    _passwordControl.InteractiveState = FieldInteractiveState.Focused;
                }
                catch { }
            }

            if (_pinFailedAttempts >= 3)
            {
                ActivityJournal.Record(
                    "pin_lockout",
                    "🚫",
                    "2FA PIN lockout",
                    $"Maximum PIN attempts (3) exceeded for {_pending2FaUser}. Unlock session cancelled.",
                    false);

                ClearTwoFactorPinMode("Max attempts exceeded");
                _lastStatusRaw = "Too many incorrect PIN attempts";
                UpdateStatus(
                    Localization.T("Too many incorrect PIN attempts. Unlock canceled."),
                    Localization.T("Unlock session canceled."));
                ShowRequestButton();

                return new CredentialResponseInsecure
                {
                    IsSuccess = false,
                    StatusText = Localization.T("Too many incorrect PIN attempts. Unlock canceled."),
                    StatusIcon = StatusIcon.Error
                };
            }
            else
            {
                int remaining = 3 - _pinFailedAttempts;
                ActivityJournal.Record(
                    "pin_invalid",
                    "⚠️",
                    "Incorrect 2FA PIN",
                    $"Failed PIN attempt {_pinFailedAttempts}/3 for {_pending2FaUser}. {remaining} remaining.",
                    false);

                string errHeadline = Localization.TF("Incorrect 2FA PIN. Attempts remaining: {0}", remaining);
                _lastStatusRaw = errHeadline;
                UpdateStatus(
                    errHeadline,
                    Localization.T("Please try again."));
                FocusPasswordField();

                return new CredentialResponseInsecure
                {
                    IsSuccess = false,
                    StatusText = Localization.TF("Incorrect 2FA PIN ({0} attempts left)", remaining),
                    StatusIcon = StatusIcon.Warning
                };
            }
        }
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

        return config.FindAllDevicesForUser(tileUsername ?? "", tileQualifiedName, User?.Sid);
    }

    private void ApplyHostInitiatedTlsPolicy(PortalWinConfig config, string source)
    {
        var tlsService = CredentialProviderBootstrapper.TlsService;
        var btService = CredentialProviderBootstrapper.BtService;
        var owner = GetTileOwnerKey();

        if (!AllowsHostInitiated)
        {
            tlsService?.SetHostInitiatedWebSocketPolicy(owner, Array.Empty<string>());
            btService?.SetHostInitiatedBluetoothPolicy(owner, Array.Empty<string>());
            return;
        }

        var hostInitiatedDevices = FindHostInitiatedDevices(config);

        if (tlsService != null)
        {
            var allowedCertHashes = hostInitiatedDevices
                .Select(device => device.CertHash)
                .Where(hash => !string.IsNullOrWhiteSpace(hash))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            tlsService.SetHostInitiatedWebSocketPolicy(owner, allowedCertHashes);
            Logger.Log($"[PortalWinTile] Applied WS TLS policy for owner '{owner}' from source '{source}'. Allowed hashes: {allowedCertHashes.Count}.");
        }

        if (btService != null)
        {
            var allowedClientIds = hostInitiatedDevices
                .Select(device => device.ClientId)
                .Where(cid => !string.IsNullOrWhiteSpace(cid))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            btService.SetHostInitiatedBluetoothPolicy(owner, allowedClientIds);
            Logger.Log($"[PortalWinTile] Applied Bluetooth tile policy for owner '{owner}' from source '{source}'. Allowed clientIds: {allowedClientIds.Count}.");
        }
    }

    private void OnRequestUnlockClicked()
    {
        StartUnlockRequest(forceTakeover: true, source: "button");
    }

    private void StartUnlockRequest(bool forceTakeover, string source)
    {
        CancelScheduledAutoReset();
        _isEmergencyRollbackActive = false;
        ClearTwoFactorPinMode("Starting new unlock request");
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
        _requestStartedTimestamp = DateTime.UtcNow;
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
                            statusAggregator.Dispose();
                            Logger.Log($"[Tile] unlock_request_approved elapsedMs={requestTimer.ElapsedMilliseconds} requestId='{correlationRequestId}'");
                            ActivityJournal.Record(
                                "unlock",
                                "✨",
                                "PC unlock approved",
                                $"{device.Name} approved an unlock request over {GetTransportLabel(device)}.",
                                deviceName: device.Name,
                                transport: GetTransportLabel(device));

                            ReleaseActiveRequest(owner, cts);
                            if (_activeRequestCts == cts)
                            {
                                _activeRequestCts = null;
                            }
                            try { cts.Cancel(); } catch { }

                            HandleApproval(config, device);
                            return;
                        }
                        else if (result == "rejected" || result == "forbidden")
                        {
                            anyRejection = true;
                            lastRejectionDevice = device;
                            Logger.LogWarning($"[Tile] unlock_request_rejected clientId='{device.ClientId}' elapsedMs={requestTimer.ElapsedMilliseconds} requestId='{correlationRequestId}'");
                            try { cts.Cancel(); } catch { }
                            break;
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
                else if (cts.IsCancellationRequested && !approvalCompleted && !Provider.UnlockState.HasPendingUnlock && !IsAnyTileAwaitingTwoFactorPin)
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
                else if (!approvalCompleted && !Provider.UnlockState.HasPendingUnlock && !IsAnyTileAwaitingTwoFactorPin)
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
                if (!approvalCompleted && !Provider.UnlockState.HasPendingUnlock && !IsAnyTileAwaitingTwoFactorPin)
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
                    // Allow explicit user actions (button, retry shortcut, Enter) to replace the current in-flight request.
                    bool isExplicitReRequest = string.Equals(source, "button", StringComparison.OrdinalIgnoreCase)
                        || source.StartsWith("shortcut", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(source, "keyboard_enter", StringComparison.OrdinalIgnoreCase);

                    if (!isExplicitReRequest)
                    {
                        Logger.Log($"[PortalWinTile] Duplicate request suppressed for owner '{owner}' (source={source}). Active request is still in progress.");
                        return false;
                    }

                    Logger.Log($"[PortalWinTile] Re-request requested by {source} for '{owner}'. Replacing active request.");
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

                if (!ct.IsCancellationRequested)
                {
                    statusAggregator.Report(UnlockTransportStage.Searching);
                    try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
                }
            }

            if (useBluetooth && bt != null && bt.IsClientConnected(device.ClientId))
            {
                Logger.Log($"[Tile] {device.Name} (BT): Connected. Sending unlock request. requestId='{requestId}'");
                statusAggregator.Report(UnlockTransportStage.AwaitingApproval);
                var sendTimer = Stopwatch.StartNew();
                var res = await bt.RequestUnlockFromClientAsync(device.ClientId, requestId, correlationEnabled, ct);
                Logger.Log($"[Tile] bt_request_done clientId={device.ClientId} result={res ?? "null"} elapsedMs={sendTimer.ElapsedMilliseconds} requestId='{requestId}'");
                if (res != null) return (res, device);

                if (!ct.IsCancellationRequested)
                {
                    statusAggregator.Report(UnlockTransportStage.Searching);
                    try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
                }
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
        if (_isAwaiting2FaPin || IsAnyTileAwaitingTwoFactorPin)
        {
            Logger.Log("[Tile] 2FA PIN mode cancelled by user.");
            var tile = _isAwaiting2FaPin ? this : GetTileAwaitingTwoFactorPin() ?? this;
            tile.ClearTwoFactorPinMode("Cancelled by user");
            tile.UpdateStatus(Localization.T("Cancelled by user."));
            tile.ShowRequestButton();
            tile.FocusPasswordField();
            return;
        }

        double elapsedFromStartMs = (DateTime.UtcNow - _requestStartedTimestamp).TotalMilliseconds;
        if (elapsedFromStartMs < 500)
        {
            Logger.Log($"[Tile] Cancel request ignored due to start debounce ({elapsedFromStartMs:F0}ms < 500ms).");
            return;
        }

        _cancellationReason = "user";
        _activeRequestCts?.Cancel();
        DisconnectAllTransportsFast("Request cancelled by user");
        UpdateStatus("Cancelled by user.");
        ShowRequestButton();
        FocusPasswordField();
    }

    protected override void OnManualPasswordInputChanged()
    {
        base.OnManualPasswordInputChanged();

        if (_isAwaiting2FaPin || IsAnyTileAwaitingTwoFactorPin)
        {
            // User is typing 2FA PIN on keyboard; do not cancel session.
            return;
        }

        if (_activeRequestCts != null && !_activeRequestCts.IsCancellationRequested)
        {
            _cancellationReason = "manual_typing";
            Logger.Log("[Tile] Manual password input detected; cancelling active remote unlock request.");
            _activeRequestCts.Cancel();
            DisconnectAllTransportsFast("Manual password input");
            UpdateStatus("Manual password input.");
            ShowRequestButton();
        }

        FocusPasswordField();
    }

    private static void DisconnectAllTransportsFast(string reason)
    {
        CredentialProviderBootstrapper.TlsService?.DisconnectAllWebSocketClients(reason);
        CredentialProviderBootstrapper.BtService?.DisconnectAllClients(reason);
    }

    private void HandleApproval(PortalWinConfig config, Portal.Common.Models.DeviceModel targetDevice)
    {
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
                var submitSid = targetAccount.UserSid ?? User?.Sid;
                Provider.OnUnlockRequested(submitUser, securePassword, submitDomain, submitSid);
                if (_isAwaiting2FaPin || IsAnyTileAwaitingTwoFactorPin)
                {
                    return;
                }
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

        if (_isAwaiting2FaPin || IsAnyTileAwaitingTwoFactorPin)
        {
            return;
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

    private void ShowRequestButton(bool isInitial = false)
    {
        if (_requestButton != null)
        {
            _requestButton.Label = isInitial ? Localization.T("Request Remote Unlock") : Localization.T("Retry");
            _requestButton.OnClick = OnRequestUnlockClicked;
            _requestButton.AsPushButton();
            _requestButton.State = AllowsHostInitiated
                ? (Provider.UsageScenario == UsageScenario.CredUI ? FieldState.DisplayInBoth : FieldState.DisplayInSelectedTile)
                : FieldState.Hidden;
        }
        if (_cancelButton != null) _cancelButton.State = FieldState.Hidden;
        FocusPasswordField();

        if (!isInitial)
        {
            ScheduleAutoResetToReadyState(6);
        }
    }

    private void ShowCancelButton()
    {
        CancelScheduledAutoReset();

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
        FocusPasswordField();
    }

    public void ScheduleAutoResetToReadyState(int delaySeconds = 6)
    {
        CancelScheduledAutoReset();

        var cts = new CancellationTokenSource();
        _autoResetCts = cts;

        Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;

            if (_isAwaiting2FaPin || IsAnyTileAwaitingTwoFactorPin || HasActiveUnlockRequest || (_activeRequestCts != null && !_activeRequestCts.IsCancellationRequested))
            {
                return;
            }

            try
            {
                ResetToReadyState();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[PortalWinTile] Auto-reset to ready error: {ex.Message}");
            }
        }, TaskScheduler.Default);
    }

    private void CancelScheduledAutoReset()
    {
        try
        {
            _autoResetCts?.Cancel();
            _autoResetCts?.Dispose();
            _autoResetCts = null;
        }
        catch { }
    }

    public void ResetToReadyState()
    {
        CancelScheduledAutoReset();

        if (_isAwaiting2FaPin || IsAnyTileAwaitingTwoFactorPin || HasActiveUnlockRequest)
        {
            return;
        }

        var config = PortalWinConfig.Load();
        var account = ResolveCurrentTileAccount(config);
        if (account != null && account.IsExpired)
        {
            UpdateStatus(Localization.T("Account Inactive"), Localization.T("Account inactivity period exceeded. Re-enter password in Portal.Host to reactivate."));
        }
        else if (account != null && account.ResolveEffectiveUnlockMode(Provider.UnlockMode) == UnlockMode.ClientInitiated)
        {
            UpdateStatus(Localization.T("Waiting for remote command"), Localization.T("Waiting for unlock command from phone app."));
        }
        else
        {
            UpdateStatus(Localization.T("Waiting for remote command"), Localization.T("Waiting for remote unlock command"));
        }

        if (_requestButton != null)
        {
            _requestButton.Label = Localization.T("Request Remote Unlock");
            _requestButton.OnClick = OnRequestUnlockClicked;
            _requestButton.AsPushButton();
            _requestButton.State = AllowsHostInitiated
                ? (Provider.UsageScenario == UsageScenario.CredUI ? FieldState.DisplayInBoth : FieldState.DisplayInSelectedTile)
                : FieldState.Hidden;
        }
        if (_cancelButton != null) _cancelButton.State = FieldState.Hidden;

        FocusPasswordField();
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
                tile.FocusPasswordField();
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

    public static bool TryTriggerRetryShortcut(string source = "shortcut_ctrl_shift")
    {
        // An explicit user retry shortcut MUST clear the emergency rollback state
        _isEmergencyRollbackActive = false;

        PortalWinTile? targetTile;
        lock (_tilesSync)
        {
            targetTile = _selectedTile
                ?? _tiles.FirstOrDefault(t => t.IsSelected)
                ?? _tiles.FirstOrDefault(t => t.AllowsHostInitiated)
                ?? _tiles.FirstOrDefault();
        }

        if (targetTile == null)
        {
            Logger.LogWarning($"[PortalWinTile] Retry shortcut ({source}) ignored: no tile found.");
            return false;
        }

        if (!targetTile.AllowsHostInitiated)
        {
            Logger.LogWarning($"[PortalWinTile] Retry shortcut ({source}) ignored: tile does not allow host-initiated unlock (mode={targetTile.Provider.UnlockMode}).");
            return false;
        }

        // If 2FA PIN mode is active, clear it so retry restarts a clean remote session
        if (targetTile._isAwaiting2FaPin || IsAnyTileAwaitingTwoFactorPin)
        {
            var pinTile = targetTile._isAwaiting2FaPin ? targetTile : GetTileAwaitingTwoFactorPin() ?? targetTile;
            pinTile.ClearTwoFactorPinMode("Retry shortcut pressed");
        }

        // If an unlock request is already active, cancel it to allow a clean restart
        if (targetTile._activeRequestCts != null && !targetTile._activeRequestCts.IsCancellationRequested)
        {
            Logger.Log($"[PortalWinTile] Retry shortcut ({source}): cancelling existing active request to restart fresh.");
            try
            {
                targetTile._activeRequestCts.Cancel();
            }
            catch { }
        }

        // Clear any characters in the password box so tile is clean
        if (targetTile._passwordControl != null)
        {
            try
            {
                targetTile._passwordControl.Password = new System.Security.SecureString();
            }
            catch { }
        }

        Logger.Log($"[PortalWinTile] Retry shortcut ({source}) triggered; starting remote unlock retry.");
        targetTile.StartUnlockRequest(forceTakeover: true, source: source);
        return true;
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
