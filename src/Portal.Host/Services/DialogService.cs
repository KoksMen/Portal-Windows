using System.Threading.Tasks;

namespace Portal.Host.Services;

public interface IDialogService
{
    Task<bool> ShowNotificationAsync(string title, string message, bool isQuestion = false);
}

public class DialogService : IDialogService
{
    private MainWindow? _mainWindow;

    public void SetMainWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
    }

    public Task<bool> ShowNotificationAsync(string title, string message, bool isQuestion = false)
    {
        if (_mainWindow != null)
        {
            return _mainWindow.ShowNotification(title, message, isQuestion);
        }
        return Task.FromResult(false);
    }
}
