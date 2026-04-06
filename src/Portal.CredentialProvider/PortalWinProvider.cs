using System.Runtime.InteropServices;
using Lithnet.CredentialProvider;
using System.IO;
using System.Drawing;
using System.Security;
using Microsoft.Win32;
using Portal.Common;
using Portal.Common.Helpers;
using Portal.CredentialProvider.Services;
using Portal.CredentialProvider.Base;

namespace Portal.CredentialProvider;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("PortalWin.Provider")]
[Guid("4F507F6A-5A02-4F19-86B3-1C04F0E8C2E5")]
public class PortalWinProvider : PortalWinProviderBase
{
    internal UnlockMode UnlockMode { get; private set; } = UnlockMode.ClientInitiated;
    internal HostRequestTrigger HostRequestTrigger { get; private set; } = HostRequestTrigger.OnClick;

    private string? _preferredDefaultSid;
    private string? _preferredDefaultCanonicalUser;
    private string? _preferredDefaultShortUser;

    public override bool IsUsageScenarioSupported(UsageScenario cpus, CredUIWinFlags dwFlags)
    {
        Logger.Log($"[PortalWinProvider] IsUsageScenarioSupported: {cpus}, Flags: {dwFlags}");
        try
        {
            var config = PortalWinConfig.Load();
            UnlockMode = config.UnlockMode;
            HostRequestTrigger = config.HostRequestTrigger;

            // Ensure the auto-request can fire again for this session
            PortalWinTile.ResetAutoRequestClaim();
            ResolvePreferredDefaultUser();

            var supported = cpus switch
            {
                UsageScenario.Logon => true,
                UsageScenario.UnlockWorkstation => true,
                UsageScenario.CredUI => true,
                _ => false
            };
            Logger.Log($"[PortalWinProvider] Scenario supported? {supported}");
            return supported;
        }
        catch (Exception ex)
        {
            Logger.LogError("[PortalWinProvider] IsUsageScenarioSupported Error", ex);
            return false;
        }
    }

    public override IEnumerable<ControlBase> GetControls(UsageScenario cpus)
    {
        Logger.Log($"[PortalWinProvider] GetControls called for scenario: {cpus}");

        yield return new CredentialProviderLabelControl("ProviderLabel", "PortalWin Remote Unlock");

        var logoImage = LoadLogo();
        if (logoImage != null)
            yield return new UserTileControl("Logo", null, logoImage);

        string statusHeadline = BuildStatusHeadline();
        string statusDetails = BuildStatusDetails();
        Logger.Log($"[PortalWinProvider] Initial Status: {statusHeadline} | {statusDetails}");

        var statusLabel = new SmallLabelControl("StatusLabel", statusHeadline);
        statusLabel.State = FieldState.DisplayInBoth;
        yield return statusLabel;

        var versionLabel = new SmallLabelControl("VersionLabel", $"Ver: {GetProjectVersionText()}");
        versionLabel.State = FieldState.DisplayInBoth;
        yield return versionLabel;

        var statusDetailsLabel = new SmallLabelControl("StatusDetailsLabel", statusDetails);
        statusDetailsLabel.State = FieldState.Hidden;
        yield return statusDetailsLabel;

        var showDetailsButton = new CommandLinkControl("ShowDetailsButton", "Show details");
        showDetailsButton.State = FieldState.DisplayInSelectedTile;
        yield return showDetailsButton;

        var hideDetailsButton = new CommandLinkControl("HideDetailsButton", "Hide details");
        hideDetailsButton.State = FieldState.Hidden;
        yield return hideDetailsButton;

        // Host-initiated controls (shown only when needed)
        var reqButton = new CommandLinkControl("RequestButton", "Request Remote Unlock");
        reqButton.State = UnlockMode == UnlockMode.HostInitiated || UnlockMode == UnlockMode.Both
            ? FieldState.DisplayInSelectedTile
            : FieldState.Hidden;
        yield return reqButton;

        var cancelButton = new CommandLinkControl("CancelButton", "Cancel Request");
        cancelButton.State = FieldState.Hidden;
        yield return cancelButton;

        var usernameField = new TextboxControl("UsernameField", "Username");
        usernameField.State = FieldState.Hidden;
        yield return usernameField;

        var passwordField = new SecurePasswordTextboxControl("PasswordField", "Password");
        passwordField.State = FieldState.DisplayInSelectedTile;
        yield return passwordField;

        yield return new SubmitButtonControl("SubmitButton", "Unlock", passwordField);
    }

    public override bool ShouldIncludeGenericTile() => true;
    public override bool ShouldIncludeUserTile(CredentialProviderUser user) => true;
    public override CredentialTile CreateGenericTile() => new PortalWinTile(this);
    public override CredentialTile2 CreateUserTile(CredentialProviderUser user)
    {
        var tile = new PortalWinTile(this, user);

        // Pre-assign default tile based on last logged-on user so early auto-flow can start sooner.
        if (ShouldBePreferredDefaultUser(user))
        {
            if (this.DefaultTile == null || this.DefaultTile.IsGenericTile)
            {
                this.DefaultTile = tile;
                this.DefaultTileAutoLogon = false;
                Logger.Log($"[PortalWinProvider] Assigned startup default tile to '{user.QualifiedUserName ?? user.UserName ?? "Unknown"}'.");
            }
        }

        return tile;
    }

    protected internal override void OnUnlockRequested(string username, SecureString? password, string domain)
    {
        Logger.Log($"[PortalWinProvider] OnUnlockRequested for user: '{username}', domain: '{domain}'");
        CredentialProviderBootstrapper.TlsService?.DisconnectAllWebSocketClients("Unlock approved");
        UnlockState.SetPending(username, password, domain);

        try
        {
            var targetTile = FindMatchingTile<PortalWinTile>(username, domain);
            if (targetTile != null)
            {
                Logger.Log("[PortalWinProvider] Triggering auto-logon on target tile.");
                targetTile.UpdateStatus(BuildStatusHeadlineForState("Remote unlock approved"), BuildStatusDetailsForState("Preparing Windows sign-in..."));
                this.SetDefaultTile(targetTile, autoLogon: true);
                Logger.Log("[PortalWinProvider] Cursor recovery scheduled after unlock approval.");
                CursorRecoveryService.TriggerAfterUnlock("Unlock approved");
            }
            else
            {
                Logger.LogError("[PortalWinProvider] Failed to find any suitable tile to trigger unlock.");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[PortalWinProvider] Critical Failed to trigger auto-logon", ex);
        }
    }

    protected internal override void OnTransportStatusChanged()
    {
        RefreshAllTileStatuses();
    }

    internal void RefreshAllTileStatuses()
    {
        if (Tiles == null || Tiles.Count == 0)
        {
            return;
        }

        foreach (var tile in Tiles.OfType<PortalWinTileBase>())
        {
            tile.RefreshStatusFromProvider();
        }
    }

    internal string BuildStatusHeadlineForState(string? rawStatus)
    {
        return BuildStatusHeadline(NormalizeHeadline(rawStatus));
    }

    internal string BuildStatusDetailsForState(string? rawStatus)
    {
        return BuildStatusDetails(NormalizeState(rawStatus));
    }

    private string BuildStatusHeadline(string? headline = null)
    {
        var state = string.IsNullOrWhiteSpace(headline) ? "Searching device" : headline.Trim();
        return $"State: {state}";
    }

    private string BuildStatusDetails(string? stateOverride = null)
    {
        var tlsService = CredentialProviderBootstrapper.TlsService;
        var btService = CredentialProviderBootstrapper.BtService;

        int networkClients = tlsService?.GetConnectedClientCount() ?? 0;
        int btClients = btService?.GetConnectedClientCount() ?? 0;
        string networkState = tlsService is { IsRunning: true } ? "ON" : "OFF";
        string btState = btService is { IsRunning: true } ? "ON" : "OFF";

        _ = stateOverride;

        return $"Network: {networkState} ({networkClients})\nBluetooth: {btState} ({btClients})";
    }

    private static string NormalizeHeadline(string? rawStatus)
    {
        return NormalizeState(rawStatus);
    }

    private static string NormalizeState(string? rawStatus)
    {
        if (string.IsNullOrWhiteSpace(rawStatus))
        {
            return "Searching device";
        }

        var value = rawStatus.Trim().TrimEnd('.');
        var lower = value.ToLowerInvariant();

        return lower switch
        {
            var text when text.Contains("timed out") => "Request timed out",
            var text when text.Contains("denied")
                       || text.Contains("rejected")
                       || text.Contains("forbidden")
                       || text.Contains("cancelled")
                       || text.Contains("error") => "Request denied",
            var text when text.Contains("awaiting approval")
                       || text.Contains("approved") => "Awaiting unlock approval",
            _ => "Searching device"
        };
    }

    private static string GetProjectVersionText()
    {
        var version = typeof(PortalWinProvider).Assembly.GetName().Version;
        return version != null ? version.ToString(3) : "unknown";
    }

    private void ResolvePreferredDefaultUser()
    {
        _preferredDefaultSid = null;
        _preferredDefaultCanonicalUser = null;
        _preferredDefaultShortUser = null;

        _preferredDefaultSid = ReadLastLoggedOnUserSid();
        if (!string.IsNullOrWhiteSpace(_preferredDefaultSid))
        {
            Logger.Log($"[PortalWinProvider] Preferred default SID from registry: '{_preferredDefaultSid}'.");
        }

        foreach (var candidate in ReadLastLoggedOnUserCandidates())
        {
            var canonical = IdentityHelper.ToCanonical(candidate);
            var shortUser = GetShortUserName(candidate);
            if (!string.IsNullOrEmpty(canonical) || !string.IsNullOrEmpty(shortUser))
            {
                _preferredDefaultCanonicalUser = canonical;
                _preferredDefaultShortUser = shortUser;
                Logger.Log($"[PortalWinProvider] Preferred default user from registry: '{candidate}' -> canonical='{canonical ?? "null"}', short='{shortUser ?? "null"}'.");
                return;
            }
        }
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
        catch
        {
            return null;
        }
    }

    private IEnumerable<string> ReadLastLoggedOnUserCandidates()
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
            Logger.LogWarning($"[PortalWinProvider] Failed to read LogonUI registry values: {ex.Message}");
        }

        return result;
    }

    private bool ShouldBePreferredDefaultUser(CredentialProviderUser user)
    {
        if (!string.IsNullOrWhiteSpace(_preferredDefaultSid) && !string.IsNullOrWhiteSpace(user.Sid))
        {
            return string.Equals(user.Sid, _preferredDefaultSid, StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrEmpty(_preferredDefaultCanonicalUser) && string.IsNullOrEmpty(_preferredDefaultShortUser))
        {
            return false;
        }

        var userCanonical = IdentityHelper.ToCanonical(user.QualifiedUserName) ?? IdentityHelper.ToCanonical(user.UserName);
        var userShort = GetShortUserName(user.QualifiedUserName) ?? GetShortUserName(user.UserName);

        if (IdentityHelper.EqualsIgnoreCase(userCanonical, _preferredDefaultCanonicalUser))
        {
            return true;
        }

        if (IdentityHelper.EqualsIgnoreCase(userShort, _preferredDefaultShortUser))
        {
            return true;
        }

        return false;
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
}
