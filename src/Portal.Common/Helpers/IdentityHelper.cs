namespace Portal.Common.Helpers;

public static class IdentityHelper
{
    public static string? ToCanonical(string? username, string? domain = null)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        var input = username.Trim();
        if (input.Contains('\\'))
        {
            var parts = input.Split('\\', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                return $"{parts[0]}\\{parts[1]}";
        }

        // If it's a UPN (e.g. user@outlook.com or user@company.com)
        if (input.Contains('@'))
        {
            if (!string.IsNullOrWhiteSpace(domain) &&
                string.Equals(domain.Trim(), "MicrosoftAccount", StringComparison.OrdinalIgnoreCase))
            {
                return $"MicrosoftAccount\\{input}";
            }

            return input;
        }

        var shortUser = GetShortUsername(input);
        if (!string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(shortUser))
            return $"{domain.Trim()}\\{shortUser}";

        return shortUser;
    }

    public static string? GetShortUsername(string? usernameOrQualifiedName)
    {
        if (string.IsNullOrWhiteSpace(usernameOrQualifiedName))
            return null;

        var input = usernameOrQualifiedName.Trim();
        if (input.Contains('\\'))
        {
            var parts = input.Split('\\', 2, StringSplitOptions.TrimEntries);
            input = parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : input;
        }

        var atIndex = input.IndexOf('@');
        if (atIndex > 0)
        {
            return input[..atIndex];
        }

        return input;
    }

    public static string? GetDomainFromIdentity(string? usernameOrQualifiedName, string? fallbackDomain = null)
    {
        if (string.IsNullOrWhiteSpace(usernameOrQualifiedName))
            return string.IsNullOrWhiteSpace(fallbackDomain) ? null : fallbackDomain.Trim();

        var input = usernameOrQualifiedName.Trim();
        if (input.Contains('\\'))
        {
            var parts = input.Split('\\', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
                return parts[0];
        }

        var atIndex = input.IndexOf('@');
        if (atIndex > 0 && atIndex < input.Length - 1)
        {
            return input[(atIndex + 1)..];
        }

        return string.IsNullOrWhiteSpace(fallbackDomain) ? null : fallbackDomain.Trim();
    }

    public static bool IsUpn(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return false;
        var at = identity.IndexOf('@');
        return at > 0 && at < identity.Length - 1 && !identity.Contains('\\');
    }

    public static bool IsMicrosoftAccount(string? identity, string? domain = null)
    {
        if (string.IsNullOrWhiteSpace(identity)) return false;
        if (string.Equals(domain, "MicrosoftAccount", StringComparison.OrdinalIgnoreCase)) return true;
        if (identity.StartsWith("MicrosoftAccount\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool EqualsIgnoreCase(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesUser(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        if (string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        var shortLeft = GetShortUsername(left);
        var shortRight = GetShortUsername(right);
        return !string.IsNullOrWhiteSpace(shortLeft)
            && !string.IsNullOrWhiteSpace(shortRight)
            && string.Equals(shortLeft, shortRight, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Safely resolves the Windows Security Identifier (SID) for a given user identity.
    /// Handles Local accounts, Domain accounts, and MSA profiles without throwing exceptions.
    /// </summary>
    public static string? TryResolveUserSid(string? username, string? domain = null)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        try
        {
            // 1. Try NTAccount translation with domain\username if domain is specified
            var effectiveName = string.IsNullOrWhiteSpace(domain) ? username.Trim() : $"{domain.Trim()}\\{username.Trim()}";
            try
            {
                var ntAccount = new System.Security.Principal.NTAccount(effectiveName);
                var sid = (System.Security.Principal.SecurityIdentifier)ntAccount.Translate(typeof(System.Security.Principal.SecurityIdentifier));
                return sid.Value;
            }
            catch { }

            // 2. If domain-qualified lookup failed, try with short username
            var shortName = GetShortUsername(username);
            if (!string.IsNullOrWhiteSpace(shortName) && !string.Equals(shortName, effectiveName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var ntAccount = new System.Security.Principal.NTAccount(shortName);
                    var sid = (System.Security.Principal.SecurityIdentifier)ntAccount.Translate(typeof(System.Security.Principal.SecurityIdentifier));
                    return sid.Value;
                }
                catch { }
            }

            // 3. Fallback: check current Windows session identity
            try
            {
                using var currentIdentity = System.Security.Principal.WindowsIdentity.GetCurrent();
                if (currentIdentity.User != null && MatchesUser(currentIdentity.Name, username))
                {
                    return currentIdentity.User.Value;
                }
            }
            catch { }
        }
        catch { }

        return null;
    }

    public static bool MatchesSid(string? sidA, string? sidB)
    {
        return !string.IsNullOrWhiteSpace(sidA)
            && !string.IsNullOrWhiteSpace(sidB)
            && string.Equals(sidA.Trim(), sidB.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
