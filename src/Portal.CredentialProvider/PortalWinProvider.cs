using System.Runtime.InteropServices;
using Lithnet.CredentialProvider;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
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
    internal UnlockMode UnlockMode { get; private set; } = UnlockMode.Both;
    internal HostRequestTrigger HostRequestTrigger { get; private set; } = HostRequestTrigger.OnClickAndAnyLockScreen;

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
            Localization.SetCurrentLanguage(config.UiLanguage);

            // Ensure the auto-request can fire again for this session
            PortalWinTile.ResetAutoRequestClaim();
            ResolvePreferredDefaultUser();

            var supported = CredentialProviderTilePolicy.IsUsageScenarioSupported(cpus, dwFlags);
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

        yield return new CredentialProviderLabelControl("ProviderLabel", Localization.T("PortalWin Remote Unlock"));

        var logoImage = LoadLogo();
        if (logoImage != null)
            yield return new UserTileControl("Logo", null, logoImage);

        string statusHeadline = BuildStatusHeadline();
        string statusDetails = BuildStatusDetails();
        Logger.Log($"[PortalWinProvider] Initial Status: {statusHeadline} | {statusDetails}");

        var statusLabel = new SmallLabelControl("StatusLabel", statusHeadline);
        statusLabel.State = FieldState.DisplayInBoth;
        yield return statusLabel;

        var versionLabel = new SmallLabelControl("VersionLabel", Localization.T("Ver: ") + GetProjectVersionText());
        versionLabel.State = FieldState.DisplayInBoth;
        yield return versionLabel;

        var statusDetailsLabel = new SmallLabelControl("StatusDetailsLabel", statusDetails);
        statusDetailsLabel.State = FieldState.Hidden;
        yield return statusDetailsLabel;

        var showDetailsButton = new CommandLinkControl("ShowDetailsButton", Localization.T("Show details"));
        showDetailsButton.State = FieldState.DisplayInSelectedTile;
        yield return showDetailsButton;

        var hideDetailsButton = new CommandLinkControl("HideDetailsButton", Localization.T("Hide details"));
        hideDetailsButton.State = FieldState.Hidden;
        yield return hideDetailsButton;

        var usernameField = new TextboxControl("UsernameField", Localization.T("Username"));
        usernameField.State = cpus == UsageScenario.CredUI
            ? FieldState.DisplayInSelectedTile
            : FieldState.Hidden;
        yield return usernameField;

        var passwordField = new SecurePasswordTextboxControl("PasswordField", Localization.T("Password"));
        passwordField.State = FieldState.DisplayInSelectedTile;
        yield return passwordField;

        yield return new SubmitButtonControl("SubmitButton", Localization.T("Unlock"), passwordField);

        // Host-initiated action push buttons placed AFTER password and submit button
        // so Windows LogonUI styles them with CredentialActionButtonStyle (native rounded button)
        var reqButton = new CommandLinkControl("RequestButton", Localization.T("Retry")).AsPushButton();
        reqButton.State = UnlockMode == UnlockMode.HostInitiated || UnlockMode == UnlockMode.Both
            ? (cpus == UsageScenario.CredUI ? FieldState.DisplayInBoth : FieldState.DisplayInSelectedTile)
            : FieldState.Hidden;
        yield return reqButton;

        var cancelButton = new CommandLinkControl("CancelButton", Localization.T("Cancel Request")).AsPushButton();
        cancelButton.State = FieldState.Hidden;
        yield return cancelButton;
    }

    public override bool ShouldIncludeGenericTile() => CredentialProviderTilePolicy.ShouldIncludeGenericTile(UsageScenario);
    public override bool ShouldIncludeUserTile(CredentialProviderUser user) => CredentialProviderTilePolicy.ShouldIncludeUserTile(UsageScenario);
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
        PortalWinTile.CancelGlobalActiveRequest();

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
        if (!string.IsNullOrWhiteSpace(rawStatus))
        {
            int bracketIndex = rawStatus.IndexOf('[');
            if (bracketIndex > 0)
            {
                string basePart = rawStatus.Substring(0, bracketIndex).Trim();
                string progressTag = rawStatus.Substring(bracketIndex).Trim();
                string normalizedBase = BuildStatusHeadline(NormalizeHeadline(basePart));

                if (progressTag.StartsWith("[progress:", StringComparison.OrdinalIgnoreCase) && progressTag.EndsWith("]"))
                {
                    string innerTag = progressTag.Substring("[progress:".Length, progressTag.Length - "[progress:".Length - 1);
                    string dynamicBar = BuildDynamicProgressBar(normalizedBase, innerTag);
                    return $"{normalizedBase}\n{dynamicBar}";
                }
                else
                {
                    // Fallback for any legacy bracket format: render symmetrically without leading spaces
                    string cleanProgress = progressTag.Trim();
                    return $"{normalizedBase}\n{cleanProgress}";
                }
            }
        }

        return BuildStatusHeadline(NormalizeHeadline(rawStatus));
    }

    private static string BuildDynamicProgressBar(string headline, string innerTag)
    {
        double targetWidth = EstimateVisualWidth(headline);

        if (innerTag.StartsWith("countdown,", StringComparison.OrdinalIgnoreCase))
        {
            var parts = innerTag.Substring("countdown,".Length).Split(',');
            if (parts.Length >= 3 &&
                int.TryParse(parts[0], out int remainingSeconds) &&
                int.TryParse(parts[1], out int totalSeconds) &&
                int.TryParse(parts[2], out int elapsedSeconds))
            {
                string timeText = $"{remainingSeconds / 60}:{remainingSeconds % 60:D2}";
                double fixedWidth = EstimateVisualWidth($"[  {timeText}  ]");
                double availableWidth = Math.Max(0, targetWidth - fixedWidth);

                const double blockWidth = 11.5;
                int nBase = Math.Max(3, (int)Math.Round(availableWidth / (2.0 * blockWidth)));

                int bestN = nBase;
                int bestSpacing = 2;
                double minDiff = double.MaxValue;

                for (int candN = Math.Max(3, nBase - 1); candN <= nBase + 1; candN++)
                {
                    for (int sp = 1; sp <= 3; sp++)
                    {
                        string spStr = new string('\u00A0', sp);
                        double candWidth = EstimateVisualWidth($"[{new string('█', candN)}{spStr}{timeText}{spStr}{new string('█', candN)}]");
                        double diff = Math.Abs(candWidth - targetWidth);
                        if (diff < minDiff)
                        {
                            minDiff = diff;
                            bestN = candN;
                            bestSpacing = sp;
                        }
                    }
                }

                int n = bestN;
                int totalBlocks = n * 2;

                int filledBlocks = remainingSeconds > 0
                    ? Math.Clamp((int)Math.Ceiling((double)remainingSeconds / totalSeconds * totalBlocks), 1, totalBlocks)
                    : 0;

                int activeIndex = remainingSeconds > 0 ? filledBlocks - 1 : -1;
                bool isBlink = (elapsedSeconds % 2 == 1);

                Span<char> blocks = stackalloc char[totalBlocks];
                for (int i = 0; i < totalBlocks; i++)
                {
                    if (i < activeIndex)
                    {
                        blocks[i] = '█';
                    }
                    else if (i == activeIndex)
                    {
                        blocks[i] = isBlink ? '▒' : '█';
                    }
                    else
                    {
                        blocks[i] = '░';
                    }
                }

                string spacingStr = new string('\u00A0', bestSpacing);
                string leftSide = new string(blocks[..n]);
                string rightSide = new string(blocks[n..]);

                return $"[{leftSide}{spacingStr}{timeText}{spacingStr}{rightSide}]";
            }
        }
        else if (innerTag.StartsWith("infinite,", StringComparison.OrdinalIgnoreCase))
        {
            var part = innerTag.Substring("infinite,".Length);
            if (int.TryParse(part, out int elapsedSeconds))
            {
                string elapsedText = $"(∞)\u00A0{elapsedSeconds / 60}:{elapsedSeconds % 60:D2}\u00A0(∞)";
                double fixedWidth = EstimateVisualWidth($"[\u00A0\u00A0{elapsedText}\u00A0\u00A0]");
                double availableWidth = Math.Max(0, targetWidth - fixedWidth);

                const double blockWidth = 11.5;
                int nBase = Math.Max(3, (int)Math.Round(availableWidth / (2.0 * blockWidth)));

                int bestN = nBase;
                int bestSpacing = 2;
                double minDiff = double.MaxValue;

                for (int candN = Math.Max(3, nBase - 1); candN <= nBase + 1; candN++)
                {
                    for (int sp = 1; sp <= 3; sp++)
                    {
                        string spStr = new string('\u00A0', sp);
                        double candWidth = EstimateVisualWidth($"[{new string('░', candN)}{spStr}{elapsedText}{spStr}{new string('░', candN)}]");
                        double diff = Math.Abs(candWidth - targetWidth);
                        if (diff < minDiff)
                        {
                            minDiff = diff;
                            bestN = candN;
                            bestSpacing = sp;
                        }
                    }
                }

                int n = bestN;
                int cycle = (n - 1) * 2;
                if (cycle <= 0) cycle = 1;
                int step = elapsedSeconds % cycle;
                int pulsePos = step < (n - 1) ? step : cycle - step;

                Span<char> left = stackalloc char[n];
                Span<char> right = stackalloc char[n];
                left.Fill('░');
                right.Fill('░');

                left[n - 1 - pulsePos] = '█';
                right[pulsePos] = '█';

                string spacingStr = new string('\u00A0', bestSpacing);
                return $"[{new string(left)}{spacingStr}{elapsedText}{spacingStr}{new string(right)}]";
            }
        }

        return $"[{innerTag}]";
    }

    private static double EstimateVisualWidth(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        double width = 0;
        foreach (char c in s)
        {
            width += c switch
            {
                ' ' or '\u00A0' => 4.0,
                '█' or '░' or '▒' or '▓' => 11.5,
                '[' or ']' => 4.5,
                ':' or '.' or ',' or ';' or '!' => 3.5,
                >= '0' and <= '9' => 7.5,
                '(' or ')' => 5.0,
                '∞' => 11.0,
                'ж' or 'ш' or 'щ' or 'ю' or 'ы' or 'Ж' or 'Ш' or 'Щ' or 'Ю' or 'Ы' or 'Ф' or 'W' or 'M' => 12.0,
                >= 'А' and <= 'Я' => 10.5,
                'т' or 'с' or 'г' => 7.5,
                >= 'а' and <= 'я' => 9.0,
                >= 'A' and <= 'Z' => 10.0,
                'i' or 'l' or 't' or 'j' or 'f' or 'r' => 5.0,
                'w' or 'm' => 12.0,
                >= 'a' and <= 'z' => 8.5,
                _ => 8.5
            };
        }
        return width;
    }

    internal string BuildStatusDetailsForState(string? rawStatus)
    {
        return BuildStatusDetails(NormalizeState(rawStatus));
    }

    private string BuildStatusHeadline(string? headline = null)
    {
        var state = string.IsNullOrWhiteSpace(headline) ? "Searching device" : headline.Trim();
        var statePrefix = Localization.T("State: ");
        if (state.StartsWith(statePrefix, StringComparison.OrdinalIgnoreCase))
        {
            state = state.Substring(statePrefix.Length).Trim();
        }
        if (state.StartsWith("State: ", StringComparison.OrdinalIgnoreCase))
        {
            state = state.Substring("State: ".Length).Trim();
        }
        return $"{statePrefix}{Localization.T(state)}";
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

        return $"{Localization.T("Network: ")}{networkState} ({networkClients})\nBluetooth: {btState} ({btClients})";
    }

    private static string NormalizeHeadline(string? rawStatus)
    {
        return NormalizeState(rawStatus);
    }

    private static string NormalizeState(string? rawStatus)
    {
        if (PortalWinTile.IsEmergencyRollbackActive)
        {
            return "Emergency rollback: request cancelled";
        }

        if (string.IsNullOrWhiteSpace(rawStatus))
        {
            return "Searching device";
        }

        var value = rawStatus.Trim().TrimEnd('.');
        var statePrefix = Localization.T("State: ");
        if (value.StartsWith(statePrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(statePrefix.Length).Trim();
        }
        if (value.StartsWith("State: ", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring("State: ".Length).Trim();
        }

        var lower = value.ToLowerInvariant();

        return lower switch
        {
            var text when text.Contains("emergency rollback") || text.Contains("аварийный") || text.Contains("откат")
                => "Emergency rollback: request cancelled",
            var text when text.Contains("manual") || text.Contains("ввод пароля")
                => "Manual password input",
            var text when text.Contains("user switch") || text.Contains("tile switch") || text.Contains("смена пользователя")
                => "Cancelled (user switch)",
            var text when text.Contains("by user") || text.Contains("пользователем")
                => "Cancelled by user",
            var text when text.Contains("unreachable") || text.Contains("недоступно")
                => "Device unreachable",
            var text when text.StartsWith("declined by") || text.StartsWith("отклонено")
                => value,
            var text when text.Contains("timed out") || text.Contains("время истекло") || text.Contains("таймаут")
                => "Request timed out",
            var text when text.Contains("denied")
                       || text.Contains("rejected")
                       || text.Contains("forbidden")
                       || text.Contains("отклонен")
                       || text.Contains("отклонён")
                       || text.Contains("запрещен")
                       || text.Contains("запрещён") => "Request denied",
            var text when text.Contains("cancelled")
                       || text.Contains("canceled")
                       || text.Contains("отменен")
                       || text.Contains("отменён") => "Request cancelled",
            var text when text.Contains("awaiting approval")
                       || text.Contains("approved")
                       || text.Contains("подтверждения")
                       || text.Contains("подтверждено") => "Awaiting unlock approval",
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
        return IdentityHelper.GetShortUsername(userOrUpn);
    }
}
