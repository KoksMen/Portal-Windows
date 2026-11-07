using System.Security;
using Portal.Common;
using Portal.CredentialProvider.Base;

namespace Portal.CredentialProvider.Services;

public static class CredentialProviderBootstrapper
{
    private static readonly object _lock = new();
    private static bool _servicesStarted;
    private static bool _exitHookRegistered;

    public static TlsUnlockService? TlsService { get; private set; }
    public static BluetoothUnlockService? BtService { get; private set; }

    private static WeakReference<Portal.CredentialProvider.Base.PortalWinProviderBase>? _currentProviderRef;
    private static CancellationTokenSource? _stopServicesCts;

    private static EarlyUnlockRequest? _pendingBootstrapperUnlock;
    private static readonly object _earlyUnlockLock = new();
    private static readonly TimeSpan EarlyUnlockTtl = TimeSpan.FromSeconds(15);

    public static bool HasPendingUnlock
    {
        get
        {
            lock (_earlyUnlockLock)
            {
                return _pendingBootstrapperUnlock != null && !_pendingBootstrapperUnlock.IsExpired(EarlyUnlockTtl);
            }
        }
    }

    public static Portal.CredentialProvider.Base.PortalWinProviderBase? CurrentProvider
    {
        get
        {
            lock (_lock)
            {
                return _currentProviderRef != null && _currentProviderRef.TryGetTarget(out var provider)
                    ? provider
                    : null;
            }
        }
    }

    public static void EnsureServicesStarted()
    {
        lock (_lock)
        {
            if (!_exitHookRegistered)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => StopServices("ProcessExit");
                _exitHookRegistered = true;
            }

            if (!_servicesStarted)
            {
                StartServices();
                _servicesStarted = true;
            }
        }
    }

    private static void StartServices()
    {
        try
        {
            var config = PortalWinConfig.Load();
            TlsService = new TlsUnlockService(config);
            TlsService.UnlockRequested += OnServiceUnlockRequested;
            TlsService.NetworkConnectionChanged += OnServiceNetworkConnectionChanged;
            TlsService.StatusChanged += OnServiceStatusChanged;
            TlsService.Start();
            Logger.Log("[Bootstrapper] TlsUnlockService started.");
        }
        catch (Exception ex)
        {
            Logger.LogError("[Bootstrapper] Failed to start TlsUnlockService", ex);
            ActivityJournal.Record("system", "⚠️", "Network unlock service failed", "Credential Provider could not start its Wi-Fi unlock service.", false);
        }

        try
        {
            BtService = new BluetoothUnlockService();
            BtService.UnlockRequested += OnServiceUnlockRequested;
            BtService.BtConnectionChanged += OnServiceBtConnectionChanged;
            BtService.StatusChanged += OnServiceStatusChanged;
            _ = BtService.StartAsync();
            Logger.Log("[Bootstrapper] BluetoothUnlockService started.");
        }
        catch (Exception ex)
        {
            Logger.LogError("[Bootstrapper] Failed to start BluetoothUnlockService", ex);
            ActivityJournal.Record("system", "⚠️", "Bluetooth unlock service failed", "Credential Provider could not start its Bluetooth unlock service.", false);
        }

        try
        {
            EmergencyCancelService.Start();
        }
        catch (Exception ex)
        {
            Logger.LogError("[Bootstrapper] Failed to start EmergencyCancelService", ex);
        }

        if (TlsService != null || BtService != null)
        {
            ActivityJournal.Record(
                "system",
                "⚡",
                "Portal provider started",
                "Credential Provider is ready on the Windows lock screen.");
        }
    }

    public static void RegisterProvider(Portal.CredentialProvider.Base.PortalWinProviderBase provider)
    {
        lock (_lock)
        {
            if (_stopServicesCts != null)
            {
                Logger.Log("[Bootstrapper] Cancelling scheduled services stop (new provider instance registered).");
                try
                {
                    _stopServicesCts.Cancel();
                    _stopServicesCts.Dispose();
                }
                catch { }
                _stopServicesCts = null;
            }

            _currentProviderRef = new WeakReference<Portal.CredentialProvider.Base.PortalWinProviderBase>(provider);
            Logger.Log($"[Bootstrapper] Registered new provider instance: {provider.GetType().Name}");

            TryApplyPendingUnlock(provider);
        }
    }

    public static void UnregisterProvider(Portal.CredentialProvider.Base.PortalWinProviderBase provider)
    {
        lock (_lock)
        {
            if (_currentProviderRef != null && _currentProviderRef.TryGetTarget(out var current) && current == provider)
            {
                _currentProviderRef = null;
            }
            Logger.Log($"[Bootstrapper] Unregistered provider instance: {provider.GetType().Name}");

            if (_currentProviderRef == null)
            {
                _stopServicesCts?.Cancel();
                _stopServicesCts?.Dispose();
                var cts = new CancellationTokenSource();
                _stopServicesCts = cts;

                _ = Task.Delay(TimeSpan.FromSeconds(2), cts.Token).ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && !cts.IsCancellationRequested)
                    {
                        lock (_lock)
                        {
                            if (_currentProviderRef == null)
                            {
                                StopServices("No active provider (debounced)");
                            }
                        }
                    }
                }, TaskScheduler.Default);
            }
        }
    }

    public static bool TryApplyPendingUnlock(Portal.CredentialProvider.Base.PortalWinProviderBase provider)
    {
        EarlyUnlockRequest? pending = null;
        lock (_earlyUnlockLock)
        {
            if (_pendingBootstrapperUnlock == null) return false;

            if (_pendingBootstrapperUnlock.IsExpired(EarlyUnlockTtl))
            {
                Logger.LogWarning($"[Bootstrapper] Discarded expired pending unlock for '{_pendingBootstrapperUnlock.Username}'.");
                _pendingBootstrapperUnlock.Dispose();
                _pendingBootstrapperUnlock = null;
                return false;
            }

            pending = _pendingBootstrapperUnlock;
            _pendingBootstrapperUnlock = null;
        }

        if (pending != null)
        {
            try
            {
                Logger.Log($"[Bootstrapper] Applying buffered early unlock request for '{pending.Username}' to provider '{provider.GetType().Name}'.");
                provider.OnUnlockRequestedInternal(pending.Username, pending.Password, pending.Domain, pending.UserSid);
                return true;
            }
            finally
            {
                pending.Dispose();
            }
        }

        return false;
    }

    public static void ClearPendingUnlock(string reason)
    {
        lock (_earlyUnlockLock)
        {
            if (_pendingBootstrapperUnlock != null)
            {
                Logger.Log($"[Bootstrapper] Cleared pending early unlock ({reason}) for user '{_pendingBootstrapperUnlock.Username}'.");
                _pendingBootstrapperUnlock.Dispose();
                _pendingBootstrapperUnlock = null;
            }
        }
    }

    private static void OnServiceUnlockRequested(string username, SecureString? password, string domain, string? userSid)
    {
        Portal.CredentialProvider.Base.PortalWinProviderBase? provider;
        lock (_lock)
        {
            provider = CurrentProvider;
        }

        if (provider != null)
        {
            Logger.Log($"[Bootstrapper] Forwarding unlock request for '{username}' to CurrentProvider '{provider.GetType().Name}'.");
            provider.OnUnlockRequestedInternal(username, password, domain, userSid);
            return;
        }

        lock (_earlyUnlockLock)
        {
            Logger.LogWarning($"[Bootstrapper] No active provider instance registered. Buffering pending early unlock request for user '{username}' (Domain: '{domain}', SID: '{userSid ?? "none"}', TTL: {EarlyUnlockTtl.TotalSeconds:F0}s)...");
            _pendingBootstrapperUnlock?.Dispose();
            _pendingBootstrapperUnlock = new EarlyUnlockRequest(username, password, domain, userSid);
        }
    }

    private static void OnServiceNetworkConnectionChanged(string clientId, bool connected)
    {
        try
        {
            CurrentProvider?.OnNetworkConnectionChangedInternal(clientId, connected);
        }
        catch (Exception ex)
        {
            Logger.LogError("[Bootstrapper] Error forwarding NetworkConnectionChanged", ex);
        }
    }

    private static void OnServiceBtConnectionChanged(string clientId, bool connected)
    {
        try
        {
            CurrentProvider?.OnBtConnectionChangedInternal(clientId, connected);
        }
        catch (Exception ex)
        {
            Logger.LogError("[Bootstrapper] Error forwarding BtConnectionChanged", ex);
        }
    }

    private static void OnServiceStatusChanged()
    {
        try
        {
            CurrentProvider?.OnTransportStatusChangedInternal();
        }
        catch (Exception ex)
        {
            Logger.LogError("[Bootstrapper] Error forwarding StatusChanged", ex);
        }
    }

    public static void UpdateGlobalState(string state)
    {
        try
        {
            TlsService?.UpdateState(state);
            BtService?.UpdateState(state);
        }
        catch { }
    }

    private static void StopServices(string reason)
    {
        lock (_lock)
        {
            ClearPendingUnlock($"StopServices: {reason}");

            try
            {
                if (TlsService != null)
                {
                    TlsService.UnlockRequested -= OnServiceUnlockRequested;
                    TlsService.NetworkConnectionChanged -= OnServiceNetworkConnectionChanged;
                    TlsService.StatusChanged -= OnServiceStatusChanged;
                }
            }
            catch { }

            try
            {
                if (BtService != null)
                {
                    BtService.UnlockRequested -= OnServiceUnlockRequested;
                    BtService.BtConnectionChanged -= OnServiceBtConnectionChanged;
                    BtService.StatusChanged -= OnServiceStatusChanged;
                }
            }
            catch { }

            try
            {
                TlsService?.DisconnectAllWebSocketClients($"Bootstrapper stop: {reason}");
            }
            catch { }

            try
            {
                TlsService?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Bootstrapper] Failed to stop TlsUnlockService. Reason='{reason}'", ex);
            }
            finally
            {
                TlsService = null;
            }

            try
            {
                BtService?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Bootstrapper] Failed to stop BluetoothUnlockService. Reason='{reason}'", ex);
            }
            finally
            {
                BtService = null;
            }

            try
            {
                EmergencyCancelService.Stop();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Bootstrapper] Failed to stop EmergencyCancelService. Reason='{reason}'", ex);
            }

            _servicesStarted = false;
            Logger.Log($"[Bootstrapper] Services stopped. Reason='{reason}'.");
        }
    }
}
