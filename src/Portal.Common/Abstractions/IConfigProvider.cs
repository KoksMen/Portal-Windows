namespace Portal.Common.Abstractions;

/// <summary>
/// Abstraction for loading and saving PortalWin configuration.
/// Supports both synchronous and asynchronous operations.
/// </summary>
public interface IConfigProvider
{
    PortalWinConfig Load();
    Task<PortalWinConfig> LoadAsync(CancellationToken ct = default);
    void Save(PortalWinConfig config);
    Task SaveAsync(PortalWinConfig config, CancellationToken ct = default);
}
