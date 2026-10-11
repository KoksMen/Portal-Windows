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
    /// Thread-safe and supports switching log targets (e.g. from default provider.log to host.log).
    /// </summary>
    public static void Initialize(string logFileName)
    {
        lock (_lock)
        {
            if (_isInitialized && string.Equals(_logFileName, logFileName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_isInitializing) return;
            _isInitializing = true;

            try
            {
                if (_isInitialized)
                {
                    try { Serilog.Log.CloseAndFlush(); } catch { }
                    _isInitialized = false;
                }

                _logFileName = logFileName;
                var logsDir = PortalStoragePaths.LogsDirectory;
                Directory.CreateDirectory(logsDir);
                var logPath = Path.Combine(logsDir, _logFileName);

                var fileLogger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.File(
                        logPath,
                        rollingInterval: RollingInterval.Day,
                        rollOnFileSizeLimit: true,
                        fileSizeLimitBytes: 10 * 1024 * 1024, // 10 MB per log segment
                        retainedFileCountLimit: 7,            // Retain up to 7 daily log files
                        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                        shared: true)
                    .CreateLogger();

                var ringBufferSink = new Helpers.ChannelRingBufferSink(fileLogger, boundedCapacity: 10_000);

                Serilog.Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.Console(
                        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .WriteTo.Sink(ringBufferSink)
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

    /// <summary>
    /// Prunes log files older than maxAge or when total logs directory size exceeds maxTotalBytes.
    /// Excludes activity.journal.jsonl.
    /// </summary>
    public static void PruneOldLogs(TimeSpan? maxAge = null, long maxTotalBytes = 50 * 1024 * 1024)
    {
        try
        {
            var logsDir = PortalStoragePaths.LogsDirectory;
            if (!Directory.Exists(logsDir)) return;

            var thresholdDate = DateTime.UtcNow - (maxAge ?? TimeSpan.FromDays(7));
            var logFiles = Directory.GetFiles(logsDir, "*.log*")
                .Where(f => !f.EndsWith("activity.journal.jsonl", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .Where(fi => fi.Exists)
                .OrderBy(fi => fi.LastWriteTimeUtc)
                .ToList();

            // 1. Delete files older than retention period
            foreach (var fi in logFiles.ToList())
            {
                if (fi.LastWriteTimeUtc < thresholdDate)
                {
                    try
                    {
                        fi.Delete();
                        logFiles.Remove(fi);
                    }
                    catch { }
                }
            }

            // 2. If total size still exceeds maxTotalBytes, prune oldest files
            long totalBytes = logFiles.Sum(f => f.Length);
            while (totalBytes > maxTotalBytes && logFiles.Count > 1)
            {
                var oldest = logFiles[0];
                try
                {
                    totalBytes -= oldest.Length;
                    oldest.Delete();
                    logFiles.RemoveAt(0);
                }
                catch
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Logger] PruneOldLogs error: {ex.Message}");
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
