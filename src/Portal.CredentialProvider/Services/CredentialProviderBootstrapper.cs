using Portal.Common;

namespace Portal.CredentialProvider.Services;

public static class CredentialProviderBootstrapper
{
    private static readonly object _lock = new();
    private static bool _servicesStarted;
    private static bool _exitHookRegistered;

    public static TlsUnlockService? TlsService { get; private set; }
    public static BluetoothUnlockService? BtService { get; private set; }

    private static WeakReference<Portal.CredentialProvider.Base.PortalWinProviderBase>? _currentProviderRef;

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
            TlsService.Start();
            Logger.Log("[Bootstrapper] TlsUnlockService started.");
        }
        catch (Exception ex)
        {
            Logger.LogError("[Bootstrapper] Failed to start TlsUnlockService", ex);
        }

        try
        {
            BtService = new BluetoothUnlockService();
            _ = BtService.StartAsync();
            Logger.Log("[Bootstrapper] BluetoothUnlockService started.");
        }
        catch (Exception ex)
        {
            Logger.LogError("[Bootstrapper] Failed to start BluetoothUnlockService", ex);
        }
    }

    public static void RegisterProvider(Portal.CredentialProvider.Base.PortalWinProviderBase provider)
    {
        lock (_lock)
        {
            if (_currentProviderRef != null && _currentProviderRef.TryGetTarget(out var oldProvider) && oldProvider != provider)
            {
                Logger.Log("[Bootstrapper] Detaching events from previous provider instance.");
                DetachProviderEvents(oldProvider);
            }

            _currentProviderRef = new WeakReference<Portal.CredentialProvider.Base.PortalWinProviderBase>(provider);
            Logger.Log($"[Bootstrapper] Registered new provider instance: {provider.GetType().Name}");
        }
    }

    public static void UnregisterProvider(Portal.CredentialProvider.Base.PortalWinProviderBase provider)
    {
        lock (_lock)
        {
            DetachProviderEvents(provider);
            if (_currentProviderRef != null && _currentProviderRef.TryGetTarget(out var current) && current == provider)
            {
                _currentProviderRef = null;
            }
            Logger.Log($"[Bootstrapper] Unregistered provider instance: {provider.GetType().Name}");

            if (_currentProviderRef == null)
            {
                StopServices("No active provider");
            }
        }
    }

    private static void DetachProviderEvents(Portal.CredentialProvider.Base.PortalWinProviderBase provider)
    {
        try
        {
            if (TlsService != null) TlsService.UnlockRequested -= provider.OnUnlockRequestedInternal;
            if (TlsService != null) TlsService.NetworkConnectionChanged -= provider.OnNetworkConnectionChangedInternal;
            if (TlsService != null) TlsService.StatusChanged -= provider.OnTransportStatusChangedInternal;
            if (BtService != null) BtService.UnlockRequested -= provider.OnUnlockRequestedInternal;
            if (BtService != null) BtService.BtConnectionChanged -= provider.OnBtConnectionChangedInternal;
            if (BtService != null) BtService.StatusChanged -= provider.OnTransportStatusChangedInternal;
        }
        catch (Exception ex)
        {
            Logger.LogError("[Bootstrapper] Error detaching events", ex);
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

            _servicesStarted = false;
            Logger.Log($"[Bootstrapper] Services stopped. Reason='{reason}'.");
        }
    }
}
