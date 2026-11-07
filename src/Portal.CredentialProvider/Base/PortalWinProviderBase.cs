using Portal.Common.Models;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Lithnet.CredentialProvider;
using Portal.Common;
using Portal.Common.Helpers;
using Portal.CredentialProvider.Services;

namespace Portal.CredentialProvider.Base;

public abstract class PortalWinProviderBase : CredentialProviderBase
{
    internal PendingUnlockState UnlockState { get; } = new();

    internal void ConsumePendingAutoLogon()
    {
        UnlockState.ConsumeAutoLogon();
        DefaultTileAutoLogon = false;
    }

    protected static readonly TimeSpan EarlyUnlockTtl = TimeSpan.FromSeconds(15);
    protected readonly object _earlyUnlockLock = new();
    protected EarlyUnlockRequest? _pendingEarlyUnlock;
    private CancellationTokenSource? _earlyUnlockWatchCts;

    internal bool HasPendingEarlyUnlock
    {
        get
        {
            if (CredentialProviderBootstrapper.HasPendingUnlock)
            {
                return true;
            }

            lock (_earlyUnlockLock)
            {
                return _pendingEarlyUnlock != null && !_pendingEarlyUnlock.IsExpired(EarlyUnlockTtl);
            }
        }
    }

    internal void CancelPendingAutoLogon()
    {
        ClearPendingEarlyUnlock("CancelPendingAutoLogon");
        CredentialProviderBootstrapper.ClearPendingUnlock("CancelPendingAutoLogon");
        UnlockState.Clear();
        DefaultTileAutoLogon = false;
    }

    internal void SetPendingEarlyUnlock(string username, SecureString? password, string domain, string? userSid)
    {
        lock (_earlyUnlockLock)
        {
            _earlyUnlockWatchCts?.Cancel();
            _earlyUnlockWatchCts?.Dispose();
            _earlyUnlockWatchCts = null;

            _pendingEarlyUnlock?.Dispose();
            _pendingEarlyUnlock = new EarlyUnlockRequest(username, password, domain, userSid);

            Logger.Log($"[{GetType().Name}] Buffered early unlock request for user '{username}' (Domain: '{domain}', SID: '{userSid ?? "none"}', TTL: {EarlyUnlockTtl.TotalSeconds:F0}s).");

            var cts = new CancellationTokenSource();
            _earlyUnlockWatchCts = cts;

            // Background watcher: checks periodically until tiles collection is materialized or TTL expires
            _ = Task.Run(async () =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (!cts.Token.IsCancellationRequested && sw.Elapsed < EarlyUnlockTtl)
                    {
                        await Task.Delay(25, cts.Token).ConfigureAwait(false);

                        if (this.Tiles != null && this.Tiles.Count > 0)
                        {
                            if (TryApplyPendingEarlyUnlock("BackgroundWatcher"))
                            {
                                return;
                            }
                        }
                    }

                    if (sw.Elapsed >= EarlyUnlockTtl)
                    {
                        lock (_earlyUnlockLock)
                        {
                            if (_pendingEarlyUnlock != null && _pendingEarlyUnlock.IsExpired(EarlyUnlockTtl))
                            {
                                Logger.LogWarning($"[{GetType().Name}] Pending early unlock for '{_pendingEarlyUnlock.Username}' expired after {EarlyUnlockTtl.TotalSeconds:F0}s timeout.");
                                _pendingEarlyUnlock.Dispose();
                                _pendingEarlyUnlock = null;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Watcher cancelled normally
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[{GetType().Name}] Error in early unlock watcher loop", ex);
                }
            });
        }
    }

    public bool TryApplyPendingEarlyUnlock(string source = "TileReadiness")
    {
        EarlyUnlockRequest? requestToApply = null;

        lock (_earlyUnlockLock)
        {
            if (_pendingEarlyUnlock == null)
            {
                return false;
            }

            if (_pendingEarlyUnlock.IsExpired(EarlyUnlockTtl))
            {
                Logger.LogWarning($"[{GetType().Name}] Discarded expired pending early unlock for '{_pendingEarlyUnlock.Username}' during {source}.");
                _pendingEarlyUnlock.Dispose();
                _pendingEarlyUnlock = null;
                return false;
            }

            if (this.Tiles == null || this.Tiles.Count == 0)
            {
                return false;
            }

            var targetTile = FindMatchingTile<CredentialTile2>(_pendingEarlyUnlock.Username, _pendingEarlyUnlock.Domain, _pendingEarlyUnlock.UserSid);
            if (targetTile == null)
            {
                return false;
            }

            requestToApply = _pendingEarlyUnlock;
            _pendingEarlyUnlock = null;

            _earlyUnlockWatchCts?.Cancel();
            _earlyUnlockWatchCts?.Dispose();
            _earlyUnlockWatchCts = null;
        }

        if (requestToApply != null)
        {
            try
            {
                Logger.Log($"[{GetType().Name}] Applying buffered early unlock request for '{requestToApply.Username}' (source: {source}).");
                OnUnlockRequested(requestToApply.Username, requestToApply.Password, requestToApply.Domain, requestToApply.UserSid);
                return true;
            }
            finally
            {
                requestToApply.Dispose();
            }
        }

        return false;
    }

    public void ClearPendingEarlyUnlock(string reason)
    {
        lock (_earlyUnlockLock)
        {
            if (_pendingEarlyUnlock != null)
            {
                Logger.Log($"[{GetType().Name}] Cleared pending early unlock ({reason}) for user '{_pendingEarlyUnlock.Username}'.");
                _pendingEarlyUnlock.Dispose();
                _pendingEarlyUnlock = null;
            }

            _earlyUnlockWatchCts?.Cancel();
            _earlyUnlockWatchCts?.Dispose();
            _earlyUnlockWatchCts = null;
        }
    }

    public override void OnLoad()
    {
        Logger.Initialize("provider.log");
        Logger.Log($"[{GetType().Name}] OnLoad invoked. Initializing...");

        // Register this provider instance for event routing BEFORE starting services.
        // This ensures CurrentProvider is already set when services start processing events.
        CredentialProviderBootstrapper.RegisterProvider(this);
        CredentialProviderBootstrapper.EnsureServicesStarted();

        // Signal to all connected clients (Network/BT) that the PC is now LOCKED.
        CredentialProviderBootstrapper.UpdateGlobalState("locked");

        // Ensure the auto-request can fire again for this session
        PortalWinTile.ResetAutoRequestClaim();

        // Apply any early unlock request buffered by Bootstrapper before provider was registered
        CredentialProviderBootstrapper.TryApplyPendingUnlock(this);
    }

    public override void OnUnload()
    {
        Logger.Log($"[{GetType().Name}] OnUnload invoked. Cleaning up...");
        ClearPendingEarlyUnlock("ProviderUnload");
        CredentialProviderBootstrapper.UnregisterProvider(this);
    }

    /// <summary>
    /// Internal event handler used by the Bootstrapper for event wiring/unwiring.
    /// Delegates to the abstract OnUnlockRequested.
    /// </summary>
    internal void OnUnlockRequestedInternal(string username, SecureString? password, string domain, string? userSid)
    {
        OnUnlockRequested(username, password, domain, userSid);
    }

    internal void OnNetworkConnectionChangedInternal(string clientId, bool connected)
    {
        OnTransportStatusChanged();
    }

    internal void OnBtConnectionChangedInternal(string clientId, bool connected)
    {
        OnTransportStatusChanged();
    }

    internal void OnTransportStatusChangedInternal()
    {
        OnTransportStatusChanged();
    }

    protected internal abstract void OnUnlockRequested(string username, SecureString? password, string domain, string? userSid = null);
    protected internal virtual void OnTransportStatusChanged() { }

    protected bool IsMatchingUser(CredentialProviderUser? user, string username, string? domain = null, string? userSid = null)
    {
        if (user == null) return false;

        // 1. Strict SID match (highest reliability)
        if (!string.IsNullOrWhiteSpace(userSid) && IdentityHelper.MatchesSid(user.Sid, userSid))
        {
            return true;
        }

        var targetCanonical = IdentityHelper.ToCanonical(username, domain);
        var targetShort = IdentityHelper.GetShortUsername(username);

        var tileUser = user.UserName ?? "";
        var qualified = user.QualifiedUserName ?? "";
        var tileCanonical = IdentityHelper.ToCanonical(qualified) ?? IdentityHelper.ToCanonical(tileUser);
        var tileShort = IdentityHelper.GetShortUsername(qualified) ?? IdentityHelper.GetShortUsername(tileUser);

        if (IdentityHelper.EqualsIgnoreCase(tileCanonical, targetCanonical))
        {
            return true;
        }

        var tileDomain = IdentityHelper.GetDomainFromIdentity(qualified);
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(tileDomain)
            || string.Equals(domain, tileDomain, StringComparison.OrdinalIgnoreCase))
        {
            if (IdentityHelper.EqualsIgnoreCase(tileShort, targetShort))
            {
                return true;
            }
        }

        return false;
    }

    protected T? FindMatchingTile<T>(string username, string? domain = null, string? userSid = null) where T : CredentialTile2
    {
        if (this.Tiles == null)
        {
            Logger.Log($"[{GetType().Name}] No tiles collection available yet (Tiles == null).");
            return null;
        }

        // 1. Strict SID match (highest reliability)
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            foreach (var tile in this.Tiles.OfType<T>())
            {
                if (tile.User != null && IdentityHelper.MatchesSid(tile.User.Sid, userSid))
                {
                    Logger.Log($"[{GetType().Name}] Matched user tile by SID '{userSid}'.");
                    return tile;
                }
            }
        }

        Logger.Log($"[{GetType().Name}] Searching {this.Tiles.Count} active tiles for user match...");
        foreach (var tile in this.Tiles.OfType<T>())
        {
            if (tile.User != null && IsMatchingUser(tile.User, username, domain, userSid))
            {
                Logger.Log($"[{GetType().Name}] Matched user tile '{tile.User.QualifiedUserName ?? tile.User.UserName}' for '{username}'.");
                return tile;
            }
        }

        Logger.Log($"[{GetType().Name}] No specific user tile matched. Falling back to Generic/Default tile.");
        return this.Tiles.OfType<T>().FirstOrDefault(t => t.User == null)
               ?? this.Tiles.OfType<T>().FirstOrDefault();
    }

    protected Bitmap? LoadLogo()
    {
        try
        {
            var config = PortalWinConfig.Load();

            // 1. Check custom provider icon if configured
            if (config.ProviderIcon == AppIconPreset.Custom)
            {
                var sharedCustom = Path.Combine(PortalStoragePaths.ConfigDirectory, "custom-provider-icon.png");
                string? customPath = !string.IsNullOrWhiteSpace(config.CustomProviderIconPath) && File.Exists(config.CustomProviderIconPath)
                    ? config.CustomProviderIconPath
                    : (File.Exists(sharedCustom) ? sharedCustom : null);

                if (customPath != null)
                {
                    try
                    {
                        Logger.Log($"[{GetType().Name}] Logo loading custom from: {customPath}");
                        using var original = new Bitmap(customPath);
                        return NormalizeLogoBitmap(original);
                    }
                    catch (Exception exCustom)
                    {
                        Logger.LogWarning($"[{GetType().Name}] Failed to load custom logo from '{customPath}', falling back: {exCustom.Message}");
                    }
                }
            }

            // 2. Check embedded preset matching config
            string preferredRes = config.ProviderIcon switch
            {
                AppIconPreset.Tactical => "Portal.CredentialProvider.Assets.icon-tactical.png",
                AppIconPreset.Casual => "Portal.CredentialProvider.Assets.icon-casual.png",
                AppIconPreset.Elegance => "Portal.CredentialProvider.Assets.icon-elegance.png",
                AppIconPreset.Hoodie => "Portal.CredentialProvider.Assets.icon-hoodie.png",
                AppIconPreset.Alternative => "Portal.CredentialProvider.Assets.icon-alternative.png",
                AppIconPreset.Classic => "Portal.CredentialProvider.Assets.icon-classic.png",
                _ => "Portal.CredentialProvider.Assets.icon-primary.png"
            };

            var asm = typeof(PortalWinProviderBase).Assembly;
            using (var stream = asm.GetManifestResourceStream(preferredRes))
            {
                if (stream != null)
                {
                    Logger.Log($"[{GetType().Name}] Logo loaded from embedded preset: {preferredRes}");
                    using var original = new Bitmap(stream);
                    return NormalizeLogoBitmap(original);
                }
            }

            // 3. Check disk candidates in ConfigDirectory or application base directory
            var appDir = System.AppDomain.CurrentDomain.BaseDirectory ?? "";
            var baseDir = Path.Combine(appDir, "CredentialProvider");
            if (!Directory.Exists(baseDir))
                baseDir = Path.GetDirectoryName(asm.Location) ?? appDir;

            var candidates = new[]
            {
                Path.Combine(PortalStoragePaths.ConfigDirectory, "tile-icon.png"),
                Path.Combine(baseDir, "tile-icon.png"),
                Path.Combine(baseDir, "logo.bmp")
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    Logger.Log($"[{GetType().Name}] Logo loaded from: {path}");
                    using var original = new Bitmap(path);
                    return NormalizeLogoBitmap(original);
                }
            }

            // 4. Fallback to default embedded resources
            var fallbackCandidates = new[]
            {
                "Portal.CredentialProvider.Assets.portal-icon.png",
                "Portal.CredentialProvider.Assets.tile-icon.png",
                "Portal.CredentialProvider.Assets.tile-icon.bmp",
                "Portal.CredentialProvider.Assets.logo.bmp"
            };

            foreach (var resName in fallbackCandidates)
            {
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream != null)
                {
                    Logger.Log($"[{GetType().Name}] Logo loaded from fallback resource: {resName}");
                    using var original = new Bitmap(stream);
                    return NormalizeLogoBitmap(original);
                }
            }

            Logger.LogWarning($"[{GetType().Name}] Logo not found (embedded or external).");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[{GetType().Name}] Failed to load logo", ex);
        }
        return null;
    }

    private static Bitmap NormalizeLogoBitmap(Bitmap source)
    {
        const int targetSize = 256;

        var output = new Bitmap(targetSize, targetSize);
        using var g = Graphics.FromImage(output);
        g.Clear(Color.Transparent);
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var scale = Math.Min((float)targetSize / source.Width, (float)targetSize / source.Height);
        var w = (int)Math.Round(source.Width * scale);
        var h = (int)Math.Round(source.Height * scale);
        var x = (targetSize - w) / 2;
        var y = (targetSize - h) / 2;
        g.DrawImage(source, new Rectangle(x, y, w, h));
        return output;
    }
}

public sealed class EarlyUnlockRequest : IDisposable
{
    public string Username { get; }
    public SecureString? Password { get; }
    public string Domain { get; }
    public string? UserSid { get; }
    public DateTime CreatedAtUtc { get; }

    public EarlyUnlockRequest(string username, SecureString? password, string domain, string? userSid)
    {
        Username = username;
        Domain = domain;
        UserSid = userSid;
        if (password != null && password.Length > 0)
        {
            Password = password.Copy();
            Password.MakeReadOnly();
        }
        CreatedAtUtc = DateTime.UtcNow;
    }

    public bool IsExpired(TimeSpan ttl) => (DateTime.UtcNow - CreatedAtUtc) > ttl;

    public void Dispose()
    {
        Password?.Dispose();
    }
}
