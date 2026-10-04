namespace Portal.Common.Abstractions;

/// <summary>
/// Abstraction for mDNS service announcement.
/// </summary>
public interface IMdnsAnnouncer : IDisposable
{
    void Start(PortalWinConfig config, string mode = "pair", string? ipAddress = null, bool forceRefresh = false);
    void ReAdvertise(PortalWinConfig config, string? ipAddress = null);
    void Stop();
}
