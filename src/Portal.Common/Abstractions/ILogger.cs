namespace Portal.Common.Abstractions;

/// <summary>
/// Abstraction for application logging.
/// </summary>
public interface ILogger
{
    void Log(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? ex = null);
}
