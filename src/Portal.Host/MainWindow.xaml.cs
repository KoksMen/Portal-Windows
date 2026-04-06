using System;
using System.ComponentModel;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Portal.Host.Models;
using Portal.Host.Services;
using Portal.Host.ViewModels;

namespace Portal.Host;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IDialogService _dialogService;
    private TaskCompletionSource<bool>? _notificationTcs;
    private UpdateToastWindow? _updateToastWindow;
    private LogsWindow? _logsWindow;

    public MainWindow(MainViewModel viewModel, IDialogService dialogService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _dialogService = dialogService;

        if (_dialogService is DialogService concreteDialogService)
        {
            concreteDialogService.SetMainWindow(this);
        }

        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.OpenLogsWindowRequested += OnOpenLogsWindowRequested;

        Loaded += MainWindow_Loaded;
        StateChanged += (_, _) => UpdateWindowToggleGlyph();
        UpdateWindowToggleGlyph();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();

        if (Application.Current is App app && !string.IsNullOrWhiteSpace(app.StartupBackupFilePath))
        {
            _viewModel.PrimeRestoreBackupFile(app.StartupBackupFilePath);
        }
    }

    public void ActivateFromExternalLaunch(string? backupFilePath)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ActivateFromExternalLaunch(backupFilePath));
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        if (!string.IsNullOrWhiteSpace(backupFilePath))
        {
            _viewModel.PrimeRestoreBackupFile(backupFilePath);
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.OpenLogsWindowRequested -= OnOpenLogsWindowRequested;
        CloseUpdateToastWindow();
        CloseLogsWindow();
        _viewModel.OnWindowClosing();
    }

    private void OnOpenLogsWindowRequested()
    {
        Dispatcher.Invoke(() =>
        {
            if (_logsWindow == null || !_logsWindow.IsLoaded)
            {
                _logsWindow = App.Current.Services.GetRequiredService<LogsWindow>();
                _logsWindow.Owner = this;
                _logsWindow.Closed += (_, _) => _logsWindow = null;
                _logsWindow.Show();
            }
            else
            {
                if (_logsWindow.WindowState == WindowState.Minimized)
                {
                    _logsWindow.WindowState = WindowState.Normal;
                }

                _logsWindow.Activate();
                _logsWindow.Focus();
            }
        });
    }

    private void CloseLogsWindow()
    {
        if (_logsWindow == null)
        {
            return;
        }

        _logsWindow.Close();
        _logsWindow = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(MainViewModel.ShowUpdateToast), StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            if (_viewModel.ShowUpdateToast)
            {
                ShowUpdateToastWindow();
            }
            else
            {
                CloseUpdateToastWindow();
            }
        });
    }

    private void ShowUpdateToastWindow()
    {
        if (_updateToastWindow == null)
        {
            _updateToastWindow = new UpdateToastWindow
            {
                DataContext = _viewModel
            };
            _updateToastWindow.Closed += (_, _) => _updateToastWindow = null;
        }

        if (!_updateToastWindow.IsVisible)
        {
            _updateToastWindow.Show();
        }

        _updateToastWindow.UpdateLayout();
        PositionUpdateToastWindow();
    }

    private void PositionUpdateToastWindow()
    {
        if (_updateToastWindow == null)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        const double offset = 14d;
        var toastWidth = _updateToastWindow.ActualWidth > 0 ? _updateToastWindow.ActualWidth : _updateToastWindow.Width;
        var toastHeight = _updateToastWindow.ActualHeight > 0 ? _updateToastWindow.ActualHeight : _updateToastWindow.Height;

        _updateToastWindow.Left = workArea.Right - toastWidth - offset;
        _updateToastWindow.Top = workArea.Bottom - toastHeight - offset;
    }

    private void CloseUpdateToastWindow()
    {
        if (_updateToastWindow == null)
        {
            return;
        }

        _updateToastWindow.Close();
        _updateToastWindow = null;
    }

    // Custom notification UI logic - same as client
    public Task<bool> ShowNotification(string title, string message, bool isQuestion = false)
    {
        NotificationOverlay.Visibility = Visibility.Visible;
        NotifyTitle.Text = title;
        NotifyMessage.Text = message;

        if (isQuestion)
        {
            NotifyActions.Visibility = Visibility.Visible;
            BtnNotifyOk.Visibility = Visibility.Collapsed;
        }
        else
        {
            NotifyActions.Visibility = Visibility.Collapsed;
            BtnNotifyOk.Visibility = Visibility.Visible;
        }

        _notificationTcs = new TaskCompletionSource<bool>();
        return _notificationTcs.Task;
    }

    private void OnNotifyYes(object sender, RoutedEventArgs e)
    {
        NotificationOverlay.Visibility = Visibility.Collapsed;
        _notificationTcs?.TrySetResult(true);
    }

    private void OnNotifyNo(object sender, RoutedEventArgs e)
    {
        NotificationOverlay.Visibility = Visibility.Collapsed;
        _notificationTcs?.TrySetResult(false);
    }

    private void AvatarImage_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateAvatarClip(sender as Image);
    }

    private void AvatarImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateAvatarClip(sender as Image);
    }

    private static void UpdateAvatarClip(Image? image)
    {
        if (image == null || image.ActualWidth <= 0 || image.ActualHeight <= 0)
        {
            return;
        }

        const double cornerRadius = 13d;
        image.Clip = new RectangleGeometry(new Rect(0, 0, image.ActualWidth, image.ActualHeight), cornerRadius, cornerRadius);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void ToggleMaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateWindowToggleGlyph();
    }

    private void UpdateWindowToggleGlyph()
    {
        if (WindowToggleOutline == null || WindowToggleOutlineBack == null)
        {
            return;
        }

        WindowToggleOutlineBack.Visibility = WindowState == WindowState.Maximized
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnNotifyOk(object sender, RoutedEventArgs e)
    {
        NotificationOverlay.Visibility = Visibility.Collapsed;
        _notificationTcs?.TrySetResult(true);
    }

    private void InputAccount_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is ComboBox cb && cb.SelectedItem is LocalAccountOption selected)
        {
            vm.SelectedLocalAccount = selected;
        }
    }

    private void InputPairIp_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is ComboBox cb && cb.SelectedItem is NetworkIpOption selected)
        {
            vm.SelectedPairIp = selected;
        }
    }

    private async void BtnCopyIp_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not Button btn) return;
        if (vm.CopyIpCommand.CanExecute(null))
        {
            vm.CopyIpCommand.Execute(null);
            await ShowCopiedFeedbackAsync(btn);
        }
    }

    private async void BtnCopyPort_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not Button btn) return;
        if (vm.CopyPortCommand.CanExecute(null))
        {
            vm.CopyPortCommand.Execute(null);
            await ShowCopiedFeedbackAsync(btn);
        }
    }

    private async void BtnCopyBt_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not Button btn) return;
        if (vm.CopyBtAddressCommand.CanExecute(null))
        {
            vm.CopyBtAddressCommand.Execute(null);
            await ShowCopiedFeedbackAsync(btn);
        }
    }

    private void BtnCreateBackup_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        SecureString? password = null;
        SecureString? confirm = null;
        try
        {
            password = BackupPasswordBox.SecurePassword.Copy();
            password.MakeReadOnly();
            confirm = BackupPasswordConfirmBox.SecurePassword.Copy();
            confirm.MakeReadOnly();

            var payload = new BackupPasswordPayload(password, confirm);
            password = null;
            confirm = null;

            if (vm.CreateEncryptedBackupCommand.CanExecute(payload))
            {
                vm.CreateEncryptedBackupCommand.Execute(payload);
            }
            else
            {
                payload.Dispose();
            }
        }
        finally
        {
            password?.Dispose();
            confirm?.Dispose();
            BackupPasswordBox.Clear();
            BackupPasswordConfirmBox.Clear();
        }
    }

    private void BtnRestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        SecureString? password = null;
        try
        {
            password = RestoreBackupPasswordBox.SecurePassword.Copy();
            password.MakeReadOnly();

            var payload = vm.CreateRestorePayload(password);
            password = null;

            if (payload == null)
            {
                _ = _dialogService.ShowNotificationAsync("Restore error", "Select backup file and enter password first.");
                return;
            }

            if (vm.RestoreEncryptedBackupCommand.CanExecute(payload))
            {
                vm.RestoreEncryptedBackupCommand.Execute(payload);
            }
            else
            {
                payload.Dispose();
            }
        }
        finally
        {
            password?.Dispose();
            RestoreBackupPasswordBox.Clear();
        }
    }

    private static async Task ShowCopiedFeedbackAsync(Button button)
    {
        var oldContent = button.Content;
        button.Content = "✅";
        await Task.Delay(900);
        if (button.Dispatcher.CheckAccess())
        {
            button.Content = oldContent;
        }
        else
        {
            button.Dispatcher.Invoke(() => button.Content = oldContent, DispatcherPriority.Background);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && DataContext is MainViewModel tabVm && tabVm.ShowWizard)
        {
            var reverse = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (tabVm.StepTransportVis)
            {
                ToggleTransportSelection(tabVm, reverse);
                e.Handled = true;
                return;
            }

            if (tabVm.StepCredsVis && !tabVm.WizShowDeviceNameEdit)
            {
                CycleSelectedAccount(tabVm, reverse);
                e.Handled = true;
                return;
            }
        }

        if (e.Key is not (Key.Escape or Key.Enter or Key.Space))
        {
            return;
        }

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (IsInputControl(e.OriginalSource) && e.Key == Key.Space)
        {
            return;
        }

        if (NotificationOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                if (NotifyActions.Visibility == Visibility.Visible)
                {
                    OnNotifyNo(this, new RoutedEventArgs());
                }
                else
                {
                    OnNotifyOk(this, new RoutedEventArgs());
                }

                e.Handled = true;
                return;
            }

            if (e.Key is Key.Enter or Key.Space)
            {
                if (NotifyActions.Visibility == Visibility.Visible)
                {
                    OnNotifyYes(this, new RoutedEventArgs());
                }
                else
                {
                    OnNotifyOk(this, new RoutedEventArgs());
                }

                e.Handled = true;
                return;
            }
        }

        if (vm.IsBusyOverlayVisible)
        {
            if (e.Key == Key.Escape)
            {
                if (vm.CancelBusyOperationCommand.CanExecute(null))
                {
                    vm.CancelBusyOperationCommand.Execute(null);
                }
                else if (vm.AcknowledgeBusyResultCommand.CanExecute(null))
                {
                    vm.AcknowledgeBusyResultCommand.Execute(null);
                }

                e.Handled = true;
                return;
            }

            if (e.Key is Key.Enter or Key.Space)
            {
                if (vm.AcknowledgeBusyResultCommand.CanExecute(null))
                {
                    vm.AcknowledgeBusyResultCommand.Execute(null);
                    e.Handled = true;
                }

                return;
            }
        }

        if (vm.IsFaqOverlayVisible && e.Key == Key.Escape)
        {
            if (vm.CloseFaqCommand.CanExecute(null))
            {
                vm.CloseFaqCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (vm.ShowAboutDialog && e.Key == Key.Escape)
        {
            if (vm.CloseAboutCommand.CanExecute(null))
            {
                vm.CloseAboutCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (vm.ShowUpdateWizard && e.Key == Key.Escape)
        {
            if (vm.CloseUpdateWizardCommand.CanExecute(null))
            {
                vm.CloseUpdateWizardCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (vm.ShowWizard)
        {
            if (e.Key == Key.Escape)
            {
                HandleWizardEscape(vm);
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Enter or Key.Space)
            {
                HandleWizardConfirm(vm);
                e.Handled = true;
                return;
            }
        }

        if (vm.ShowSettingsPanel && e.Key == Key.Escape)
        {
            if (vm.CloseSettingsCommand.CanExecute(null))
            {
                vm.CloseSettingsCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    private static bool IsInputControl(object? source)
    {
        return source is TextBox
            or PasswordBox
            or ComboBox
            or ComboBoxItem
            or ListBoxItem
            or ToggleButton
            or CheckBox
            or RadioButton
            or Button;
    }

    private void HandleWizardEscape(MainViewModel vm)
    {
        if (vm.StepPairingVis)
        {
            if (vm.StepPairingBackCommand.CanExecute(null))
            {
                vm.StepPairingBackCommand.Execute(null);
            }

            return;
        }

        if (vm.StepTransportVis)
        {
            if (vm.StepTransportBackCommand.CanExecute(null))
            {
                vm.StepTransportBackCommand.Execute(null);
            }

            return;
        }

        if (vm.StepCredsVis)
        {
            if (vm.WizShowDeviceNameEdit)
            {
                if (vm.CancelEditCommand.CanExecute(null))
                {
                    vm.CancelEditCommand.Execute(null);
                }
            }
            else if (vm.StepCredsBackCommand.CanExecute(null))
            {
                vm.StepCredsBackCommand.Execute(null);
            }

            return;
        }

        if (vm.CloseWizardCommand.CanExecute(null))
        {
            vm.CloseWizardCommand.Execute(null);
        }
    }

    private void HandleWizardConfirm(MainViewModel vm)
    {
        if (vm.StepCredsVis)
        {
            if (vm.CredsNextCommand.CanExecute(InputPass))
            {
                vm.CredsNextCommand.Execute(InputPass);
            }

            return;
        }

        if (vm.StepTransportVis)
        {
            if (vm.TransportNextCommand.CanExecute(null))
            {
                vm.TransportNextCommand.Execute(null);
            }

            return;
        }

        if (vm.StepNameDeviceVis)
        {
            if (vm.NameDeviceNextCommand.CanExecute(null))
            {
                vm.NameDeviceNextCommand.Execute(null);
            }

            return;
        }

        if ((vm.StepSuccessVis || vm.StepErrorVis) && vm.CloseWizardCommand.CanExecute(null))
        {
            vm.CloseWizardCommand.Execute(null);
        }
    }

    private static void ToggleTransportSelection(MainViewModel vm, bool reverse)
    {
        // Two-state toggle: Tab (or Shift+Tab) switches between Network and Bluetooth.
        _ = reverse;
        if (vm.IsTransTcp)
        {
            vm.IsTransBt = true;
        }
        else
        {
            vm.IsTransTcp = true;
        }
    }

    private static void CycleSelectedAccount(MainViewModel vm, bool reverse)
    {
        if (vm.AvailableLocalAccounts.Count == 0)
        {
            return;
        }

        var currentIndex = vm.SelectedLocalAccount == null
            ? -1
            : vm.AvailableLocalAccounts.IndexOf(vm.SelectedLocalAccount);

        if (currentIndex < 0)
        {
            vm.SelectedLocalAccount = vm.AvailableLocalAccounts[0];
            return;
        }

        var nextIndex = reverse
            ? (currentIndex - 1 + vm.AvailableLocalAccounts.Count) % vm.AvailableLocalAccounts.Count
            : (currentIndex + 1) % vm.AvailableLocalAccounts.Count;

        vm.SelectedLocalAccount = vm.AvailableLocalAccounts[nextIndex];
    }
}
