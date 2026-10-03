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
        return scenario == UsageScenario.CredUI;
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
        string? typedUserName)
    {
        var canonicalUser = IdentityHelper.ToCanonical(selectedQualifiedUser)
            ?? IdentityHelper.ToCanonical(typedUserName)
            ?? IdentityHelper.ToCanonical(selectedUserName);
        var shortUser = IdentityHelper.GetShortUsername(selectedQualifiedUser)
            ?? IdentityHelper.GetShortUsername(typedUserName)
            ?? IdentityHelper.GetShortUsername(selectedUserName);

        if (!string.IsNullOrWhiteSpace(canonicalUser))
        {
            var canonicalMatch = targetDevice.Accounts.FirstOrDefault(a =>
                IdentityHelper.EqualsIgnoreCase(
                    IdentityHelper.ToCanonical(a.Username, a.Domain),
                    canonicalUser));

            if (canonicalMatch != null)
            {
                return canonicalMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(shortUser))
        {
            var shortMatch = targetDevice.Accounts.FirstOrDefault(a =>
                IdentityHelper.EqualsIgnoreCase(IdentityHelper.GetShortUsername(a.Username), shortUser));

            if (shortMatch != null)
            {
                return shortMatch;
            }
        }

        return targetDevice.Accounts.Count == 1
            ? targetDevice.Accounts[0]
            : null;
    }
}
