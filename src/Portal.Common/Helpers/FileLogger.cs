using Portal.Common.Abstractions;

namespace Portal.Common;

/// <summary>
/// Non-static implementation of ILogger that delegates to the static Logger.
/// Used for dependency injection while maintaining backward compatibility.
/// </summary>
public class FileLogger : Abstractions.ILogger
{
    private readonly string _logFileName;

    public FileLogger(string logFileName = "provider.log")
    {
        _logFileName = logFileName;
        Logger.Initialize(_logFileName);
    }

    public void Log(string message) => Logger.Log(message);
    public void LogWarning(string message) => Logger.LogWarning(message);
    public void LogError(string message, Exception? ex = null) => Logger.LogError(message, ex);
}
