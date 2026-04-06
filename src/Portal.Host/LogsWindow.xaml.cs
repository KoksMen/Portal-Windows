using System;
using System.ComponentModel;
using System.Windows;
using Portal.Host.ViewModels;

namespace Portal.Host;

public partial class LogsWindow : Window
{
    private readonly LogsWindowViewModel _viewModel;

    public LogsWindow(LogsWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.CloseRequested += OnCloseRequested;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Start();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _viewModel.Stop();
        _viewModel.CloseRequested -= OnCloseRequested;
        Loaded -= OnLoaded;
        Closing -= OnClosing;
    }

    private void OnCloseRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(Close);
            return;
        }

        Close();
    }
}
