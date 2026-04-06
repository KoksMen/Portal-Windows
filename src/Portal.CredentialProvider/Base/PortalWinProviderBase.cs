using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Security;
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

    public override void OnLoad()
    {
        Logger.Initialize("provider.log");
        Logger.Log($"[{GetType().Name}] OnLoad invoked. Initializing...");
        CredentialProviderBootstrapper.EnsureServicesStarted();

        // Register this provider instance for event routing.
        // This re-wires events if LogonUI reloaded for a different user session.
        CredentialProviderBootstrapper.RegisterProvider(this);

        // NEW: Signal to all connected clients (Network/BT) that the PC is now LOCKED.
        CredentialProviderBootstrapper.UpdateGlobalState("locked");

        // Ensure the auto-request can fire again for this session
        PortalWinTile.ResetAutoRequestClaim();

        // === Primary instance: subscribe to TLS, BT, and IPC events ===
        // === Secondary instance: subscribe only to IPC events ===
        //
        // IMPORTANT: Always unsubscribe first (defensive), then subscribe.
        // This prevents duplicate subscriptions when LogonUI reloads
        // the provider multiple times without calling OnUnload properly (Bug #2).

        if (CredentialProviderBootstrapper.TlsService != null)
        {
            CredentialProviderBootstrapper.TlsService.UnlockRequested -= OnUnlockRequestedInternal;  // defensive
            CredentialProviderBootstrapper.TlsService.UnlockRequested += OnUnlockRequestedInternal;
            CredentialProviderBootstrapper.TlsService.NetworkConnectionChanged -= OnNetworkConnectionChangedInternal;
            CredentialProviderBootstrapper.TlsService.NetworkConnectionChanged += OnNetworkConnectionChangedInternal;
            CredentialProviderBootstrapper.TlsService.StatusChanged -= OnTransportStatusChangedInternal;
            CredentialProviderBootstrapper.TlsService.StatusChanged += OnTransportStatusChangedInternal;
            Logger.Log($"[{GetType().Name}] Subscribed to TlsUnlockService events.");
        }

        if (CredentialProviderBootstrapper.BtService != null)
        {
            CredentialProviderBootstrapper.BtService.UnlockRequested -= OnUnlockRequestedInternal;  // defensive
            CredentialProviderBootstrapper.BtService.UnlockRequested += OnUnlockRequestedInternal;
            CredentialProviderBootstrapper.BtService.BtConnectionChanged -= OnBtConnectionChangedInternal;
            CredentialProviderBootstrapper.BtService.BtConnectionChanged += OnBtConnectionChangedInternal;
            CredentialProviderBootstrapper.BtService.StatusChanged -= OnTransportStatusChangedInternal;
            CredentialProviderBootstrapper.BtService.StatusChanged += OnTransportStatusChangedInternal;
            Logger.Log($"[{GetType().Name}] Subscribed to BluetoothUnlockService events.");
        }
    }

    public override void OnUnload()
    {
        Logger.Log($"[{GetType().Name}] OnUnload invoked. Cleaning up...");
        CredentialProviderBootstrapper.UnregisterProvider(this);
    }

    /// <summary>
    /// Internal event handler used by the Bootstrapper for event wiring/unwiring.
    /// Delegates to the abstract OnUnlockRequested.
    /// </summary>
    internal void OnUnlockRequestedInternal(string username, SecureString? password, string domain)
    {
        OnUnlockRequested(username, password, domain);
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

    protected internal abstract void OnUnlockRequested(string username, SecureString? password, string domain);
    protected internal virtual void OnTransportStatusChanged() { }

    protected T? FindMatchingTile<T>(string username, string? domain = null) where T : CredentialTile2
    {
        if (this.Tiles == null)
        {
            Logger.LogWarning($"[{GetType().Name}] No tiles collection available!");
            return null;
        }

        var targetCanonical = IdentityHelper.ToCanonical(username, domain);
        var targetShort = IdentityHelper.GetShortUsername(username);

        Logger.Log($"[{GetType().Name}] Searching {this.Tiles.Count} active tiles for user match...");
        foreach (var tile in this.Tiles.OfType<T>())
        {
            if (tile.User != null)
            {
                var tileUser = tile.User.UserName ?? "";
                var qualified = tile.User.QualifiedUserName ?? "";
                var tileCanonical = IdentityHelper.ToCanonical(qualified) ?? IdentityHelper.ToCanonical(tileUser);
                var tileShort = IdentityHelper.GetShortUsername(qualified) ?? IdentityHelper.GetShortUsername(tileUser);

                if (IdentityHelper.EqualsIgnoreCase(tileCanonical, targetCanonical))
                {
                    Logger.Log($"[{GetType().Name}] Matched user tile (canonical) '{tileCanonical}' for '{targetCanonical}'.");
                    return tile;
                }

                if (IdentityHelper.EqualsIgnoreCase(tileShort, targetShort))
                {
                    Logger.Log($"[{GetType().Name}] Matched user tile (short fallback) '{tileUser}' for '{username}'.");
                    return tile;
                }
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
            // Check multiple possible embedded resource names
            var asm = typeof(PortalWinProviderBase).Assembly;
            var embeddedCandidates = new[]
            {
                "Portal.CredentialProvider.Assets.portal-icon.png",
                "Portal.CredentialProvider.Assets.tile-icon.bmp",
                "Portal.CredentialProvider.Assets.tile-icon.png",
                "Portal.CredentialProvider.Assets.logo.bmp"
            };

            foreach (var resName in embeddedCandidates)
            {
                using (var stream = asm.GetManifestResourceStream(resName))
                {
                    if (stream != null)
                    {
                        Logger.Log($"[{GetType().Name}] Logo loaded from embedded resource: {resName}");
                        using var original = new Bitmap(stream);
                        return NormalizeLogoBitmap(original);
                    }
                }
            }

            var appDir = System.AppDomain.CurrentDomain.BaseDirectory ?? "";
            var baseDir = Path.Combine(appDir, "CredentialProvider");
            if (!Directory.Exists(baseDir))
                baseDir = Path.GetDirectoryName(asm.Location) ?? appDir;

            var candidates = new[]
            {
                Path.Combine(baseDir, "logo.bmp"),
                Path.Combine(baseDir, "tile-icon.png")
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
