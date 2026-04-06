namespace Portal.Common.Abstractions;

/// <summary>
/// Abstraction for mDNS service announcement.
/// </summary>
public interface IMdnsAnnouncer : IDisposable
{
    void Start(PortalWinConfig config, string mode = "pair", string? ipAddress = null);
    void Stop();
}
