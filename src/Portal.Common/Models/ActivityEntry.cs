namespace Portal.Common.Models;

/// <summary>
/// A privacy-conscious user-facing activity record. It intentionally never contains passwords,
/// certificate material, IP addresses, or request bodies.
/// </summary>
public sealed class ActivityEntry
{
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string Category { get; set; } = "system";
    public string Icon { get; set; } = "•";
    public string Title { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? Transport { get; set; }
    public bool IsSuccess { get; set; } = true;
}
