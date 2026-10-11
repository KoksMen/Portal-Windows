using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Portal.Common.Helpers;

namespace Portal.Common.Models;

public class UserPasswordExpiryStatus
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("domain")]
    public string? Domain { get; set; }

    [JsonPropertyName("userSid")]
    public string? UserSid { get; set; }

    [JsonPropertyName("daysUntilExpiry")]
    public int? DaysUntilExpiry { get; set; }

    [JsonPropertyName("isExpired")]
    public bool IsExpired { get; set; }

    [JsonPropertyName("isNeverExpires")]
    public bool IsNeverExpires { get; set; }

    [JsonPropertyName("checkedAtUtc")]
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
}

public class PasswordStatusReport
{
    [JsonPropertyName("checkedAtUtc")]
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("accounts")]
    public List<UserPasswordExpiryStatus> Accounts { get; set; } = new();
}

public static class PasswordStatusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string StatusFilePath => Path.Combine(PortalStoragePaths.RuntimeDirectory, "password_status.json");

    public static PasswordStatusReport? ReadCurrentStatus()
    {
        try
        {
            var path = StatusFilePath;
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PasswordStatusReport>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[PasswordStatusStore] Failed to read password_status.json: {ex.Message}");
            return null;
        }
    }

    public static void SaveStatus(PasswordStatusReport report)
    {
        try
        {
            Directory.CreateDirectory(PortalStoragePaths.RuntimeDirectory);
            var json = JsonSerializer.Serialize(report, JsonOptions);
            File.WriteAllText(StatusFilePath, json);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[PasswordStatusStore] Failed to write password_status.json: {ex.Message}");
        }
    }

    public static UserPasswordExpiryStatus? GetAccountExpiryStatus(string username, string? domain, string? userSid)
    {
        var report = ReadCurrentStatus();
        if (report == null || report.Accounts == null || report.Accounts.Count == 0)
        {
            return null;
        }

        // Match by SID first
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            var matchBySid = report.Accounts.FirstOrDefault(a => string.Equals(a.UserSid, userSid, StringComparison.OrdinalIgnoreCase));
            if (matchBySid != null) return matchBySid;
        }

        // Match by Username & Domain
        var shortTarget = IdentityHelper.GetShortUsername(username);
        return report.Accounts.FirstOrDefault(a =>
            IdentityHelper.EqualsIgnoreCase(shortTarget, IdentityHelper.GetShortUsername(a.Username)) &&
            (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(a.Domain) || string.Equals(domain, a.Domain, StringComparison.OrdinalIgnoreCase)));
    }
}
