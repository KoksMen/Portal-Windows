using Lithnet.CredentialProvider;
using Portal.Common.Helpers;
using Portal.Common.Models;

namespace Portal.CredentialProvider;

public static class CredentialProviderTilePolicy
{
    private const CredUIWinFlags UnsupportedCredUiFlags =
        CredUIWinFlags.CREDUIWIN_AUTHPACKAGE_ONLY
        | CredUIWinFlags.CREDUIWIN_IN_CRED_ONLY;

    public static bool IsUsageScenarioSupported(UsageScenario scenario, CredUIWinFlags flags)
    {
        return scenario switch
        {
            UsageScenario.Logon => true,
            UsageScenario.UnlockWorkstation => true,
            UsageScenario.CredUI => IsPasswordCompatibleCredUi(flags),
            _ => false
        };
    }

    public static bool ShouldIncludeGenericTile(UsageScenario scenario)
    {
        return false;
    }

    public static bool ShouldIncludeUserTile(UsageScenario scenario)
    {
        return scenario == UsageScenario.Logon
            || scenario == UsageScenario.UnlockWorkstation
            || scenario == UsageScenario.CredUI;
    }

    private static bool IsPasswordCompatibleCredUi(CredUIWinFlags flags)
    {
        return (flags & UnsupportedCredUiFlags) == 0;
    }

    public static DeviceAccount? ResolveApprovalAccount(
        DeviceModel targetDevice,
        string? selectedQualifiedUser,
        string? selectedUserName,
        string? typedUserName,
        string? selectedUserSid = null)
    {
        // 1. Strict SID matching
        if (!string.IsNullOrWhiteSpace(selectedUserSid))
        {
            var sidMatch = targetDevice.Accounts.FirstOrDefault(a =>
                IdentityHelper.MatchesSid(a.UserSid, selectedUserSid));
            if (sidMatch != null)
            {
                return sidMatch;
            }
        }

        // 2. Strict identity matching (canonical Domain\User or UPN)
        var targetUser = selectedQualifiedUser ?? typedUserName ?? selectedUserName;
        var targetDomain = IdentityHelper.GetDomainFromIdentity(targetUser);

        if (!string.IsNullOrWhiteSpace(targetUser) || !string.IsNullOrWhiteSpace(selectedUserSid))
        {
            var accountMatch = targetDevice.Accounts.FirstOrDefault(a =>
                IdentityHelper.MatchesAccount(selectedUserSid, targetUser, targetDomain, a));

            if (accountMatch != null)
            {
                return accountMatch;
            }

            // A specific user was requested, but did not match any configured account on this device.
            // Do NOT fall back to an arbitrary account.
            return null;
        }

        // 3. Fallback only if no specific user was selected (e.g. blank CredUI tile)
        return targetDevice.Accounts.Count == 1
            ? targetDevice.Accounts[0]
            : null;
    }
}
