using Portal.Common.Models;

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
        return value switch
        {
            "Portal started" => "Portal запущен",
            "PC unlock approved" => "Разблокировка ПК подтверждена",
            "Unlock request cancelled" => "Запрос разблокировки отменён",
            "Unlock request declined" => "Запрос разблокировки отклонён",
            "Host is ready. Pairing, unlock and network recovery events will appear here." => "Компьютер готов. Здесь будут отображаться привязка, разблокировки и восстановление сети.",
            "The remote unlock request was cancelled." => "Удалённый запрос разблокировки отменён.",
            "A paired device declined the remote unlock request." => "Привязанное устройство отклонило удалённый запрос разблокировки.",
            _ => value
        };
    }
}
