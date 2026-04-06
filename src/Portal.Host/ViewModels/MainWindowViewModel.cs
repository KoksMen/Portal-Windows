using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Portal.Common;
using Portal.Common.Models;
using Portal.Host.Models;
using Portal.Host.Services;

namespace Portal.Host.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly FirewallService _firewall;
    private readonly ProviderSetupService _providerSetup;
    private readonly CertificateManager _certManager;
    private readonly NetworkPairingService _networkPairing;
    private readonly NetworkService _networkService;
    private readonly QrCodeService _qrCodeService;
    private readonly ProviderLocatorService _providerLocator;
    private readonly BluetoothService _bluetoothService;

    // View state properties would go here

    public MainWindowViewModel(
        FirewallService firewall,
        ProviderSetupService providerSetup,
        CertificateManager certManager,
        NetworkPairingService networkPairing,
        NetworkService networkService,
        QrCodeService qrCodeService,
        ProviderLocatorService providerLocator,
        BluetoothService bluetoothService)
    {
        _firewall = firewall;
        _providerSetup = providerSetup;
        _certManager = certManager;
        _networkPairing = networkPairing;
        _networkService = networkService;
        _qrCodeService = qrCodeService;
        _providerLocator = providerLocator;
        _bluetoothService = bluetoothService;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
