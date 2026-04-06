using System;
using System.IO;
using Serilog;
using Serilog.Events;
using Portal.Common.Abstractions;

namespace Portal.Common;

public static class Logger
{
    private static string _logFileName = "provider.log";
    private static bool _isInitialized = false;
    private static readonly object _lock = new();

    /// <summary>
    /// Initialize the logger with a specific log file name.
    /// Call this once at application startup.
    /// </summary>
    public static void Initialize(string logFileName)
    {
        lock (_lock)
        {
            if (_isInitialized) return;

            _logFileName = logFileName;
            var logPath = Path.Combine(PortalStoragePaths.LogsDirectory, _logFileName);

            Directory.CreateDirectory(PortalStoragePaths.LogsDirectory);

            Serilog.Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                    shared: true)
                .CreateLogger();

            _isInitialized = true;
        }
    }

    public static void Log(string message)
    {
        EnsureInitialized();
        Serilog.Log.Information(message);
    }

    public static void LogWarning(string message)
    {
        EnsureInitialized();
        Serilog.Log.Warning(message);
    }

    public static void LogError(string message, Exception? ex = null)
    {
        EnsureInitialized();
        if (ex != null)
            Serilog.Log.Error(ex, message);
        else
            Serilog.Log.Error(message);
    }

    private static void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            Initialize("provider.log");
        }
    }
}
