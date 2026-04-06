namespace Portal.Common.Helpers;

public static class IdentityHelper
{
    public static string? ToCanonical(string? username, string? domain = null)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        var input = username.Trim();
        if (input.Contains("\\"))
        {
            var parts = input.Split('\\', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                return $"{parts[0]}\\{parts[1]}";
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
        if (input.Contains("\\"))
        {
            var parts = input.Split('\\', 2, StringSplitOptions.TrimEntries);
            return parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;
        }

        return input;
    }

    public static string? GetDomainFromIdentity(string? usernameOrQualifiedName, string? fallbackDomain = null)
    {
        if (!string.IsNullOrWhiteSpace(usernameOrQualifiedName) && usernameOrQualifiedName.Contains("\\"))
        {
            var parts = usernameOrQualifiedName.Split('\\', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
                return parts[0];
        }

        return string.IsNullOrWhiteSpace(fallbackDomain) ? null : fallbackDomain.Trim();
    }

    public static bool EqualsIgnoreCase(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
