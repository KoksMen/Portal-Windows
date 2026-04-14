using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.Common;
using Portal.Common.Models;
using Portal.Host.Helpers;
using Portal.Host.Models;
using Portal.Host.Services;

namespace Portal.Host.ViewModels;

public class LocalAccountOption
{
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";
    public string DisplayName => $"{Domain}\\{Username}";
    public override string ToString() => DisplayName;
}

public class NetworkIpOption
{
    public string InterfaceName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string DisplayName => $"{InterfaceName} - {IpAddress}";
    public string DisplayNameMultiLine => $"{InterfaceName}{Environment.NewLine}{IpAddress}";
    public override string ToString() => DisplayName;
}

public class FaqCategoryGroup
{
    public string Title { get; set; } = "General";
    public ObservableCollection<FaqArticle> Articles { get; } = new();
    public bool IsExpanded { get; set; }
}

public partial class MainViewModel : ObservableObject
{
    private readonly FirewallService _firewall;
    private readonly ProviderSetupService _providerSetup;
    private readonly CertificateManager _certManager;
    private readonly NetworkPairingService _networkPairing;
    private readonly NetworkService _networkService;
    private readonly QrCodeService _qrCodeService;
    private readonly ProviderLocatorService _providerLocator;
    private readonly BluetoothService _bluetoothService;
    private readonly IDialogService _dialogService;
    private readonly FaqContentService _faqContentService;
    private readonly UpdateService _updateService;
    private readonly EncryptedBackupService _encryptedBackupService;

    private BluetoothPairingService? _btPairing;
    private PortalWinConfig _config;
    private X509Certificate2? _hostCert;

    private CancellationTokenSource? _pairingCts;
    private CancellationTokenSource? _wizardSetupCts;
    private CancellationTokenSource? _busyOperationCts;
    private TaskCompletionSource<bool>? _busyResultTcs;
    private TaskCompletionSource<bool>? _credsSignal;
    private TaskCompletionSource<bool>? _transportSignal;

    private DeviceModel? _newlyPairedDevice;
    private System.Windows.Threading.DispatcherTimer? _expirationTimer;
    private DateTime _pairingStartTime;
    private PairingContext _pairingContext = new();
    private bool _pairingBackRequested;
    private bool _pairingRefreshRequested;
    private int _pairingSessionId;
    private bool _suppressDuplicateProtectionPrompt;
    private bool _previousCrossTransportProtectionEnabled;
    private int _wizardSessionId;

    private enum PairingStepResult
    {
        Paired,
        RetryCurrentTransport,
        BackToTransport,
        Cancelled
    }

    private enum BusyOperationOutcome
    {
        Completed,
        Cancelled,
        Skipped
    }

    // --- Observable Properties (Dashboard/Status) ---
    [ObservableProperty] private string _mainStatusText = "Loading...";
    [ObservableProperty] private bool _isServiceActive;
    [ObservableProperty] private bool _hasSetupIssues;
    [ObservableProperty] private string _setupIssueTitle = "Setup required";
    [ObservableProperty] private string _setupIssueHint = "";
    [ObservableProperty] private string _providerInstallButtonText = "Install";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartWizardButton))]
    private bool _isStatusReady;
    public bool CanStartWizardButton => IsStatusReady;

    [ObservableProperty] private bool _isRegisteredOk;
    [ObservableProperty] private bool _isFilesOk;
    [ObservableProperty] private bool _isFirewallOk;
    [ObservableProperty] private bool _isCertOk;

    [ObservableProperty] private string _clientCountText = "0 trusted devices";
    [ObservableProperty] private string _ipAddressText = "IP: Unknown";

    [ObservableProperty] private bool _showSetupPanel = true;
    public bool ShowConnectedPanel => !ShowSetupPanel;

    public ObservableCollection<DeviceModel> Devices { get; } = new();

    // --- App Info ---
    public string AppVersion => "v1.0.4";
    public string AppReleaseVersion => "1.0.4-Eve-Stable-Release";

    // Replace these URLs and GitHub handles with your production values before release.
    // This is the single place to edit About screen links.
    private const string AboutTermsUrlValue = "https://github.com/xXMRK888YTXx/Portal-Docs/blob/master/Windows/Terms%20of%20Service.md";
    private const string AboutPrivacyUrlValue = "https://github.com/xXMRK888YTXx/Portal-Docs/blob/master/Windows/Privacy%20Policy.md";
    private const string AboutMobileClientUrlValue = "https://play.google.com/store/apps/details?id=com.xxmrk888ytxx.portal";
    private const string AboutAndroidSourceUrlValue = "https://github.com/xXMRK888YTXx/Portal-Android";
    private const string AboutDesktopSourceUrlValue = "https://github.com/KoksMen/Portal-Windows";
    private const string AboutAndroidDeveloperHandleValue = "xXMRK888YTXx";
    private const string AboutDesktopDeveloperHandleValue = "KoksMen";

    public string AboutTermsUrl => AboutTermsUrlValue;
    public string AboutPrivacyUrl => AboutPrivacyUrlValue;
    public string AboutMobileClientUrl => AboutMobileClientUrlValue;
    public string AboutAndroidSourceUrl => AboutAndroidSourceUrlValue;
    public string AboutDesktopSourceUrl => AboutDesktopSourceUrlValue;
    public string AboutTermsDisplay => AboutTermsUrl.Replace("https://", "");
    public string AboutPrivacyDisplay => AboutPrivacyUrl.Replace("https://", "");
    public string AboutMobileClientDisplay => AboutMobileClientUrl.Replace("https://", "");
    public string AboutAndroidSourceDisplay => AboutAndroidSourceUrl.Replace("https://", "");
    public string AboutDesktopSourceDisplay => AboutDesktopSourceUrl.Replace("https://", "");
    public string AboutAndroidDeveloperHandle => $"@{AboutAndroidDeveloperHandleValue}";
    public string AboutDesktopDeveloperHandle => $"@{AboutDesktopDeveloperHandleValue}";
    public string AboutAndroidDeveloperProfileUrl => $"https://github.com/{AboutAndroidDeveloperHandleValue}";
    public string AboutDesktopDeveloperProfileUrl => $"https://github.com/{AboutDesktopDeveloperHandleValue}";
    public string AboutAndroidDeveloperAvatarUrl => $"{AboutAndroidDeveloperProfileUrl}.png";
    public string AboutDesktopDeveloperAvatarUrl => $"{AboutDesktopDeveloperProfileUrl}.png";
    [ObservableProperty] private BitmapSource? _aboutAndroidDeveloperAvatarImage;
    [ObservableProperty] private BitmapSource? _aboutDesktopDeveloperAvatarImage;
    // --- Observable Properties (Settings) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSettingsPanel))]
    private bool _showDashboard = true;
    public bool ShowSettingsPanel => !ShowDashboard;

    [ObservableProperty] private bool _showAboutDialog;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFaqOverlayVisible))]
    private bool _showFaq;
    [ObservableProperty] private bool _isDiagnosticsUnlocked;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFaqSection))]
    [NotifyPropertyChangedFor(nameof(ShowUpdatesSection))]
    [NotifyPropertyChangedFor(nameof(IsFaqOverlayVisible))]
    private bool _areExperimentalFeaturesEnabled;
    [ObservableProperty] private string _faqSearchText = "";
    [ObservableProperty] private string _selectedFaqCategory = "All";
    [NotifyPropertyChangedFor(nameof(SelectedFaqTagsText))]
    [NotifyPropertyChangedFor(nameof(SelectedFaqUpdatedAtText))]
    [ObservableProperty] private FaqArticle? _selectedFaqArticle;
    [ObservableProperty] private string _faqLastUpdatedText = "Local FAQ is ready to load.";
    [ObservableProperty] private string _faqSourceText = "Source: local file";
    [ObservableProperty] private bool _isRefreshingFaq;
    [ObservableProperty] private string _faqRefreshButtonText = "Update Wiki";
    public ObservableCollection<string> FaqCategories { get; } = new();
    public ObservableCollection<FaqArticle> FaqArticles { get; } = new();
    public ObservableCollection<FaqArticle> FilteredFaqArticles { get; } = new();
    public ObservableCollection<FaqCategoryGroup> FilteredFaqGroups { get; } = new();

    [ObservableProperty] private string _updateStatusTitle = "Updates are idle.";
    [ObservableProperty] private string _updateStatusMessage = "Check for Updates to query the latest release from GitHub.";
    [ObservableProperty] private string _updateCurrentVersionText = string.Empty;
    [ObservableProperty] private string _updateSourceText = $"Source: {UpdateService.BuiltInSourceLabel}";
    [ObservableProperty] private string _updateAvailableVersionText = "No update detected";
    [ObservableProperty] private string _updateLastCheckedText = "Not checked yet";
    [ObservableProperty] private string _updateLastInstalledText = "Last update: not installed yet";
    [ObservableProperty] private string _updateRepositoryText = string.Empty;
    [ObservableProperty] private string _updateTokenText = string.Empty;
    [ObservableProperty] private bool _isAutoUpdateChecksEnabled = true;
    [ObservableProperty] private string _updateFileName = "No package selected";
    [ObservableProperty] private string _updateTransferText = "0 MB / 0 MB";
    [ObservableProperty] private string _updateSpeedText = "Speed: --";
    [NotifyPropertyChangedFor(nameof(ShowUpdateTransferBlock))]
    [ObservableProperty] private AppUpdateStage _updateCurrentStage = AppUpdateStage.Idle;
    [NotifyPropertyChangedFor(nameof(ShowDeterminateProgressBar))]
    [ObservableProperty] private double _updateProgressPercent;
    [NotifyPropertyChangedFor(nameof(CanInstallUpdate))]
    [NotifyPropertyChangedFor(nameof(UpdateProgressHintText))]
    [NotifyPropertyChangedFor(nameof(CanCloseUpdateWizard))]
    [ObservableProperty] private bool _isUpdateOperationInProgress;
    [NotifyPropertyChangedFor(nameof(ShowIndeterminateProgressBar))]
    [NotifyPropertyChangedFor(nameof(ShowDeterminateProgressBar))]
    [ObservableProperty] private bool _isUpdateProgressIndeterminate;
    [NotifyPropertyChangedFor(nameof(ShowIndeterminateProgressBar))]
    [NotifyPropertyChangedFor(nameof(ShowDeterminateProgressBar))]
    [ObservableProperty] private bool _isUpdateProgressPrimed;
    [NotifyPropertyChangedFor(nameof(CanInstallUpdate))]
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string _updateBannerText = "";
    [ObservableProperty] private bool _hasUpdateBanner;
    [ObservableProperty] private string _installUpdateButtonText = "Install Update";
    [ObservableProperty] private bool _showUpdateWizard;
    [ObservableProperty] private bool _showUpdateToast;
    [ObservableProperty] private string _updateToastTitle = "Update available";
    [ObservableProperty] private string _updateToastMessage = "";

    private AppUpdateManifest? _availableUpdateManifest;
    private readonly System.Windows.Threading.DispatcherTimer _updateToastTimer;
    private int _aboutVersionTapCount;
    public string SelectedFaqTagsText => SelectedFaqArticle == null || SelectedFaqArticle.Tags.Count == 0
        ? "Tags: local, help"
        : $"Tags: {string.Join(", ", SelectedFaqArticle.Tags)}";
    public string SelectedFaqUpdatedAtText => SelectedFaqArticle == null
        ? "Updated: --"
        : $"Updated: {SelectedFaqArticle.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
    public bool CanInstallUpdate => IsUpdateAvailable && !IsUpdateOperationInProgress && _availableUpdateManifest != null;
    public bool ShowFaqSection => AreExperimentalFeaturesEnabled;
    public bool ShowDiagnosticsSection => IsDiagnosticsUnlocked;
    public bool ShowUpdatesSection => true;
    public bool IsFaqOverlayVisible => AreExperimentalFeaturesEnabled && ShowFaq;
    public bool CanCloseUpdateWizard => !IsUpdateOperationInProgress;
    public bool ShowIndeterminateProgressBar => IsUpdateProgressPrimed && IsUpdateProgressIndeterminate;
    public bool ShowDeterminateProgressBar => IsUpdateProgressPrimed && !IsUpdateProgressIndeterminate;
    public bool ShowUpdateTransferBlock => IsUpdateOperationInProgress && UpdateCurrentStage == AppUpdateStage.Downloading;
    public string UpdateProgressHintText => IsUpdateOperationInProgress
        ? "Update Wizard is running the current stage. Please wait."
        : "Details are shown here only during install.";

    partial void OnFaqSearchTextChanged(string value)
    {
        UpdateFaqFilter();
    }

    partial void OnSelectedFaqCategoryChanged(string value)
    {
        UpdateFaqFilter();
    }

    partial void OnIsDiagnosticsUnlockedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDiagnosticsSection));
    }

    partial void OnIsAutoUpdateChecksEnabledChanged(bool value)
    {
        if (_config == null)
        {
            return;
        }

        _config.AutoUpdateChecksEnabled = value;
        _config.Save();
    }

    private void UpdateFaqFilter()
    {
        FilteredFaqArticles.Clear();
        FilteredFaqGroups.Clear();

        var lowerFilter = FaqSearchText?.Trim().ToLowerInvariant() ?? string.Empty;
        var category = SelectedFaqCategory;

        var items = FaqArticles.Where(article =>
            (category == "All" || string.Equals(article.Category, category, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(lowerFilter) ||
             article.Title.Contains(lowerFilter, StringComparison.OrdinalIgnoreCase) ||
             article.Content.Contains(lowerFilter, StringComparison.OrdinalIgnoreCase) ||
             article.Tags.Any(tag => tag.Contains(lowerFilter, StringComparison.OrdinalIgnoreCase))));

        foreach (var item in items.OrderBy(article => article.Category).ThenBy(article => article.Title))
        {
            FilteredFaqArticles.Add(item);
        }

        foreach (var group in FilteredFaqArticles
                     .GroupBy(article => string.IsNullOrWhiteSpace(article.Category) ? "General" : article.Category)
                     .OrderBy(group => group.Key))
        {
            var faqGroup = new FaqCategoryGroup
            {
                Title = group.Key,
                IsExpanded = !string.IsNullOrWhiteSpace(lowerFilter)
                             || string.Equals(category, "All", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(category, group.Key, StringComparison.OrdinalIgnoreCase)
            };

            foreach (var article in group)
            {
                faqGroup.Articles.Add(article);
            }

            FilteredFaqGroups.Add(faqGroup);
        }

        if (SelectedFaqArticle == null || !FilteredFaqArticles.Contains(SelectedFaqArticle))
        {
            SelectedFaqArticle = FilteredFaqArticles.FirstOrDefault();
        }
    }

    [ObservableProperty] private string _settingsPort = "29170";
    [ObservableProperty] private string _settingsDllPath = "";
    [ObservableProperty] private string _settingsHostRequestTimeoutMinutes = "2";
    [ObservableProperty] private bool _isVpnCompatibilityModeEnabled = true;
    [ObservableProperty] private string _restoreBackupFileText = "No backup file selected";
    [ObservableProperty] private bool _showCreateBackupDialog;
    [ObservableProperty] private bool _showRestoreBackupDialog;

    private string? _restoreBackupFilePath;
    public event Action? OpenLogsWindowRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHostTriggerSelector))]
    private bool _isModeClient = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHostTriggerSelector))]
    private bool _isModeHost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHostTriggerSelector))]
    private bool _isModeBoth;

    /// <summary>
    /// Show the host request trigger selector only when HostInitiated or Both mode is selected.
    /// </summary>
    public bool ShowHostTriggerSelector => IsModeHost || IsModeBoth;

    // Host Request Trigger radio buttons
    [ObservableProperty] private bool _isTriggerOnClick = true;
    [ObservableProperty] private bool _isTriggerOnClickAndStartup;
    [ObservableProperty] private bool _isTriggerOnClickAndAnyLockScreen;

    // Loading State for Settings
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotWorkingSettings))]
    private bool _isWorkingSettings = false;

    public bool IsNotWorkingSettings => !IsWorkingSettings;

    [ObservableProperty] private string _saveConfigBtnText = "Save Configuration";
    [ObservableProperty] private string _uninstallBtnText = "Uninstall Everything";
    [ObservableProperty] private bool _isDuplicateAccountProtectionEnabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseCrossTransportProtection))]
    private bool _isCrossTransportDuplicateProtectionEnabled = false;
    public bool CanUseCrossTransportProtection => IsDuplicateAccountProtectionEnabled;

    partial void OnIsDuplicateAccountProtectionEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUseCrossTransportProtection));

        if (!value)
        {
            _previousCrossTransportProtectionEnabled = IsCrossTransportDuplicateProtectionEnabled;
            IsCrossTransportDuplicateProtectionEnabled = false;
        }

        if (_suppressDuplicateProtectionPrompt || value)
        {
            return;
        }

        _ = ConfirmDisableDuplicateProtectionAsync();
    }

    // Loading State for System Health & Maintenance actions
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotWorkingHealth))]
    private bool _isWorkingHealth = false;

    public bool IsNotWorkingHealth => !IsWorkingHealth;

    // --- Observable Properties (Wizard) ---
    [ObservableProperty] private bool _showWizard = false;
    [ObservableProperty] private string _wizStatusText = "Initializing...";
    [ObservableProperty] private string _wizErrorText = "";
    [ObservableProperty] private bool _wizIsIndeterminate = false;

    // Wizard Step Visibility
    [ObservableProperty] private bool _stepProgressVis = true;
    [ObservableProperty] private bool _stepCredsVis;
    [ObservableProperty] private bool _stepTransportVis;
    [ObservableProperty] private bool _stepPairingVis;
    [ObservableProperty] private bool _stepSuccessVis;
    [ObservableProperty] private bool _stepErrorVis;
    [ObservableProperty] private bool _stepNameDeviceVis;

    // Creds inputs
    [ObservableProperty] private string _wizInputUser = "";
    [ObservableProperty] private string _wizInputDomain = "";
    public string WizInputPass { get; set; } = ""; // VM shouldn't bind plain passwords easily, but kept simple here

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WizHideDeviceNameEdit))]
    [NotifyPropertyChangedFor(nameof(WizShowAutoAccountSelector))]
    private bool _wizShowDeviceNameEdit = false;

    public bool WizHideDeviceNameEdit => !WizShowDeviceNameEdit;
    public bool WizShowAutoAccountSelector => true;

    [ObservableProperty] private string _wizCredsNextText = "Next →";
    [ObservableProperty] private LocalAccountOption? _selectedLocalAccount;

    // Transport inputs
    [ObservableProperty] private bool _wizIsNetworkTransport = true;

    public bool IsTransTcp
    {
        get => WizIsNetworkTransport;
        set
        {
            if (value)
            {
                WizIsNetworkTransport = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTransBt));
            }
        }
    }

    public bool IsTransBt
    {
        get => !WizIsNetworkTransport;
        set
        {
            if (value)
            {
                WizIsNetworkTransport = false;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTransTcp));
            }
        }
    }

    // Pairing Step Info
    [ObservableProperty] private string _wizPairCode = "--- ---";
    [ObservableProperty] private string _wizExpiresInfo = "Code expires in 2:00";
    [ObservableProperty] private bool _wizIsExpiresRed = false;
    [ObservableProperty] private string _wizPairInfo = "Waiting...";

    [ObservableProperty] private bool _wizShowNetworkInfo = true;
    public bool WizShowBluetoothInfo => !WizShowNetworkInfo;

    [ObservableProperty] private string _wizIpOnly = "";
    [ObservableProperty] private string _wizPortOnly = "";
    [ObservableProperty] private string _wizBtAddress = "";
    [ObservableProperty] private NetworkIpOption? _selectedPairIp;

    [ObservableProperty] private BitmapSource? _wizQrCodeImage;
    [ObservableProperty] private bool _wizShowQrCode = false;

    // Naming Step
    [ObservableProperty] private string _wizDeviceName = "";

    private string? _editingClientId = null;
    public ObservableCollection<LocalAccountOption> AvailableLocalAccounts { get; } = new();
    public ObservableCollection<NetworkIpOption> AvailableLocalIps { get; } = new();

    partial void OnSelectedLocalAccountChanged(LocalAccountOption? value)
    {
        if (value == null) return;
        WizInputUser = value.Username;
        WizInputDomain = value.Domain;
    }

    partial void OnSelectedPairIpChanged(NetworkIpOption? value)
    {
        if (value == null || string.IsNullOrWhiteSpace(value.IpAddress))
        {
            _pairingContext.HostIpAddress = string.Empty;
            return;
        }

        WizIpOnly = value.IpAddress;
        _pairingContext.HostIpAddress = value.IpAddress;

        if (_pairingContext.SelectedTransport == Common.TransportType.Network && StepPairingVis)
        {
            RefreshNetworkQrPayload();
        }
    }


    public MainViewModel(
        FirewallService firewall,
        ProviderSetupService providerSetup,
        CertificateManager certManager,
        NetworkPairingService networkPairing,
        NetworkService networkService,
        QrCodeService qrCodeService,
        ProviderLocatorService providerLocator,
        BluetoothService bluetoothService,
        IDialogService dialogService,
        FaqContentService faqContentService,
        UpdateService updateService,
        EncryptedBackupService encryptedBackupService)
    {
        _firewall = firewall;
        _providerSetup = providerSetup;
        _certManager = certManager;
        _networkPairing = networkPairing;
        _networkService = networkService;
        _qrCodeService = qrCodeService;
        _providerLocator = providerLocator;
        _bluetoothService = bluetoothService;
        _dialogService = dialogService;
        _faqContentService = faqContentService;
        _updateService = updateService;
        _encryptedBackupService = encryptedBackupService;
        _updateToastTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _updateToastTimer.Tick += (_, _) =>
        {
            _updateToastTimer.Stop();
            ShowUpdateToast = false;
        };

        _config = PortalWinConfig.Load();
        LoadConfigToUi();
        UpdateCurrentVersionText = AppVersion;
        _ = LoadAboutAvatarsAsync();
    }

    public async Task InitializeAsync()
    {
        Logger.Log("[MainViewModel] Initializing...");
        await RunBusyOperationAsync(
            "Starting Portal",
            "Running initial Host checks...",
            _ => RefreshStatusAsync(),
            minimumDisplayDuration: TimeSpan.FromSeconds(0.9),
            canCancel: false);

        ApplyLastUpdateResult();
        _ = RunScheduledUpdateCheckAsync();
    }

    private void ApplyLastUpdateResult()
    {
        var lastResult = _updateService.TryReadLastUpdateResult();
        if (lastResult == null)
        {
            return;
        }

        HasUpdateBanner = true;
        UpdateBannerText = lastResult.Success
            ? lastResult.Summary
            : lastResult.RollbackAttempted
                ? $"{lastResult.Summary} {lastResult.Details} Rollback: {(lastResult.RollbackSucceeded ? "completed." : "failed.")}".Trim()
                : $"{lastResult.Summary} {lastResult.Details}".Trim();
        UpdateStatusTitle = lastResult.Success ? "Last update completed" : "Last update failed";
        UpdateStatusMessage = lastResult.Details;
        if (lastResult.Success)
        {
            UpdateAvailableVersionText = $"Installed version: {lastResult.TargetVersion}";
            _config.LastInstalledUpdateUtc = lastResult.CompletedAtUtc;
            _config.Save();
            UpdateLastInstalledText = $"Last update: {lastResult.CompletedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        }

        _updateService.ClearLastUpdateResult();
    }

    private void LoadConfigToUi()
    {
        SettingsPort = _config.Port.ToString();
        SettingsHostRequestTimeoutMinutes = _config.HostRequestTimeoutMinutes.ToString();
        IsVpnCompatibilityModeEnabled = _config.VpnCompatibilityModeEnabled;
        AreExperimentalFeaturesEnabled = _config.ExperimentalFeaturesEnabled;
        UpdateSourceText = $"Source: {UpdateService.BuiltInSourceLabel}";
        UpdateLastCheckedText = _config.LastUpdateCheckUtc.HasValue
            ? $"Last checked: {_config.LastUpdateCheckUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "Not checked yet";
        UpdateLastInstalledText = _config.LastInstalledUpdateUtc.HasValue
            ? $"Last update: {_config.LastInstalledUpdateUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "Last update: not installed yet";
        UpdateRepositoryText = _config.UpdateRepository;
        UpdateTokenText = string.Empty;
        IsAutoUpdateChecksEnabled = _config.AutoUpdateChecksEnabled;
        if (!AreExperimentalFeaturesEnabled)
        {
            ShowFaq = false;
            ShowUpdateWizard = false;
        }
        IsModeClient = _config.UnlockMode == UnlockMode.ClientInitiated;
        IsModeHost = _config.UnlockMode == UnlockMode.HostInitiated;
        IsModeBoth = _config.UnlockMode == UnlockMode.Both;

        IsTriggerOnClick = _config.HostRequestTrigger == HostRequestTrigger.OnClick;
        IsTriggerOnClickAndStartup = _config.HostRequestTrigger == HostRequestTrigger.OnClickAndStartup;
        IsTriggerOnClickAndAnyLockScreen = _config.HostRequestTrigger == HostRequestTrigger.OnClickAndAnyLockScreen;
        _suppressDuplicateProtectionPrompt = true;
        IsDuplicateAccountProtectionEnabled = _config.EnforceUniqueAccountPerTransport;
        IsCrossTransportDuplicateProtectionEnabled = _config.EnforceUniqueAccountAcrossTransports && IsDuplicateAccountProtectionEnabled;
        _suppressDuplicateProtectionPrompt = false;

        RefreshDevicesList();
    }

    private async Task LoadAboutAvatarsAsync()
    {
        var androidAvatarTask = AvatarImageLoader.LoadAs96DpiAsync(AboutAndroidDeveloperAvatarUrl);
        var desktopAvatarTask = AvatarImageLoader.LoadAs96DpiAsync(AboutDesktopDeveloperAvatarUrl);

        AboutAndroidDeveloperAvatarImage = await androidAvatarTask;
        AboutDesktopDeveloperAvatarImage = await desktopAvatarTask;
    }

    private async Task ConfirmDisableDuplicateProtectionAsync()
    {
        var confirm = await _dialogService.ShowNotificationAsync(
            "Disable Protection?",
            "Turning this off may break host-initiated request routing when one account is linked to multiple devices on the same transport. Continue?",
            true);

        if (!confirm)
        {
            _suppressDuplicateProtectionPrompt = true;
            IsDuplicateAccountProtectionEnabled = true;
            IsCrossTransportDuplicateProtectionEnabled = _previousCrossTransportProtectionEnabled;
            _suppressDuplicateProtectionPrompt = false;
        }
    }

    private void BeginBusyOperation(string actionTitle, string actionStatus, bool canCancel)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsBusyOperationActive = true;
            IsBusyOperationResultVisible = false;
            BusyOperationTitle = actionTitle;
            BusyOperationStatus = actionStatus;
            BusyCancelButtonText = "Cancel action";
            IsBusyOperationCancelable = canCancel;
            CanCancelBusyOperation = canCancel;
            BusyResultButtonText = "OK";
        });
    }

    private void UpdateBusyOperationStatus(string actionStatus)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            BusyOperationStatus = actionStatus;
        });
    }

    private void EndBusyOperation()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsBusyOperationActive = false;
            IsBusyOperationResultVisible = false;
            BusyCancelButtonText = "Cancel action";
            IsBusyOperationCancelable = true;
            CanCancelBusyOperation = true;
        }, System.Windows.Threading.DispatcherPriority.Send);
    }

    private static int ParseHostRequestTimeoutMinutes(string? value)
    {
        if (!int.TryParse(value, out var parsed))
        {
            return 2;
        }

        return Math.Max(0, parsed);
    }

    private async Task ShowBusyResultAsync(string title, string message, string buttonText = "OK")
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _busyResultTcs = tcs;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            BusyOperationTitle = title;
            BusyOperationStatus = message;
            BusyResultButtonText = buttonText;
            BusyCancelButtonText = "Cancel action";
            IsBusyOperationCancelable = false;
            CanCancelBusyOperation = false;
            IsBusyOperationActive = false;
            IsBusyOperationResultVisible = true;
        }, System.Windows.Threading.DispatcherPriority.Send);

        await tcs.Task;
    }

    private async Task<BusyOperationOutcome> RunBusyOperationAsync(
        string actionTitle,
        string actionStatus,
        Func<CancellationToken, Task> operation,
        TimeSpan? minimumDisplayDuration = null,
        bool canCancel = true,
        bool keepOverlayForResult = false)
    {
        if (IsBusyOperationActive || IsBusyOperationResultVisible)
        {
            return BusyOperationOutcome.Skipped;
        }

        using var cancellationTokenSource = new CancellationTokenSource();
        _busyOperationCts = cancellationTokenSource;
        BeginBusyOperation(actionTitle, actionStatus, canCancel);
        var startedAt = Stopwatch.StartNew();
        var completedWithoutError = false;

        try
        {
            await operation(cancellationTokenSource.Token);
            completedWithoutError = true;
            return BusyOperationOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            Logger.LogWarning($"[MainViewModel] Busy operation cancelled: {actionTitle}");
            completedWithoutError = true;
            return BusyOperationOutcome.Cancelled;
        }
        finally
        {
            if (minimumDisplayDuration.HasValue)
            {
                var remaining = minimumDisplayDuration.Value - startedAt.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(remaining);
                    }
                    catch
                    {
                    }
                }
            }

            if (ReferenceEquals(_busyOperationCts, cancellationTokenSource))
            {
                _busyOperationCts = null;
            }

            if (!(keepOverlayForResult && completedWithoutError))
            {
                EndBusyOperation();
            }
        }
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            IsStatusReady = false;
            _config = PortalWinConfig.Load();
            AreExperimentalFeaturesEnabled = _config.ExperimentalFeaturesEnabled;
            if (!AreExperimentalFeaturesEnabled)
            {
                ShowFaq = false;
            }

            var isFirewallOk = await _firewall.CheckFirewallRule(_config.Port);
            var isCertOk = _certManager.CheckCertificate();
            var providerDllPath = _providerLocator.FindProviderDll(SettingsDllPath);
            var providerHealth = _providerSetup.CheckProviderHealth(providerDllPath);
            var ips = await _networkService.GetLocalIPsAsync(_config.VpnCompatibilityModeEnabled);

            Application.Current.Dispatcher.Invoke(() =>
            {
                IsRegisteredOk = providerHealth.CredentialProviderGuidsOk && providerHealth.ComRegistrationOk;
                IsFilesOk = providerHealth.FilesOk;
                IsFirewallOk = isFirewallOk;
                IsCertOk = isCertOk;
                IsServiceActive = providerHealth.IsHealthy;
                ProviderInstallButtonText = providerHealth.IsHealthy ? "Reinstall" : "Install";

                MainStatusText = IsServiceActive
                    ? "✓ Service Active & Ready"
                    : $"⚠ {(providerHealth.FailureReasons.FirstOrDefault() ?? "Service Not Installed")}";

                var setupIssues = new List<string>();
                if (!providerHealth.IsHealthy) setupIssues.Add("Credential Provider is not installed or is damaged.");
                if (!isFirewallOk) setupIssues.Add("Firewall rules are missing.");
                if (!isCertOk) setupIssues.Add("Host SSL certificate is missing.");

                HasSetupIssues = setupIssues.Count > 0;
                SetupIssueTitle = setupIssues.Count > 0
                    ? setupIssues[0]
                    : "All core components are configured.";
                SetupIssueHint = setupIssues.Count > 0
                    ? "Click START / ACTIVATE to auto-fix. If needed: Advanced Settings -> System Health -> Reinstall Provider / Fix Firewall / Regenerate Certificate."
                    : "No setup actions required.";

                ClientCountText = $"{_config.Devices.Count} trusted devices";
                RefreshDevicesList();

                ShowSetupPanel = !(IsServiceActive && _config.Devices.Any());
                OnPropertyChanged(nameof(ShowConnectedPanel));

                IpAddressText = "Your IP for client: " + (ips.FirstOrDefault() ?? "Unknown");

                // Do not overwrite unsaved Settings edits while Settings screen is open.
                if (ShowDashboard)
                {
                    SettingsPort = _config.Port.ToString();
                    SettingsHostRequestTimeoutMinutes = _config.HostRequestTimeoutMinutes.ToString();
                    IsVpnCompatibilityModeEnabled = _config.VpnCompatibilityModeEnabled;
                    _suppressDuplicateProtectionPrompt = true;
                    IsDuplicateAccountProtectionEnabled = _config.EnforceUniqueAccountPerTransport;
                    IsCrossTransportDuplicateProtectionEnabled = _config.EnforceUniqueAccountAcrossTransports && IsDuplicateAccountProtectionEnabled;
                    _suppressDuplicateProtectionPrompt = false;
                }
                IsStatusReady = true;
            });
        }
        catch
        {
            IsStatusReady = false;
        }
    }

    private void RefreshDevicesList()
    {
        Devices.Clear();
        foreach (var d in _config.Devices) Devices.Add(d);
    }

    // --- Commands (Dashboard & General) ---

    [RelayCommand]
    private async Task StartWizardAsync()
    {
        var providerDllPath = string.Empty;
        var infrastructureReady = false;

        var outcome = await RunBusyOperationAsync(
            "Preparing setup wizard",
            "Checking Host readiness before opening setup...",
            async cancellationToken =>
            {
                providerDllPath = _providerLocator.FindProviderDll(SettingsDllPath) ?? "";
                if (!string.IsNullOrWhiteSpace(providerDllPath))
                {
                    SettingsDllPath = providerDllPath;
                }

                var firewallOk = await _firewall.CheckFirewallRule(_config.Port, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var certOk = _certManager.CheckCertificate();
                var providerHealth = _providerSetup.CheckProviderHealth(providerDllPath);
                infrastructureReady = firewallOk && certOk && providerHealth.IsHealthy;
            });

        if (outcome == BusyOperationOutcome.Completed)
        {
            await StartWizardFlow(isAddingDevice: infrastructureReady);
        }
    }

    [RelayCommand]
    private async Task AddDeviceAsync() => await StartWizardFlow(true);

    [RelayCommand]
    private void ShowSettings()
    {
        ShowDashboard = false;
        OnPropertyChanged(nameof(ShowSettingsPanel));
        _ = RefreshStatusAsync();
    }

    [RelayCommand]
    private void CloseSettings()
    {
        ShowDashboard = true;
        OnPropertyChanged(nameof(ShowSettingsPanel));
    }

    [RelayCommand]
    private void ShowAbout()
    {
        ShowAboutDialog = true;
    }

    [RelayCommand]
    private void CloseAbout()
    {
        ShowAboutDialog = false;
    }

    [RelayCommand]
    private void RegisterAboutVersionTap()
    {
        if (IsDiagnosticsUnlocked)
        {
            return;
        }

        _aboutVersionTapCount++;
        if (_aboutVersionTapCount < 5)
        {
            return;
        }

        IsDiagnosticsUnlocked = true;
        Logger.Log("[About] Diagnostics section unlocked for current session.");
    }

    [RelayCommand]
    private async Task OpenExternalLinkAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Logger.Log($"[About] Opening external link: {url}");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.LogError("OpenExternalLink", ex);
            await _dialogService.ShowNotificationAsync("Link open failed", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenCreateBackupDialog()
    {
        ShowCreateBackupDialog = true;
    }

    [RelayCommand]
    private void OpenRestoreBackupDialog()
    {
        ShowRestoreBackupDialog = true;
    }

    [RelayCommand]
    private void CloseCreateBackupDialog()
    {
        ShowCreateBackupDialog = false;
    }

    [RelayCommand]
    private void CloseRestoreBackupDialog()
    {
        ShowRestoreBackupDialog = false;
    }

    [RelayCommand]
    private Task OpenLogsWindowAsync()
    {
        OpenLogsWindowRequested?.Invoke();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CancelBusyOperationAsync()
    {
        if (_busyOperationCts == null || _busyOperationCts.IsCancellationRequested || !IsBusyOperationActive)
        {
            return;
        }

        var confirmed = await _dialogService.ShowNotificationAsync(
            "Cancel current action?",
            "Cancelling now may leave Portal partially configured and can require manual recovery or reinstall. Continue?",
            true);

        if (!confirmed)
        {
            return;
        }

        CanCancelBusyOperation = false;
        BusyCancelButtonText = "Cancelling...";
        UpdateBusyOperationStatus("Stopping current action. Please wait...");
        _busyOperationCts.Cancel();
    }

    [RelayCommand]
    private void AcknowledgeBusyResult()
    {
        IsBusyOperationActive = false;
        IsBusyOperationResultVisible = false;
        BusyOperationTitle = "Working";
        BusyOperationStatus = "Please wait...";
        IsBusyOperationCancelable = true;
        CanCancelBusyOperation = true;
        BusyResultButtonText = "OK";
        _busyResultTcs?.TrySetResult(true);
        _busyResultTcs = null;
    }

    [ObservableProperty] private bool _isWorkingDelete = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusyOverlayVisible))]
    private bool _isBusyOperationActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusyOverlayVisible))]
    private bool _isBusyOperationResultVisible;

    public bool IsBusyOverlayVisible => IsBusyOperationActive || IsBusyOperationResultVisible;

    [ObservableProperty] private string _busyOperationTitle = "Working";
    [ObservableProperty] private string _busyOperationStatus = "Please wait...";
    [ObservableProperty] private bool _isBusyOperationCancelable = true;
    [ObservableProperty] private bool _canCancelBusyOperation = true;
    [ObservableProperty] private string _busyCancelButtonText = "Cancel action";
    [ObservableProperty] private string _busyResultButtonText = "OK";

    [RelayCommand]
    private async Task RemoveDeviceAsync(string clientId)
    {
        var confirmed = await _dialogService.ShowNotificationAsync("Confirm", "Delete this device?", true);
        if (!confirmed)
        {
            return;
        }

        var deviceName = _config.FindDeviceByClientId(clientId)?.Name ?? "device";
        var outcome = await RunBusyOperationAsync(
            "Deleting trusted device",
            $"Removing '{deviceName}' from the trusted devices list...",
            async cancellationToken =>
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var device = _config.FindDeviceByClientId(clientId);
                    if (device != null)
                    {
                        Logger.Log($"Removing device: {device.Name} ({device.ClientId})");
                        Application.Current.Dispatcher.Invoke(() => _config.Devices.Remove(device));
                        cancellationToken.ThrowIfCancellationRequested();
                        _config.Save();
                    }
                }, cancellationToken);

                await Task.Delay(500, cancellationToken);
            },
            keepOverlayForResult: true);

        await RefreshStatusAsync();
        if (outcome == BusyOperationOutcome.Completed)
        {
            RefreshDevicesList();
            await ShowBusyResultAsync("Device deleted", $"'{deviceName}' was removed from trusted devices.");
        }
        else if (outcome == BusyOperationOutcome.Cancelled)
        {
            await ShowBusyResultAsync("Deletion cancelled", "Device deletion was cancelled. Please verify the device list before continuing.");
        }
    }

    [RelayCommand]
    private void ToggleDeviceEnabled(DeviceModel? device)
    {
        if (device == null)
        {
            return;
        }

        var configDevice = _config.FindDeviceByClientId(device.ClientId);
        if (configDevice == null)
        {
            return;
        }

        configDevice.IsEnabled = device.IsEnabled;
        _config.Save();
        RefreshDevicesList();
        _ = RefreshStatusAsync();
    }

    [RelayCommand]
    private void EditDeviceAccount(string clientId)
    {
        var device = _config.Devices.FirstOrDefault(d => d.ClientId == clientId);
        if (device == null) return;

        _editingClientId = clientId;

        var account = device.Accounts.FirstOrDefault();
        if (account == null)
        {
            account = new Portal.Common.Models.DeviceAccount();
            device.Accounts.Add(account);
        }

        LoadAvailableLocalAccounts();
        var editDomain = string.IsNullOrWhiteSpace(account.Domain) ? Environment.UserDomainName : account.Domain;
        SelectedLocalAccount = AvailableLocalAccounts.FirstOrDefault(a =>
            string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Domain, editDomain, StringComparison.OrdinalIgnoreCase));

        if (SelectedLocalAccount == null)
        {
            SelectedLocalAccount = new LocalAccountOption { Username = account.Username, Domain = editDomain };
            AvailableLocalAccounts.Insert(0, SelectedLocalAccount);
        }

        WizInputUser = SelectedLocalAccount.Username;
        WizInputDomain = SelectedLocalAccount.Domain;
        WizDeviceName = device.Name;
        WizInputPass = ""; // don't show existing password securely
        WizShowDeviceNameEdit = true;
        WizCredsNextText = "Save Changes";

        ShowDashboard = true; // This hides Settings Panel
        ShowWizard = true;
        SetShowStep("StepCreds");
    }

    [RelayCommand]
    private void CancelEdit()
    {
        _editingClientId = null;
        ShowWizard = false;
        ShowDashboard = false; // Return to Settings
    }

    // --- Commands (Settings actions) ---

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        SaveConfigBtnText = "Saving Configuration...";
        try
        {
            var outcome = await RunBusyOperationAsync(
                "Saving configuration",
                "Writing Portal settings...",
                async cancellationToken =>
                {
                    int port = int.TryParse(SettingsPort, out var parsedPort) ? parsedPort : 29170;
                    int hostRequestTimeoutMinutes = ParseHostRequestTimeoutMinutes(SettingsHostRequestTimeoutMinutes);

                    UnlockMode mode = UnlockMode.ClientInitiated;
                    if (IsModeHost) mode = UnlockMode.HostInitiated;
                    if (IsModeBoth) mode = UnlockMode.Both;

                    HostRequestTrigger trigger = HostRequestTrigger.OnClick;
                    if (IsTriggerOnClickAndStartup) trigger = HostRequestTrigger.OnClickAndStartup;
                    if (IsTriggerOnClickAndAnyLockScreen) trigger = HostRequestTrigger.OnClickAndAnyLockScreen;

                    UpdateBusyOperationStatus("Saving configuration to disk...");
                    await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        _config.Port = port;
                        _config.UnlockMode = mode;
                        _config.HostRequestTrigger = trigger;
                        _config.HostRequestTimeoutMinutes = hostRequestTimeoutMinutes;
                        _config.VpnCompatibilityModeEnabled = IsVpnCompatibilityModeEnabled;
                        _config.EnforceUniqueAccountPerTransport = IsDuplicateAccountProtectionEnabled;
                        _config.EnforceUniqueAccountAcrossTransports = IsDuplicateAccountProtectionEnabled && IsCrossTransportDuplicateProtectionEnabled;
                        _config.Save();
                    }, cancellationToken);
                },
                minimumDisplayDuration: TimeSpan.FromSeconds(1.2),
                keepOverlayForResult: true);

            await RefreshStatusAsync();

            if (outcome == BusyOperationOutcome.Completed)
            {
                SettingsPort = _config.Port.ToString();
                SettingsHostRequestTimeoutMinutes = _config.HostRequestTimeoutMinutes.ToString();
                await ShowBusyResultAsync("Configuration saved", "Host configuration was saved. Firewall rules were not changed automatically.");
            }
            else if (outcome == BusyOperationOutcome.Cancelled)
            {
                await ShowBusyResultAsync("Saving cancelled", "Saving was cancelled. Some settings may already be written, so please review Host status before proceeding.");
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowNotificationAsync("Error", $"Error saving settings: {ex.Message}");
        }
        finally
        {
            SaveConfigBtnText = "Save Configuration";
        }
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        var confirmed = await _dialogService.ShowNotificationAsync("Confirm", "Uninstall Portal service completely?", true);
        if (!confirmed)
        {
            return;
        }

        UninstallBtnText = "Uninstalling...";
        try
        {
            string dllPath = _providerLocator.FindProviderDll(SettingsDllPath) ?? "";
            var outcome = await RunBusyOperationAsync(
                "Uninstalling Portal",
                "Removing firewall rules, certificate, and Credential Provider...",
                async cancellationToken =>
                {
                    UpdateBusyOperationStatus("Removing Windows Firewall rules...");
                    await _firewall.RemoveFirewallRule(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    UpdateBusyOperationStatus("Removing host certificate...");
                    await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _certManager.RemoveCertificate(_config);
                    }, cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();
                    UpdateBusyOperationStatus("Unregistering Credential Provider...");
                    await _providerSetup.UninstallProviderAsync(dllPath, cancellationToken);
                },
                keepOverlayForResult: true);

            await RefreshStatusAsync();

            if (outcome == BusyOperationOutcome.Completed)
            {
                await ShowBusyResultAsync("Uninstall complete", "Portal was uninstalled successfully.");
            }
            else if (outcome == BusyOperationOutcome.Cancelled)
            {
                await ShowBusyResultAsync("Uninstall cancelled", "Uninstall was cancelled. Please verify system health before using Portal again.");
            }
        }
        finally
        {
            UninstallBtnText = "Uninstall Everything";
        }
    }

    [RelayCommand]
    private async Task FixFirewallAsync()
    {
        int port = _config.Port;
        var confirmed = await _dialogService.ShowNotificationAsync("Confirm", $"Re-add firewall rules for port {port}?", true);
        if (!confirmed)
        {
            return;
        }

        var outcome = await RunBusyOperationAsync(
            "Updating firewall rules",
            $"Rebuilding Portal firewall rules for port {port}...",
            async cancellationToken =>
            {
                await _firewall.RemoveFirewallRule(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await _firewall.AddFirewallRule(port, cancellationToken);
            },
            keepOverlayForResult: true);

        await RefreshStatusAsync();
        if (outcome == BusyOperationOutcome.Completed)
        {
            await ShowBusyResultAsync("Firewall updated", "Firewall rules were updated.");
        }
        else if (outcome == BusyOperationOutcome.Cancelled)
        {
            await ShowBusyResultAsync("Firewall update cancelled", "Firewall update was cancelled. Rules may be partially applied.");
        }
    }

    [RelayCommand]
    private async Task RemoveFirewallRulesAsync()
    {
        var confirmed = await _dialogService.ShowNotificationAsync("Confirm", "Delete all Portal firewall rules?", true);
        if (!confirmed)
        {
            return;
        }

        var outcome = await RunBusyOperationAsync(
            "Deleting firewall rules",
            "Removing all Portal firewall rules from Windows Firewall...",
            cancellationToken => _firewall.RemoveFirewallRule(cancellationToken),
            keepOverlayForResult: true);

        await RefreshStatusAsync();
        if (outcome == BusyOperationOutcome.Completed)
        {
            await ShowBusyResultAsync("Firewall rules deleted", "All Portal firewall rules were removed.");
        }
        else if (outcome == BusyOperationOutcome.Cancelled)
        {
            await ShowBusyResultAsync("Deletion cancelled", "Firewall rule deletion was cancelled. Please re-check firewall status.");
        }
    }

    [RelayCommand]
    private async Task FixCertAsync()
    {
        var confirmed = await _dialogService.ShowNotificationAsync(
            "Confirm",
            "Regenerate SSL certificate?\n\nAll currently trusted clients will become invalid and will need to be paired again.",
            true);
        if (!confirmed)
        {
            return;
        }

        var outcome = await RunBusyOperationAsync(
            "Regenerating SSL certificate",
            "Creating a new host certificate. Clients will need to re-pair afterwards...",
            async cancellationToken =>
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _certManager.RemoveCertificate(_config);
                    cancellationToken.ThrowIfCancellationRequested();
                    _certManager.CreateOrLoadCertificate(_config);
                }, cancellationToken);
            },
            minimumDisplayDuration: TimeSpan.FromSeconds(1.2),
            keepOverlayForResult: true);

        await RefreshStatusAsync();
        if (outcome == BusyOperationOutcome.Completed)
        {
            await ShowBusyResultAsync(
                "Certificate regenerated",
                "SSL certificate was regenerated successfully. Existing trusted clients are now invalid and must be re-paired.");

            var invalidClientsCount = _config.Devices.Count;
            if (invalidClientsCount > 0)
            {
                var deleteAllClients = await _dialogService.ShowNotificationAsync(
                    "Delete invalid clients?",
                    $"After SSL regeneration, {invalidClientsCount} trusted client(s) are invalid. Delete all of them now?",
                    true);

                if (deleteAllClients)
                {
                    var deleteOutcome = await RunBusyOperationAsync(
                        "Deleting invalid clients",
                        "Removing trusted clients that no longer match the regenerated certificate...",
                        async cancellationToken =>
                        {
                            await Task.Run(() =>
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                _config.Devices.Clear();
                                _config.Save();
                            }, cancellationToken);
                        },
                        keepOverlayForResult: true);

                    await RefreshStatusAsync();

                    if (deleteOutcome == BusyOperationOutcome.Completed)
                    {
                        await ShowBusyResultAsync(
                            "Clients deleted",
                            "All invalid trusted clients were removed. Re-pair devices to continue using unlock.");
                    }
                    else if (deleteOutcome == BusyOperationOutcome.Cancelled)
                    {
                        await ShowBusyResultAsync(
                            "Deletion cancelled",
                            "Invalid clients were kept. You can remove them later from Connected Devices.");
                    }
                }
            }
        }
        else if (outcome == BusyOperationOutcome.Cancelled)
        {
            await ShowBusyResultAsync("Certificate action cancelled", "Certificate regeneration was cancelled. Host certificate state should be verified.");
        }
    }

    [RelayCommand]
    private async Task InstallProviderAsync()
    {
        var dllPath = _providerLocator.FindProviderDll(SettingsDllPath);
        if (string.IsNullOrEmpty(dllPath)) { await _dialogService.ShowNotificationAsync("Error", "DLL not found"); return; }

        try
        {
            var healthBefore = _providerSetup.CheckProviderHealth(dllPath);
            var hasExistingRegistration = healthBefore.CredentialProviderGuidsOk || healthBefore.ComRegistrationOk;

            var outcome = await RunBusyOperationAsync(
                hasExistingRegistration ? "Reinstalling Credential Provider" : "Installing Credential Provider",
                hasExistingRegistration
                    ? "Removing old provider registration before reinstall..."
                    : "Registering Credential Provider...",
                async cancellationToken =>
                {
                    if (hasExistingRegistration)
                    {
                        UpdateBusyOperationStatus("Removing old Credential Provider registration...");
                        await _providerSetup.UninstallProviderAsync(dllPath, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    UpdateBusyOperationStatus("Registering Credential Provider...");
                    await _providerSetup.InstallProviderAsync(dllPath, cancellationToken);
                },
                minimumDisplayDuration: TimeSpan.FromSeconds(1.2),
                keepOverlayForResult: true);

            await RefreshStatusAsync();

            if (outcome == BusyOperationOutcome.Completed)
            {
                await ShowBusyResultAsync(
                    hasExistingRegistration ? "Provider reinstalled" : "Provider installed",
                    hasExistingRegistration
                    ? "Provider reinstalled successfully."
                    : "Provider installed successfully.");
            }
            else if (outcome == BusyOperationOutcome.Cancelled)
            {
                await ShowBusyResultAsync("Provider action cancelled", "Provider installation was cancelled. Please verify provider health before continuing.");
            }
        }
        catch (Exception ex)
        {
            await RefreshStatusAsync();
            await _dialogService.ShowNotificationAsync("Error", $"Installation failed:\n{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task UninstallProviderAsync()
    {
        var dllPath = _providerLocator.FindProviderDll(SettingsDllPath);
        if (string.IsNullOrEmpty(dllPath)) { await _dialogService.ShowNotificationAsync("Error", "DLL not found"); return; }

        var outcome = await RunBusyOperationAsync(
            "Uninstalling Credential Provider",
            "Removing Credential Provider registration...",
            cancellationToken => _providerSetup.UninstallProviderAsync(dllPath, cancellationToken),
            minimumDisplayDuration: TimeSpan.FromSeconds(1.2),
            keepOverlayForResult: true);

        await RefreshStatusAsync();
        if (outcome == BusyOperationOutcome.Completed)
        {
            await ShowBusyResultAsync("Provider uninstalled", "Credential Provider was uninstalled.");
        }
        else if (outcome == BusyOperationOutcome.Cancelled)
        {
            await ShowBusyResultAsync("Provider action cancelled", "Provider uninstall was cancelled. Please verify provider health.");
        }
    }

    [RelayCommand]
    private async Task ResetAllAsync()
    {
        var confirmed = await _dialogService.ShowNotificationAsync("Confirm Reset", "This will reset all settings, remove all devices, and certificates. Continue?", true);
        if (!confirmed)
        {
            return;
        }

        string dllPath = _providerLocator.FindProviderDll(SettingsDllPath) ?? "";
        var outcome = await RunBusyOperationAsync(
            "Resetting Portal",
            "Removing devices, certificates, firewall rules, and provider registration...",
            async cancellationToken =>
            {
                UpdateBusyOperationStatus("Removing Windows Firewall rules...");
                await _firewall.RemoveFirewallRule(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                UpdateBusyOperationStatus("Removing host certificate...");
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _certManager.RemoveCertificate(_config);
                }, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                UpdateBusyOperationStatus("Unregistering Credential Provider...");
                await _providerSetup.UninstallProviderAsync(dllPath, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                UpdateBusyOperationStatus("Clearing paired devices and saving clean configuration...");
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _config.Devices.Clear();
                    _config.Save();
                }, cancellationToken);
            },
            keepOverlayForResult: true);

        await RefreshStatusAsync();
        if (outcome == BusyOperationOutcome.Completed)
        {
            await ShowBusyResultAsync("Reset complete", "Portal was reset successfully.");
        }
        else if (outcome == BusyOperationOutcome.Cancelled)
        {
            await ShowBusyResultAsync("Reset cancelled", "Reset was cancelled. Please review current Host health before continuing.");
        }
    }

    [RelayCommand]
    private void OpenUpdateWizard()
    {
        ResetUpdateWizardVisualState();
        ShowUpdateWizard = true;
    }

    [RelayCommand]
    private async Task CreateEncryptedBackupAsync(BackupPasswordPayload? payload)
    {
        if (payload == null)
        {
            await _dialogService.ShowNotificationAsync("Backup error", "Password is required to create encrypted backup.");
            return;
        }

        byte[]? serverCertificatePfx = null;
        try
        {
            if (payload.Password.Length == 0 || payload.ConfirmPassword.Length == 0)
            {
                await _dialogService.ShowNotificationAsync("Backup error", "Please fill in both password fields.");
                return;
            }

            if (!SecureStringsEqual(payload.Password, payload.ConfirmPassword))
            {
                await _dialogService.ShowNotificationAsync("Backup error", "Passwords do not match.");
                return;
            }

            if (payload.Password.Length < 8)
            {
                await _dialogService.ShowNotificationAsync("Backup error", "Use password length of at least 8 characters.");
                return;
            }

            if (_config.Devices.Count == 0)
            {
                await _dialogService.ShowNotificationAsync("Backup", "No trusted devices to back up.");
                return;
            }

            if (!File.Exists(CertificateService.DefaultCertPath))
            {
                await _dialogService.ShowNotificationAsync(
                    "Backup error",
                    $"Server certificate is missing: {CertificateService.DefaultCertPath}\nGenerate/fix certificate first, then retry backup.");
                return;
            }

            var defaultName = $"Portal-Backup-{DateTime.Now:yyyyMMdd-HHmmss}{EncryptedBackupService.BackupFileExtension}";
            var saveDialog = new SaveFileDialog
            {
                Title = "Save encrypted backup",
                FileName = defaultName,
                DefaultExt = EncryptedBackupService.BackupFileExtension,
                AddExtension = true,
                Filter = EncryptedBackupService.BackupFileFilter,
                OverwritePrompt = true
            };

            var saveResult = saveDialog.ShowDialog();
            if (saveResult != true || string.IsNullOrWhiteSpace(saveDialog.FileName))
            {
                return;
            }

            var devicesSnapshot = _config.Devices.Select(d => d).ToList();
            serverCertificatePfx = await File.ReadAllBytesAsync(CertificateService.DefaultCertPath);
            ShowCreateBackupDialog = false;
            var outcome = await RunBusyOperationAsync(
                "Creating encrypted backup",
                "Encrypting trusted devices and server certificate with AES-256...",
                async cancellationToken =>
                {
                    UpdateBusyOperationStatus("Deriving key and encrypting data...");
                    await _encryptedBackupService.CreateDeviceBackupAsync(
                        saveDialog.FileName,
                        _config,
                        devicesSnapshot,
                        serverCertificatePfx,
                        payload.Password,
                        cancellationToken);
                },
                minimumDisplayDuration: TimeSpan.FromSeconds(1.2),
                keepOverlayForResult: true);

            if (outcome == BusyOperationOutcome.Completed)
            {
                ShowCreateBackupDialog = false;
                await ShowBusyResultAsync("Backup created", $"Encrypted backup was saved to:\n{saveDialog.FileName}");
            }
            else if (outcome == BusyOperationOutcome.Cancelled)
            {
                ShowCreateBackupDialog = false;
                await ShowBusyResultAsync("Backup cancelled", "Encrypted backup creation was cancelled.");
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowNotificationAsync("Backup error", $"Failed to create backup: {ex.Message}");
        }
        finally
        {
            if (serverCertificatePfx != null)
            {
                Array.Clear(serverCertificatePfx, 0, serverCertificatePfx.Length);
            }
            payload.Dispose();
        }
    }

    [RelayCommand]
    private Task SelectRestoreBackupFileAsync()
    {
        var openDialog = new OpenFileDialog
        {
            Title = "Select encrypted backup",
            DefaultExt = EncryptedBackupService.BackupFileExtension,
            Filter = EncryptedBackupService.BackupFileFilter,
            CheckFileExists = true,
            Multiselect = false
        };

        var result = openDialog.ShowDialog();
        if (result == true && !string.IsNullOrWhiteSpace(openDialog.FileName))
        {
            _restoreBackupFilePath = openDialog.FileName;
            RestoreBackupFileText = Path.GetFileName(openDialog.FileName);
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RestoreEncryptedBackupAsync(RestoreBackupPayload? payload)
    {
        if (payload == null)
        {
            await _dialogService.ShowNotificationAsync("Restore error", "Backup file and password are required.");
            return;
        }

        try
        {
            if (payload.Password.Length == 0)
            {
                await _dialogService.ShowNotificationAsync("Restore error", "Please enter backup password.");
                return;
            }

            if (!File.Exists(payload.FilePath))
            {
                await _dialogService.ShowNotificationAsync("Restore error", $"Backup file not found:\n{payload.FilePath}");
                return;
            }

            DecryptedBackupData? restoredData = null;
            var restoreSkippedAsNoChanges = false;
            try
            {
                ShowRestoreBackupDialog = false;
                var outcome = await RunBusyOperationAsync(
                    "Restoring from backup",
                    "Scanning backup package and preparing restore...",
                    async cancellationToken =>
                    {
                        UpdateBusyOperationStatus("Scanning backup file...");
                        cancellationToken.ThrowIfCancellationRequested();

                        UpdateBusyOperationStatus("Decrypting backup...");
                        restoredData = await _encryptedBackupService.RestoreDeviceBackupAsync(
                            payload.FilePath,
                            payload.Password,
                            cancellationToken);

                        cancellationToken.ThrowIfCancellationRequested();
                        UpdateBusyOperationStatus("Comparing with current state...");
                        var currentConfigJson = JsonSerializer.Serialize(_config);
                        var restoredConfigForCompare = restoredData.Config;
                        restoredConfigForCompare.Devices = restoredData.Devices;
                        var restoredConfigJson = JsonSerializer.Serialize(restoredConfigForCompare);
                        var currentDevicesJson = JsonSerializer.Serialize(_config.Devices);
                        var restoredDevicesJson = JsonSerializer.Serialize(restoredData.Devices);
                        var currentCertBytes = File.Exists(CertificateService.DefaultCertPath)
                            ? File.ReadAllBytes(CertificateService.DefaultCertPath)
                            : Array.Empty<byte>();

                        var sameState =
                            string.Equals(currentConfigJson, restoredConfigJson, StringComparison.Ordinal) &&
                            string.Equals(currentDevicesJson, restoredDevicesJson, StringComparison.Ordinal) &&
                            currentCertBytes.SequenceEqual(restoredData.ServerCertificatePfx);

                        if (currentCertBytes.Length > 0)
                        {
                            Array.Clear(currentCertBytes, 0, currentCertBytes.Length);
                        }

                        if (sameState)
                        {
                            UpdateBusyOperationStatus("No changes detected in backup.");
                            restoreSkippedAsNoChanges = true;
                            return;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        UpdateBusyOperationStatus("Restoring server certificate...");
                        await Task.Run(() =>
                        {
                            var certDir = Path.GetDirectoryName(CertificateService.DefaultCertPath);
                            if (!string.IsNullOrWhiteSpace(certDir) && !Directory.Exists(certDir))
                            {
                                Directory.CreateDirectory(certDir);
                            }

                            File.WriteAllBytes(CertificateService.DefaultCertPath, restoredData.ServerCertificatePfx);
                            if (!string.IsNullOrWhiteSpace(certDir))
                            {
                                CertificateService.EnsureCertPermissions(certDir);
                            }
                        }, cancellationToken);

                        cancellationToken.ThrowIfCancellationRequested();
                        UpdateBusyOperationStatus("Applying config and trusted devices...");
                        await Task.Run(() =>
                        {
                            var restoredConfig = restoredData.Config;
                            restoredConfig.Devices = restoredData.Devices;
                            _config = restoredConfig;
                            _config.Save();
                        }, cancellationToken);
                },
                minimumDisplayDuration: TimeSpan.FromSeconds(1.3),
                keepOverlayForResult: true);

                if (outcome == BusyOperationOutcome.Completed)
                {
                    ShowRestoreBackupDialog = false;
                    if (restoreSkippedAsNoChanges)
                    {
                        await ShowBusyResultAsync(
                            "Already up to date",
                            "Backup matches current configuration, devices and certificate. No changes were applied.");
                    }
                    else
                    {
                        LoadConfigToUi();
                        await RefreshStatusAsync();
                        await ShowBusyResultAsync(
                            "Backup restored",
                            "Configuration, trusted devices and host certificate were restored.");
                    }
                }
                else if (outcome == BusyOperationOutcome.Cancelled)
                {
                    ShowRestoreBackupDialog = false;
                    await ShowBusyResultAsync("Restore cancelled", "Restore operation was cancelled.");
                }
            }
            finally
            {
                restoredData?.Dispose();
            }
        }
        catch (CryptographicException)
        {
            await _dialogService.ShowNotificationAsync("Restore error", "Wrong password or corrupted backup file.");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowNotificationAsync("Restore error", $"Failed to restore backup: {ex.Message}");
        }
        finally
        {
            payload.Dispose();
        }
    }

    public void PrimeRestoreBackupFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        _restoreBackupFilePath = filePath;
        RestoreBackupFileText = Path.GetFileName(filePath);
        ShowDashboard = false;
        ShowRestoreBackupDialog = true;
        OnPropertyChanged(nameof(ShowSettingsPanel));
    }

    public RestoreBackupPayload? CreateRestorePayload(SecureString password)
    {
        if (password == null || password.Length == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_restoreBackupFilePath))
        {
            return null;
        }

        return new RestoreBackupPayload(_restoreBackupFilePath, password);
    }

    [RelayCommand]
    private void DismissUpdateToast()
    {
        _updateToastTimer.Stop();
        ShowUpdateToast = false;
    }

    [RelayCommand]
    private void OpenUpdateWizardFromToast()
    {
        DismissUpdateToast();
        OpenUpdateWizard();
    }

    private void ShowUpdateAvailabilityToast(string message)
    {
        UpdateToastTitle = "New update available";
        UpdateToastMessage = message;
        ShowUpdateToast = true;
        _updateToastTimer.Stop();
        _updateToastTimer.Start();
    }

    [RelayCommand]
    private async Task SaveUpdateSourceAsync()
    {
        string normalizedRepository = string.Empty;
        if (!string.IsNullOrWhiteSpace(UpdateRepositoryText))
        {
            normalizedRepository = NormalizeRepositoryInput(UpdateRepositoryText) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedRepository))
            {
                await _dialogService.ShowNotificationAsync(
                    "Invalid repository",
                    "Use owner/repo or https://github.com/owner/repo");
                return;
            }
        }

        _config.UpdateRepository = normalizedRepository;
        _config.SetUpdateAccessToken(string.IsNullOrWhiteSpace(UpdateTokenText) ? null : UpdateTokenText.Trim());
        _config.Save();
        UpdateRepositoryText = _config.UpdateRepository;
        UpdateTokenText = string.Empty;

        await _dialogService.ShowNotificationAsync(
            "Update source saved",
            string.IsNullOrWhiteSpace(_config.UpdateRepository)
                ? "Using built-in repository. Private token updated."
                : $"Using repository: {_config.UpdateRepository}");
    }

    [RelayCommand]
    private void CloseUpdateWizard()
    {
        if (IsUpdateOperationInProgress)
        {
            return;
        }

        ShowUpdateWizard = false;
    }

    private async Task RunScheduledUpdateCheckAsync()
    {
        if (!IsAutoUpdateChecksEnabled)
        {
            return;
        }

        try
        {
            var silentResult = await _updateService.TryRunScheduledCheckAsync(_config);
            if (silentResult.Status != AppUpdateSilentCheckStatus.Completed || silentResult.CheckResult == null)
            {
                return;
            }

            UpdateLastCheckedText = _config.LastUpdateCheckUtc.HasValue
                ? $"Last checked: {_config.LastUpdateCheckUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : $"Last checked: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

            ApplyUpdateCheckResult(silentResult.CheckResult, isAutomatic: true);

            if (silentResult.CheckResult.Status == AppUpdateAvailabilityStatus.UpdateAvailable)
            {
                HasUpdateBanner = true;
                UpdateBannerText = $"Update available: v{silentResult.CheckResult.Manifest?.Version?.TrimStart('v', 'V') ?? "unknown"}. Open Update Wizard to install.";
                var versionText = silentResult.CheckResult.Manifest?.Version?.TrimStart('v', 'V') ?? "unknown";
                ShowUpdateAvailabilityToast($"Version v{versionText} is available. Click to open Update Wizard.");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[MainViewModel] Scheduled update check failed.", ex);
        }
    }

    private void ApplyUpdateCheckResult(AppUpdateCheckResult result, bool isAutomatic)
    {
        // Always reset visual progress before showing/starting a new install cycle.
        UpdateProgressPercent = 0;
        IsUpdateProgressIndeterminate = true;
        IsUpdateProgressPrimed = false;
        UpdateCurrentStage = AppUpdateStage.Idle;
        UpdateTransferText = "--";
        UpdateSpeedText = "Speed: --";

        switch (result.Status)
        {
            case AppUpdateAvailabilityStatus.UpdateAvailable:
                _availableUpdateManifest = result.Manifest;
                IsUpdateAvailable = result.Manifest != null;
                UpdateStatusTitle = "GitHub update available";
                UpdateStatusMessage = result.Message;
                UpdateSourceText = !string.IsNullOrWhiteSpace(result.Manifest?.SourceRepository)
                    ? $"Source: GitHub Releases - {result.Manifest.SourceRepository}"
                    : $"Source: {UpdateService.BuiltInSourceLabel}";
                UpdateAvailableVersionText = result.Manifest != null ? $"Available version: v{result.Manifest.Version.TrimStart('v', 'V')}" : "Available version: unknown";
                UpdateFileName = string.IsNullOrWhiteSpace(result.Manifest?.PackageFileName)
                    ? "Package file is not specified in release"
                    : result.Manifest!.PackageFileName;
                UpdateTransferText = result.Manifest?.PackageSizeBytes is > 0
                    ? $"Size: {FormatBytes(result.Manifest.PackageSizeBytes.Value)}"
                    : "Size: unknown";
                UpdateSpeedText = result.Manifest?.RequiresProviderReinstall == true
                    ? "Post-install: Credential Provider reinstall"
                    : "Post-install: restart Host";
                break;

            case AppUpdateAvailabilityStatus.NoUpdate:
                IsUpdateAvailable = false;
                _availableUpdateManifest = null;
                if (!isAutomatic)
                {
                    UpdateStatusTitle = "You are up to date";
                    UpdateStatusMessage = result.Message;
                    UpdateAvailableVersionText = "No update detected";
                    UpdateFileName = "No package selected";
                    UpdateTransferText = "--";
                    UpdateSpeedText = "Speed: --";
                }
                break;

            default:
                IsUpdateAvailable = false;
                _availableUpdateManifest = null;
                if (!isAutomatic)
                {
                    UpdateStatusTitle = "Update source unavailable";
                    UpdateStatusMessage = result.Message;
                    UpdateAvailableVersionText = "No update source available";
                    UpdateFileName = "GitHub source unavailable";
                    UpdateTransferText = "--";
                    UpdateSpeedText = "Speed: --";
                }
                break;
        }

        InstallUpdateCommand.NotifyCanExecuteChanged();
    }

    private void ResetUpdateWizardVisualState()
    {
        UpdateProgressPercent = 0;
        // Keep determinate progress hidden until real download progress starts.
        IsUpdateProgressIndeterminate = true;
        IsUpdateProgressPrimed = false;
        UpdateCurrentStage = AppUpdateStage.Idle;
        UpdateTransferText = "--";
        UpdateSpeedText = "Speed: --";
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsUpdateOperationInProgress)
        {
            return;
        }

        IsUpdateOperationInProgress = true;
        IsUpdateProgressIndeterminate = true;
        IsUpdateProgressPrimed = false;
        UpdateCurrentStage = AppUpdateStage.Checking;
        IsUpdateAvailable = false;
        _availableUpdateManifest = null;
        InstallUpdateButtonText = "Install Update";
        UpdateStatusTitle = "Checking for updates";
        UpdateStatusMessage = "Contacting GitHub Releases and comparing versions...";
        UpdateLastCheckedText = $"Checking started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        UpdateFileName = "Manifest";
        UpdateTransferText = "--";
        UpdateSpeedText = "Speed: --";
        UpdateProgressPercent = 0;
        HasUpdateBanner = false;

        AppUpdateCheckResult? result = null;
        var suggestSourceSetup = false;

        try
        {
            result = await _updateService.CheckForUpdatesAsync(_config);
            _config.LastUpdateCheckUtc = DateTime.UtcNow;
            _config.LastDiscoveredUpdateVersion = result.Status == AppUpdateAvailabilityStatus.UpdateAvailable
                ? result.Manifest?.Version?.Trim()
                : null;
            _config.Save();
            UpdateLastCheckedText = $"Last checked: {_config.LastUpdateCheckUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            ApplyUpdateCheckResult(result, isAutomatic: false);
        }
        catch (Exception ex)
        {
            Logger.LogError("[MainViewModel] Failed to check updates.", ex);
            UpdateStatusTitle = "GitHub update check failed";
            UpdateStatusMessage = ex.Message;
            UpdateAvailableVersionText = "Update check failed";
            UpdateFileName = "GitHub release error";
            UpdateTransferText = "--";
            UpdateSpeedText = "Speed: --";
            suggestSourceSetup = ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase)
                                 || ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase)
                                 || ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            IsUpdateProgressIndeterminate = false;
            IsUpdateOperationInProgress = false;
        }

        if (result == null)
        {
            if (suggestSourceSetup)
            {
                await _dialogService.ShowNotificationAsync("Update check failed", "GitHub Releases is unavailable or the built-in Portal-Windows repository could not be reached. Retry later.");
                return;
            }

            await _dialogService.ShowNotificationAsync("Update check failed", UpdateStatusMessage);
            return;
        }

        if (result.Status != AppUpdateAvailabilityStatus.UpdateAvailable || result.Manifest == null)
        {
            await _dialogService.ShowNotificationAsync("No updates", "Current version is up to date.");
            return;
        }

        var availableVersion = result.Manifest.Version.TrimStart('v', 'V');
        var installNow = await _dialogService.ShowNotificationAsync(
            "Update found",
            $"Version v{availableVersion} is available. Install now?",
            true);

        if (!installNow)
        {
            return;
        }

        ResetUpdateWizardVisualState();
        ShowUpdateWizard = true;
        await InstallUpdateAsync();
    }

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (!CanInstallUpdate || _availableUpdateManifest == null)
        {
            return;
        }

        ShowUpdateWizard = true;

        IsUpdateOperationInProgress = true;
        IsUpdateProgressIndeterminate = true;
        IsUpdateProgressPrimed = false;
        UpdateCurrentStage = AppUpdateStage.Checking;
        InstallUpdateButtonText = "Installing Update...";
        UpdateProgressPercent = 0;
        UpdateStatusTitle = "Installing GitHub update";
        UpdateStatusMessage = "Preparing download from GitHub Releases...";
        HasUpdateBanner = false;

        var progress = new Progress<AppUpdateProgressSnapshot>(snapshot =>
        {
            IsUpdateProgressPrimed = true;
            UpdateCurrentStage = snapshot.Stage;
            IsUpdateProgressIndeterminate = snapshot.Stage != AppUpdateStage.Downloading || !snapshot.TotalBytes.HasValue || snapshot.TotalBytes <= 0;
            UpdateStatusTitle = snapshot.StageText;
            UpdateStatusMessage = snapshot.StatusText;
            UpdateFileName = string.IsNullOrWhiteSpace(snapshot.FileName) ? "Package" : snapshot.FileName;
            UpdateTransferText = snapshot.TotalBytes.HasValue && snapshot.TotalBytes > 0
                ? $"{FormatBytes(snapshot.BytesReceived)} / {FormatBytes(snapshot.TotalBytes.Value)}"
                : $"{FormatBytes(snapshot.BytesReceived)} downloaded";
            UpdateSpeedText = snapshot.BytesPerSecond.HasValue
                ? $"Speed: {FormatBytes((long)snapshot.BytesPerSecond.Value)}/s"
                : "Speed: --";
            UpdateProgressPercent = snapshot.TotalBytes.HasValue && snapshot.TotalBytes.Value > 0
                ? Math.Round(snapshot.BytesReceived * 100d / snapshot.TotalBytes.Value, 1)
                : 0;
        });

        try
        {
            await _updateService.PrepareAndLaunchUpdateAsync(_availableUpdateManifest, _config, progress);
            UpdateStatusTitle = "Installation";
            UpdateStatusMessage = "Portal-Windows is handing off the prepared package to Updater and will close now.";
            UpdateAvailableVersionText = $"Installing version: v{_availableUpdateManifest.Version.TrimStart('v', 'V')}";
            await Task.Delay(600);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Logger.LogError("[MainViewModel] Failed to install update.", ex);
            UpdateStatusTitle = "Update installation failed";
            UpdateStatusMessage = ex.Message;
            HasUpdateBanner = true;
            UpdateBannerText = $"Update failed: {ex.Message}";
            ShowUpdateWizard = true;
        }
        finally
        {
            IsUpdateProgressIndeterminate = false;
            IsUpdateOperationInProgress = false;
            UpdateCurrentStage = AppUpdateStage.Idle;
            InstallUpdateButtonText = "Install Update";
            InstallUpdateCommand.NotifyCanExecuteChanged();
        }
    }

    // --- Commands (Wizard logic) ---

    private void SetShowStep(string stepName)
    {
        StepProgressVis = stepName == "StepProgress";
        StepCredsVis = stepName == "StepCreds";
        StepTransportVis = stepName == "StepTransport";
        StepPairingVis = stepName == "StepPairing";
        StepSuccessVis = stepName == "StepSuccess";
        StepErrorVis = stepName == "StepError";
        StepNameDeviceVis = stepName == "StepNameDevice";
    }

    private void SetShowError(string msg)
    {
        WizErrorText = msg;
        SetShowStep("StepError");
    }

    private int ResetWizardSessionState()
    {
        var sessionId = Interlocked.Increment(ref _wizardSessionId);

        _wizardSetupCts?.Cancel();
        _pairingCts?.Cancel();
        _networkPairing.StopPairing();
        _btPairing?.Stop();

        _credsSignal?.TrySetResult(false);
        _transportSignal?.TrySetResult(false);
        _credsSignal = null;
        _transportSignal = null;
        _pairingContext.ClearSensitiveData();

        return sessionId;
    }

    private bool IsCurrentWizardSession(int sessionId) => sessionId == _wizardSessionId;

    private async Task StartWizardFlow(bool isAddingDevice)
    {
        Logger.Log($"[MainWindow] Starting Wizard Flow. AddingDevice={isAddingDevice}. Configuration: Port={SettingsPort}");

        var sessionId = ResetWizardSessionState();
        _wizardSetupCts = new CancellationTokenSource();
        ShowWizard = true;
        SetShowStep("StepProgress");
        WizStatusText = "Initializing setup...";
        WizIsIndeterminate = true;
        _newlyPairedDevice = null;

        string dllPath = _providerLocator.FindProviderDll(SettingsDllPath) ?? "";
        if (!string.IsNullOrEmpty(dllPath)) SettingsDllPath = dllPath;

        if (!isAddingDevice && (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath)))
        {
            SetShowError("DLL file not found. Please build the CredentialProvider project.");
            return;
        }

        try
        {
            int port = int.TryParse(SettingsPort, out var p) ? p : 29170;
            _config.Port = port;

            if (!isAddingDevice)
            {
                await Task.Run(async () =>
                {
                    Application.Current.Dispatcher.Invoke(() => WizStatusText = "Step 1/4: Verifying Firewall...");
                    var firewallOk = await _firewall.CheckFirewallRule(port, _wizardSetupCts.Token);
                    if (!firewallOk)
                    {
                        Application.Current.Dispatcher.Invoke(() => WizStatusText = "Step 1/4: Configuring Firewall...");
                        await _firewall.AddFirewallRule(port, _wizardSetupCts.Token);
                    }

                    Application.Current.Dispatcher.Invoke(() => WizStatusText = "Step 2/4: Checking Certificate...");
                    if (!_certManager.CheckCertificate())
                    {
                        _hostCert = _certManager.CreateOrLoadCertificate(_config);
                        _config = PortalWinConfig.Load();
                    }
                    else
                    {
                        _hostCert = _certManager.CreateOrLoadCertificate(_config);
                    }

                    var providerHealth = _providerSetup.CheckProviderHealth(dllPath);
                    if (!providerHealth.IsHealthy)
                    {
                        Application.Current.Dispatcher.Invoke(() => WizStatusText = "Step 3/4: Installing Provider...");
                        await _providerSetup.InstallProviderAsync(dllPath, _wizardSetupCts.Token);
                    }
                });
            }
            else
            {
                if (!_certManager.CheckCertificate()) _hostCert = _certManager.CreateOrLoadCertificate(_config);
                else _hostCert = _certManager.CreateOrLoadCertificate(_config);
            }

            if (_wizardSetupCts.IsCancellationRequested || !IsCurrentWizardSession(sessionId)) return;

            WizStatusText = "Checking credentials...";
            LoadAvailableLocalAccounts();
            var currentDomain = Environment.UserDomainName;
            var currentUser = Environment.UserName;
            SelectedLocalAccount = AvailableLocalAccounts.FirstOrDefault(a =>
                string.Equals(a.Username, currentUser, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Domain, currentDomain, StringComparison.OrdinalIgnoreCase))
                ?? AvailableLocalAccounts.FirstOrDefault();

            WizInputUser = SelectedLocalAccount?.Username ?? currentUser;
            WizInputPass = ""; // Legacy placeholder, password is captured from PasswordBox on submit.
            WizInputDomain = SelectedLocalAccount?.Domain ?? currentDomain;
            WizShowDeviceNameEdit = false;
            WizCredsNextText = "Next →";

            var wizardDone = false;
            while (!wizardDone)
            {
                SetShowStep("StepCreds");
                _credsSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var credsConfirmed = await _credsSignal.Task;

                if (_wizardSetupCts.IsCancellationRequested || !IsCurrentWizardSession(sessionId) || !credsConfirmed) return;

                var goBackToCreds = false;
                while (true)
                {
                    SetShowStep("StepTransport");
                    _transportSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var transportConfirmed = await _transportSignal.Task;

                    if (_wizardSetupCts.IsCancellationRequested || !IsCurrentWizardSession(sessionId)) return;
                    if (!transportConfirmed)
                    {
                        goBackToCreds = true;
                        break;
                    }

                    _pairingContext.TargetUsername = WizInputUser;
                    _pairingContext.TargetDomain = WizInputDomain;
                    _pairingContext.SelectedTransport = WizIsNetworkTransport ? Common.TransportType.Network : Common.TransportType.Bluetooth;

                    var pairingResult = await RunPairingLoopAsync();

                    if (_wizardSetupCts.IsCancellationRequested || !IsCurrentWizardSession(sessionId) || pairingResult == PairingStepResult.Cancelled)
                    {
                        return;
                    }

                    if (pairingResult == PairingStepResult.RetryCurrentTransport)
                    {
                        continue;
                    }

                    if (pairingResult == PairingStepResult.BackToTransport)
                    {
                        continue;
                    }

                    wizardDone = true;
                    break;
                }

                if (goBackToCreds)
                {
                    continue;
                }
            }

            if (!IsCurrentWizardSession(sessionId))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                await _networkPairing.StopListener();
            });

            SetShowStep("StepProgress");
            await Task.Run(() =>
            {
                Application.Current.Dispatcher.Invoke(() => WizStatusText = "Finalizing configuration...");
                _config.Save();
            });

            if (!IsCurrentWizardSession(sessionId))
            {
                return;
            }

            if (_newlyPairedDevice != null)
            {
                WizStatusText = "Device connected!";
                WizDeviceName = _newlyPairedDevice.Name;
                SetShowStep("StepNameDevice");
            }
            else
            {
                SetShowStep("StepSuccess");
                await RefreshStatusAsync();
                await Task.Delay(2000);
                if (IsCurrentWizardSession(sessionId))
                {
                    ShowWizard = false;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[MainWindow] Wizard Critical Error", ex);
            SetShowError(ex.Message);
        }
    }

    private async Task<PairingStepResult> RunPairingLoopAsync()
    {
        _pairingBackRequested = false;
        _pairingRefreshRequested = false;
        _newlyPairedDevice = null;
        string code = _networkPairing.GeneratePairingCode();
        _pairingContext.PairingCode = code;
        _pairingStartTime = DateTime.Now;
        var transport = _pairingContext.SelectedTransport;
        var sessionId = Interlocked.Increment(ref _pairingSessionId);
        WizPairInfo = transport == TransportType.Network
            ? "Network pairing service started. Waiting for device..."
            : "Bluetooth pairing service started. Waiting for device...";

        if (code != null) WizPairCode = $"{code.Substring(0, 3)} {code.Substring(3)}";
        WizExpiresInfo = "Code expires in 2:00";
        WizIsExpiresRed = false;

        _expirationTimer?.Stop();
        _expirationTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _expirationTimer.Tick += (s, e) =>
        {
            var remaining = TimeSpan.FromMinutes(2) - (DateTime.Now - _pairingStartTime);
            if (remaining.TotalSeconds <= 0)
            {
                WizExpiresInfo = "Code Expired";
                WizIsExpiresRed = true;
                _expirationTimer.Stop();
            }
            else WizExpiresInfo = $"Code expires in {remaining:m\\:ss}";
        };
        _expirationTimer.Start();

        SetShowStep("StepPairing");

        _pairingCts = new CancellationTokenSource();
        var token = _pairingCts.Token;

        await UpdatePairingDisplayParamsAsync(code ?? "");

        if (transport == Common.TransportType.Bluetooth)
        {
            await Task.Run(async () =>
            {
                try
                {
                    _btPairing?.Dispose();
                    _btPairing = new BluetoothPairingService();

                    await _btPairing.StartAsync(_config, _pairingContext,
                        status => UpdatePairInfoSafe(status, sessionId, TransportType.Bluetooth), token);

                    var result = await _btPairing.WaitForPairingAsync();

                    if (result?.Success == true && result?.Device != null)
                    {
                        _newlyPairedDevice = result.Device;
                        _config = PortalWinConfig.Load();
                    }
                }
                catch (Exception ex) { if (!token.IsCancellationRequested) Logger.LogError("BT Pairing failed", ex); }
                finally { Application.Current.Dispatcher.Invoke(() => _expirationTimer?.Stop()); }
            });
        }
        else
        {
            await Task.Run(async () =>
            {
                try
                {
                    await _networkPairing.StartListener(_config, _hostCert!);
                    var result = await _networkPairing.StartPairing(_pairingContext,
                        status => UpdatePairInfoSafe(status, sessionId, TransportType.Network), token);

                    if (result?.Success == true && result?.Device != null)
                    {
                        _newlyPairedDevice = result.Device;
                        _config = PortalWinConfig.Load();
                    }
                }
                catch (Exception ex) { if (!token.IsCancellationRequested) Logger.LogError("Pairing failed", ex); }
                finally { Application.Current.Dispatcher.Invoke(() => _expirationTimer?.Stop()); }
            });
        }

        if (_newlyPairedDevice != null)
        {
            return PairingStepResult.Paired;
        }

        if (_pairingBackRequested)
        {
            return PairingStepResult.BackToTransport;
        }

        if (_pairingRefreshRequested)
        {
            return PairingStepResult.RetryCurrentTransport;
        }

        return PairingStepResult.Cancelled;
    }

    private void UpdatePairInfoSafe(string status, int sessionId, TransportType expectedTransport)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!StepPairingVis) return;
            if (sessionId != _pairingSessionId) return;
            if (_pairingContext.SelectedTransport != expectedTransport) return;
            WizPairInfo = status;
        });
    }

    private async Task UpdatePairingDisplayParamsAsync(string code)
    {
        int codeInt = int.TryParse(code, out var c) ? c : 0;
        string hostName = Environment.MachineName;

        if (_pairingContext.SelectedTransport == Common.TransportType.Bluetooth)
        {
            WizShowNetworkInfo = false;
            OnPropertyChanged(nameof(WizShowBluetoothInfo));

            var btAddress = await _bluetoothService.GetLocalBluetoothAddressAsync();
            WizBtAddress = btAddress ?? "No BT Adapter";

            var qrData = new
            {
                transport = "bt",
                address = btAddress ?? "",
                serviceUuid = BtProtocol.ServiceUuid.ToString(),
                code = codeInt,
                name = hostName
            };
            var qrPayload = System.Text.Json.JsonSerializer.Serialize(qrData, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
            SetQrCode(qrPayload);
        }
        else
        {
            WizShowNetworkInfo = true;
            OnPropertyChanged(nameof(WizShowBluetoothInfo));

            var ipOptions = await _networkService.GetLocalInterfaceAddressesAsync(_config.VpnCompatibilityModeEnabled);
            AvailableLocalIps.Clear();
            foreach (var option in ipOptions)
            {
                AvailableLocalIps.Add(new NetworkIpOption
                {
                    InterfaceName = option.InterfaceName,
                    IpAddress = option.IpAddress
                });
            }

            if (AvailableLocalIps.Count == 0)
            {
                AvailableLocalIps.Add(new NetworkIpOption
                {
                    InterfaceName = "Unknown",
                    IpAddress = "Unknown"
                });
            }

            var matchedSelection = SelectedPairIp == null
                ? null
                : AvailableLocalIps.FirstOrDefault(x => string.Equals(x.IpAddress, SelectedPairIp.IpAddress, StringComparison.OrdinalIgnoreCase));
            SelectedPairIp = matchedSelection ?? AvailableLocalIps.First();

            WizIpOnly = SelectedPairIp.IpAddress;
            WizPortOnly = SettingsPort;
            RefreshNetworkQrPayload(codeInt, hostName);
        }
    }

    private void RefreshNetworkQrPayload(int? providedCode = null, string? providedHostName = null)
    {
        int codeInt = providedCode ?? (int.TryParse(_pairingContext.PairingCode, out var parsedCode) ? parsedCode : 0);
        string hostName = providedHostName ?? Environment.MachineName;

        var qrData = new
        {
            transport = "net",
            ip = string.IsNullOrWhiteSpace(SelectedPairIp?.IpAddress) ? WizIpOnly : SelectedPairIp!.IpAddress,
            port = int.TryParse(WizPortOnly, out var p) ? p : 29170,
            code = codeInt,
            name = hostName
        };
        var qrPayload = System.Text.Json.JsonSerializer.Serialize(qrData, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        SetQrCode(qrPayload);
    }

    private void LoadAvailableLocalAccounts()
    {
        var machineName = Environment.MachineName;
        var currentDomain = Environment.UserDomainName;
        var known = new Dictionary<string, LocalAccountOption>(StringComparer.OrdinalIgnoreCase);
        var profileUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var profileSearcher = new ManagementObjectSearcher(
                "SELECT LocalPath, Special, Loaded FROM Win32_UserProfile");

            foreach (ManagementObject profile in profileSearcher.Get())
            {
                var special = profile["Special"] as bool?;
                if (special == true) continue;

                var localPath = profile["LocalPath"]?.ToString();
                if (string.IsNullOrWhiteSpace(localPath)) continue;

                var userName = Path.GetFileName(localPath.TrimEnd('\\'));
                if (string.IsNullOrWhiteSpace(userName)) continue;

                // Skip well-known non-interactive profile folders.
                if (string.Equals(userName, "Public", StringComparison.OrdinalIgnoreCase) ||
                    userName.StartsWith("Default", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(userName, "All Users", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                profileUsers.Add(userName);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Host] Failed to enumerate user profiles via WMI: {ex.Message}");
        }

        void AddAccount(string user, string dom)
        {
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(dom)) return;
            if (user.EndsWith("$", StringComparison.Ordinal)) return;

            var key = $"{dom}\\{user}";
            if (!known.ContainsKey(key))
            {
                known[key] = new LocalAccountOption { Username = user.Trim(), Domain = dom.Trim() };
            }
        }

        // Always include the current interactive account.
        AddAccount(Environment.UserName, currentDomain);

        try
        {
            // Query only real local Windows accounts from SAM.
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Disabled, Lockout, LocalAccount FROM Win32_UserAccount WHERE LocalAccount=True");

            foreach (ManagementObject account in searcher.Get())
            {
                var disabled = account["Disabled"] as bool?;
                var locked = account["Lockout"] as bool?;
                if (disabled == true || locked == true)
                    continue;

                var name = account["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                // Only keep accounts that have a real user profile (interactive-capable).
                if (!profileUsers.Contains(name) &&
                    !string.Equals(name, Environment.UserName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Skip well-known service/internal accounts from UI.
                if (string.Equals(name, "WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "DefaultAccount", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Guest", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddAccount(name, machineName);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Host] Failed to enumerate local accounts via WMI: {ex.Message}");
        }

        AvailableLocalAccounts.Clear();
        foreach (var account in known.Values.OrderBy(a => a.DisplayName))
        {
            AvailableLocalAccounts.Add(account);
        }
    }

    private void SetQrCode(string payload)
    {
        var bitmap = _qrCodeService.GenerateQrCode(payload);
        if (bitmap != null)
        {
            WizQrCodeImage = bitmap;
            WizShowQrCode = true;
        }
        else WizShowQrCode = false;
    }

    private bool IsAccountAlreadyPairedForTransport(string username, string domain, TransportType transport, string? excludeClientId = null)
    {
        if (!IsDuplicateAccountProtectionEnabled)
        {
            return false;
        }

        return _config.HasPairedAccountForTransport(username, domain, transport, excludeClientId);
    }

    private bool IsAccountAlreadyPairedOnOtherTransport(string username, string domain, TransportType transport, string? excludeClientId = null)
    {
        if (!IsDuplicateAccountProtectionEnabled || !IsCrossTransportDuplicateProtectionEnabled)
        {
            return false;
        }

        return _config.HasPairedAccountOnOtherTransport(username, domain, transport, excludeClientId);
    }

    [RelayCommand]
    private void CredsNext(object passwordParams)
    {
        SecureString? submittedPassword = null;
        var hasFreshPassword = false;

        if (passwordParams is System.Windows.Controls.PasswordBox pb && pb.SecurePassword != null && pb.SecurePassword.Length > 0)
        {
            submittedPassword = pb.SecurePassword.Copy();
            submittedPassword.MakeReadOnly();
            hasFreshPassword = true;
        }

        try
        {
            if (submittedPassword == null && (!string.IsNullOrEmpty(_editingClientId) || !_pairingContext.HasTargetPassword))
            {
                _dialogService.ShowNotificationAsync("Error", "Please enter password.");
                return;
            }

            if (!string.IsNullOrEmpty(_editingClientId))
            {
                var device = _config.Devices.FirstOrDefault(d => d.ClientId == _editingClientId);
                if (device != null)
                {
                    if (SelectedLocalAccount == null)
                    {
                        _dialogService.ShowNotificationAsync("Error", "Please select account.");
                        return;
                    }

                    if (IsAccountAlreadyPairedForTransport(SelectedLocalAccount.Username, SelectedLocalAccount.Domain, device.TransportType, _editingClientId))
                    {
                        _dialogService.ShowNotificationAsync("Error", "This Windows account is already linked to another device.");
                        return;
                    }
                    if (IsAccountAlreadyPairedOnOtherTransport(SelectedLocalAccount.Username, SelectedLocalAccount.Domain, device.TransportType, _editingClientId))
                    {
                        _dialogService.ShowNotificationAsync("Error", "This account already has pairing on another transport.");
                        return;
                    }

                    var account = device.Accounts.FirstOrDefault();
                    if (account == null)
                    {
                        account = new Portal.Common.Models.DeviceAccount();
                        device.Accounts.Add(account);
                    }

                    if (submittedPassword == null)
                    {
                        _dialogService.ShowNotificationAsync("Error", "Please enter password.");
                        return;
                    }

                    device.Name = WizDeviceName; // Also save updated name
                    WizInputUser = SelectedLocalAccount.Username;
                    WizInputDomain = SelectedLocalAccount.Domain;
                    account.Username = SelectedLocalAccount.Username;
                    account.Domain = SelectedLocalAccount.Domain;
                    account.SetPassword(submittedPassword);
                    _config.Save();
                    RefreshDevicesList();
                    _dialogService.ShowNotificationAsync("Success", "Account updated successfully.");
                }

                _editingClientId = null;
                ShowWizard = false;
                ShowDashboard = false; // Show settings panel again
                if (passwordParams is System.Windows.Controls.PasswordBox passBox) passBox.Password = "";
                return;
            }

            if (SelectedLocalAccount == null)
            {
                _dialogService.ShowNotificationAsync("Error", "Please select account.");
                return;
            }

            WizInputUser = SelectedLocalAccount.Username;
            WizInputDomain = SelectedLocalAccount.Domain;

            if (hasFreshPassword && submittedPassword != null)
            {
                _pairingContext.SetTargetPassword(submittedPassword);
            }

            _credsSignal?.TrySetResult(true);
        }
        finally
        {
            submittedPassword?.Dispose();
        }
    }

    [RelayCommand]
    private void TransportNext()
    {
        if (SelectedLocalAccount == null)
        {
            _dialogService.ShowNotificationAsync("Error", "Please select account.");
            return;
        }

        var selectedTransport = WizIsNetworkTransport ? TransportType.Network : TransportType.Bluetooth;
        if (IsAccountAlreadyPairedForTransport(SelectedLocalAccount.Username, SelectedLocalAccount.Domain, selectedTransport))
        {
            var transportLabel = selectedTransport == TransportType.Network ? "Network" : "Bluetooth";
            _dialogService.ShowNotificationAsync("Error", $"This account already has a paired device for {transportLabel} transport.");
            return;
        }
        if (IsAccountAlreadyPairedOnOtherTransport(SelectedLocalAccount.Username, SelectedLocalAccount.Domain, selectedTransport))
        {
            _dialogService.ShowNotificationAsync("Error", "This account already has pairing on the other transport.");
            return;
        }

        _transportSignal?.TrySetResult(true);
    }

    [RelayCommand]
    private async Task RefreshCodeAsync()
    {
        _pairingRefreshRequested = true;
        _pairingBackRequested = false;
        _pairingCts?.Cancel();
        _networkPairing.StopPairing();
        _btPairing?.Stop();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void SkipPairing()
    {
        _pairingCts?.Cancel();
        _networkPairing.StopPairing();
        _btPairing?.Stop();
    }

    [RelayCommand]
    private void CloseWizard()
    {
        ResetWizardSessionState();
        _pairingBackRequested = false;
        _pairingRefreshRequested = false;

        ShowWizard = false;
        _ = RefreshStatusAsync();
    }

    [RelayCommand]
    private async Task NameDeviceNextAsync()
    {
        var sessionId = _wizardSessionId;

        if (_newlyPairedDevice != null && !string.IsNullOrWhiteSpace(WizDeviceName))
        {
            var device = _config.Devices.FirstOrDefault(c => c.ClientId == _newlyPairedDevice.ClientId);
            if (device != null)
            {
                device.Name = WizDeviceName.Trim();
                _config.Save();
            }
            await RefreshStatusAsync();
        }

        if (!IsCurrentWizardSession(sessionId))
        {
            return;
        }

        SetShowStep("StepSuccess");
        await Task.Delay(2000);
        if (IsCurrentWizardSession(sessionId))
        {
            ShowWizard = false;
        }
    }

    [RelayCommand]
    private void StepCredsBack()
    {
        // Go back to previous view entirely if canceling credentials initial step
        CloseWizard();
    }

    [RelayCommand]
    private void StepTransportBack()
    {
        _transportSignal?.TrySetResult(false);
        SetShowStep("StepCreds");
    }

    [RelayCommand]
    private void StepPairingBack()
    {
        _pairingBackRequested = true;
        _pairingRefreshRequested = false;
        _pairingCts?.Cancel();
        _networkPairing.StopPairing();
        _btPairing?.Stop();

        SetShowStep("StepTransport");
    }

    [RelayCommand]
    private async Task OpenFaq()
    {
        if (!AreExperimentalFeaturesEnabled)
        {
            return;
        }

        ShowFaq = true;
        await ReloadFaqAsync(showBusyState: false);
    }

    [RelayCommand]
    private void CloseFaq()
    {
        ShowFaq = false;
        FaqSearchText = "";
    }

    [RelayCommand]
    private async Task RefreshFaqAsync()
    {
        if (!AreExperimentalFeaturesEnabled)
        {
            return;
        }

        await ReloadFaqAsync(showBusyState: true);
    }

    private async Task ReloadFaqAsync(bool showBusyState)
    {
        if (IsRefreshingFaq)
        {
            return;
        }

        IsRefreshingFaq = true;
        FaqRefreshButtonText = "Updating...";

        try
        {
            if (showBusyState)
            {
                FaqLastUpdatedText = "Reloading local FAQ source...";
            }

            var document = await _faqContentService.RefreshAsync();
            FaqArticles.Clear();
            foreach (var article in document.Articles.OrderBy(article => article.Category).ThenBy(article => article.Title))
            {
                FaqArticles.Add(article);
            }

            FaqCategories.Clear();
            FaqCategories.Add("All");
            foreach (var category in document.Articles
                         .Select(article => article.Category)
                         .Where(category => !string.IsNullOrWhiteSpace(category))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(category => category))
            {
                FaqCategories.Add(category);
            }

            if (!FaqCategories.Contains(SelectedFaqCategory))
            {
                SelectedFaqCategory = "All";
            }

            FaqSourceText = $"Source: {document.Source}";
            FaqLastUpdatedText = $"Last updated: {document.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            UpdateFaqFilter();
        }
        catch (Exception ex)
        {
            Logger.LogError("FAQ Load Error", ex);
            FaqLastUpdatedText = "Failed to reload local FAQ source.";
            FaqSourceText = "Source: local file";
            await _dialogService.ShowNotificationAsync("FAQ update failed", ex.Message);
        }
        finally
        {
            IsRefreshingFaq = false;
            FaqRefreshButtonText = "Update Wiki";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var suffixIndex = 0;
        while (value >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            value /= 1024;
            suffixIndex++;
        }

        return $"{value:0.##} {suffixes[suffixIndex]}";
    }

    private static string? NormalizeRepositoryInput(string value)
    {
        var normalized = value.Trim().Trim('"');
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase)))
        {
            normalized = uri.AbsolutePath.Trim('/');
        }

        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        return $"{parts[0]}/{parts[1]}";
    }

    private static bool SecureStringsEqual(SecureString left, SecureString right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        IntPtr leftPtr = IntPtr.Zero;
        IntPtr rightPtr = IntPtr.Zero;
        char[]? leftChars = null;
        char[]? rightChars = null;

        try
        {
            leftPtr = System.Runtime.InteropServices.Marshal.SecureStringToGlobalAllocUnicode(left);
            rightPtr = System.Runtime.InteropServices.Marshal.SecureStringToGlobalAllocUnicode(right);

            leftChars = new char[left.Length];
            rightChars = new char[right.Length];
            System.Runtime.InteropServices.Marshal.Copy(leftPtr, leftChars, 0, leftChars.Length);
            System.Runtime.InteropServices.Marshal.Copy(rightPtr, rightChars, 0, rightChars.Length);

            var areEqual = true;
            for (var i = 0; i < leftChars.Length; i++)
            {
                areEqual &= leftChars[i] == rightChars[i];
            }

            return areEqual;
        }
        finally
        {
            if (leftChars != null)
            {
                Array.Clear(leftChars, 0, leftChars.Length);
            }

            if (rightChars != null)
            {
                Array.Clear(rightChars, 0, rightChars.Length);
            }

            if (leftPtr != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.ZeroFreeGlobalAllocUnicode(leftPtr);
            }

            if (rightPtr != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.ZeroFreeGlobalAllocUnicode(rightPtr);
            }
        }
    }

    // --- Clipboard Commands ---
    [RelayCommand] private void CopyIp() => CopyText(WizIpOnly);
    [RelayCommand] private void CopyPort() => CopyText(WizPortOnly);
    [RelayCommand] private void CopyCode() => CopyText(WizPairCode.Replace(" ", ""));
    [RelayCommand] private void CopyBtAddress() => CopyText(WizBtAddress);

    private void CopyText(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }

    public void OnWindowClosing()
    {
        _pairingCts?.Cancel();
        _networkPairing.StopPairing();
        _btPairing?.Stop();
        _ = _networkPairing.StopListener();
    }
}
