using Portal.Common.Models;
using Portal.Common.Helpers;

namespace Portal.Host.Models;

public sealed class ActivityCard
{
    public string Icon { get; init; } = "•";
    public string Title { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public string TimeText { get; init; } = string.Empty;
    public string TimeBadge { get; init; } = string.Empty;
    public string DeviceBadge { get; init; } = string.Empty;
    public string TransportBadge { get; init; } = string.Empty;
    public bool HasDeviceBadge => !string.IsNullOrWhiteSpace(DeviceBadge);
    public bool HasTransportBadge => !string.IsNullOrWhiteSpace(TransportBadge);
    public string AccentColor { get; init; } = "#4f8cff";
    public string BackgroundColor { get; init; } = "#111c31";

    public static ActivityCard FromEntry(ActivityEntry entry, bool useRussian)
    {
        var isCancellation = entry.Title.Contains("cancelled", StringComparison.OrdinalIgnoreCase);
        var isWarning = !entry.IsSuccess;
        var isCritical = isWarning && isCancellation;
        return new ActivityCard
        {
            Icon = entry.Icon,
            Title = Translate(entry.Title, useRussian),
            Details = entry.Category == "unlock" && entry.IsSuccess && !string.IsNullOrWhiteSpace(entry.DeviceName)
                ? useRussian ? "Удалённая разблокировка подтверждена." : "Remote unlock request approved."
                : Translate(entry.Details, useRussian),
            TimeText = FormatTime(entry.OccurredAtUtc.ToLocalTime(), useRussian),
            TimeBadge = FormatTime(entry.OccurredAtUtc.ToLocalTime(), useRussian),
            DeviceBadge = entry.DeviceName ?? string.Empty,
            TransportBadge = entry.Transport ?? string.Empty,
            AccentColor = isCritical ? "#F05D6F" : isWarning ? "#f2a65a" : entry.Category == "unlock" ? "#47d18c" : "#70a5ff",
            BackgroundColor = isCritical ? "#32171D" : isWarning ? "#2a1d16" : entry.Category == "unlock" ? "#11261e" : "#111c31"
        };
    }

    private static string FormatTime(DateTime localTime, bool useRussian)
    {
        var age = DateTime.Now - localTime;
        if (age < TimeSpan.FromMinutes(1)) return useRussian ? "только что" : "just now";
        if (age < TimeSpan.FromHours(1)) return useRussian ? $"{Math.Max(1, (int)age.TotalMinutes)} мин. назад" : $"{Math.Max(1, (int)age.TotalMinutes)} min ago";
        if (age < TimeSpan.FromDays(1)) return useRussian ? $"{(int)age.TotalHours} ч назад" : $"{(int)age.TotalHours} h ago";
        return localTime.ToString("dd MMM, HH:mm");
    }

    private static string Translate(string value, bool useRussian)
    {
        if (!useRussian) return value;

        var translated = Localization.T(value);
        if (!string.Equals(translated, value, StringComparison.Ordinal))
            return translated;

        const string bluetoothApprovalSuffix = " approved an unlock request over Bluetooth.";
        if (value.EndsWith(bluetoothApprovalSuffix, StringComparison.Ordinal))
            return $"Устройство {value[..^bluetoothApprovalSuffix.Length]} подтвердило запрос разблокировки по Bluetooth.";

        const string wifiApprovalSuffix = " approved an unlock request over Wi-Fi.";
        if (value.EndsWith(wifiApprovalSuffix, StringComparison.Ordinal))
            return $"Устройство {value[..^wifiApprovalSuffix.Length]} подтвердило запрос разблокировки по Wi‑Fi.";

        const string bothApprovalSuffix = " approved an unlock request over Wi-Fi or Bluetooth.";
        if (value.EndsWith(bothApprovalSuffix, StringComparison.Ordinal))
            return $"Устройство {value[..^bothApprovalSuffix.Length]} подтвердило запрос разблокировки по Wi‑Fi или Bluetooth.";

        const string bluetoothReadySuffix = " is ready to unlock this PC via Bluetooth.";
        if (value.EndsWith(bluetoothReadySuffix, StringComparison.Ordinal))
            return $"Устройство {value[..^bluetoothReadySuffix.Length]} готово разблокировать этот ПК по Bluetooth.";

        const string wifiReadySuffix = " is ready to unlock this PC via Wi-Fi.";
        if (value.EndsWith(wifiReadySuffix, StringComparison.Ordinal))
            return $"Устройство {value[..^wifiReadySuffix.Length]} готово разблокировать этот ПК по Wi‑Fi.";

        const string disabledSuffix = " is disabled in Portal settings.";
        if (value.EndsWith(disabledSuffix, StringComparison.Ordinal))
            return $"Устройство {value[..^disabledSuffix.Length]} отключено в настройках Portal.";

        return value;
    }
}
