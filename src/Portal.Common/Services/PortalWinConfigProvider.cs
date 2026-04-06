using Portal.Common.Abstractions;

namespace Portal.Common;

/// <summary>
/// Default implementation of IConfigProvider that reads/writes PortalWinConfig to disk.
/// Provides both synchronous (backward-compat) and async methods.
/// </summary>
public class PortalWinConfigProvider : IConfigProvider
{
    public PortalWinConfig Load()
    {
        return PortalWinConfig.Load();
    }

    public async Task<PortalWinConfig> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await Task.FromResult(Load());
    }

    public void Save(PortalWinConfig config)
    {
        config.Save();
    }

    public async Task SaveAsync(PortalWinConfig config, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Save(config);
        await Task.CompletedTask;
    }
}
