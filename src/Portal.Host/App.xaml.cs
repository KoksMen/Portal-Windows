using System.Windows;
using System.Security.Principal;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Portal.Common;
using Portal.Host.Services;

namespace Portal.Host;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\Portal.Host.SingleInstance";
    private const string ActivationPipeName = "Portal.Host.ActivationPipe";

    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _pipeCts;
    private Task? _pipeServerTask;

    public new static App Current => (App)Application.Current;
    public IServiceProvider Services { get; }
    public string? StartupBackupFilePath { get; private set; }

    public App()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // ViewModels & Views
        services.AddTransient<MainWindow>();
        services.AddTransient<ViewModels.MainViewModel>();
        services.AddTransient<LogsWindow>();
        services.AddTransient<ViewModels.LogsWindowViewModel>();

        // Services
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<NetworkService>();
        services.AddSingleton<QrCodeService>();
        services.AddSingleton<ProviderLocatorService>();
        services.AddSingleton<BluetoothService>();
        services.AddSingleton<FirewallService>();
        services.AddSingleton<ProviderSetupService>();
        services.AddSingleton<CertificateManager>();
        services.AddSingleton<NetworkPairingService>();
        services.AddSingleton<FaqContentService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<EncryptedBackupService>();
        services.AddSingleton<BackupFileAssociationService>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!AcquireSingleInstance())
        {
            SendActivationToRunningInstance(e.Args);
            Shutdown(0);
            return;
        }

        Logger.Initialize("host.log");

        if (!IsRunningAsAdministrator())
        {
            const string message = "Portal Host must be started with Administrator privileges.";
            Logger.LogError("[Host] Startup aborted: administrator privileges are required.");
            MessageBox.Show(message, "Portal Host", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        base.OnStartup(e);
        Services.GetRequiredService<BackupFileAssociationService>()
            .EnsureAssociation(Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location);

        if (e.Args.Length > 0)
        {
            StartupBackupFilePath = TryGetBackupPath(e.Args[0]);
        }

        StartActivationPipeServer();

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity == null)
            {
                return false;
            }

            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _pipeCts?.Cancel();
            _pipeServerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }
        finally
        {
            _pipeCts?.Dispose();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }

        base.OnExit(e);
    }

    private bool AcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
            return createdNew;
        }
        catch
        {
            return false;
        }
    }

    private static void SendActivationToRunningInstance(string[] args)
    {
        try
        {
            var payload = (args.Length > 0 ? args[0] : string.Empty) ?? string.Empty;
            using var client = new NamedPipeClientStream(".", ActivationPipeName, PipeDirection.Out);
            client.Connect(1200);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(payload);
            writer.Flush();
        }
        catch
        {
            // Best-effort signal to existing instance.
        }
    }

    private void StartActivationPipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        _pipeServerTask = Task.Run(() => RunActivationPipeServerAsync(_pipeCts.Token));
    }

    private async Task RunActivationPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    ActivationPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var payload = await reader.ReadToEndAsync();
                var backupPath = TryGetBackupPath(payload);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (MainWindow is MainWindow mainWindow)
                    {
                        mainWindow.ActivateFromExternalLaunch(backupPath);
                    }
                    else
                    {
                        StartupBackupFilePath = backupPath;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(250, ct);
            }
        }
    }

    private static string? TryGetBackupPath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var path = candidate.Trim().Trim('"');
        if (!File.Exists(path))
        {
            return null;
        }

        return path.EndsWith(EncryptedBackupService.BackupFileExtension, StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }
}
