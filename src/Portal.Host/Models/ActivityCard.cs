using Portal.Common.Models;

namespace Portal.Host.Models;

public sealed class ActivityCard
{
    public string Icon { get; init; } = "•";
    public string Title { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public string TimeText { get; init; } = string.Empty;
    public string AccentColor { get; init; } = "#4f8cff";
    public string BackgroundColor { get; init; } = "#111c31";

    public static ActivityCard FromEntry(ActivityEntry entry)
    {
        var isWarning = !entry.IsSuccess;
        return new ActivityCard
        {
            Icon = entry.Icon,
            Title = entry.Title,
            Details = entry.Details,
            TimeText = FormatTime(entry.OccurredAtUtc.ToLocalTime()),
            AccentColor = isWarning ? "#f2a65a" : entry.Category == "unlock" ? "#47d18c" : "#70a5ff",
            BackgroundColor = isWarning ? "#2a1d16" : entry.Category == "unlock" ? "#11261e" : "#111c31"
        };
    }

    private static string FormatTime(DateTime localTime)
    {
        var age = DateTime.Now - localTime;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)age.TotalMinutes)} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        return localTime.ToString("dd MMM, HH:mm");
    }
}
