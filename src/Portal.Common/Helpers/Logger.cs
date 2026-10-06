using System;
using System.IO;
using Serilog;
using Serilog.Events;
using Portal.Common.Abstractions;

namespace Portal.Common;

public static class Logger
{
    private static string _logFileName = "provider.log";
    private static volatile bool _isInitialized = false;
    private static bool _isInitializing = false;
    private static readonly object _lock = new();

    public static bool IsInitialized => _isInitialized;

    /// <summary>
    /// Initialize the logger with a specific log file name.
    /// Call this once at application startup.
    /// </summary>
    public static void Initialize(string logFileName)
    {
        lock (_lock)
        {
            if (_isInitialized || _isInitializing) return;
            _isInitializing = true;

            try
            {
                _logFileName = logFileName;
                var logsDir = PortalStoragePaths.LogsDirectory;
                Directory.CreateDirectory(logsDir);
                var logPath = Path.Combine(logsDir, _logFileName);

                Serilog.Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.Console(
                        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .WriteTo.File(
                        logPath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7,
                        fileSizeLimitBytes: null,
                        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                        shared: true)
                    .CreateLogger();

                _isInitialized = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Logger] Initialization failed: {ex}");
            }
            finally
            {
                _isInitializing = false;
            }
        }
    }

    public static void Log(string message)
    {
        if (_isInitializing)
        {
            System.Diagnostics.Debug.WriteLine(message);
            return;
        }

        EnsureInitialized();
        if (_isInitialized)
        {
            Serilog.Log.Information(message);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine(message);
        }
    }

    public static void LogWarning(string message)
    {
        if (_isInitializing)
        {
            System.Diagnostics.Debug.WriteLine(message);
            return;
        }

        EnsureInitialized();
        if (_isInitialized)
        {
            Serilog.Log.Warning(message);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine(message);
        }
    }

    public static void LogError(string message, Exception? ex = null)
    {
        if (_isInitializing)
        {
            System.Diagnostics.Debug.WriteLine(message);
            return;
        }

        EnsureInitialized();
        if (_isInitialized)
        {
            if (ex != null)
                Serilog.Log.Error(ex, message);
            else
                Serilog.Log.Error(message);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine(message);
        }
    }

    private static void EnsureInitialized()
    {
        if (!_isInitialized && !_isInitializing)
        {
            Initialize("provider.log");
        }
    }
}
