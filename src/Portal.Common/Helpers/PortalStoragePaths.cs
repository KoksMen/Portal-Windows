using System;
using System.IO;

namespace Portal.Common;

public static class PortalStoragePaths
{
    private const string CurrentRootFolderName = "Portal-Windows";
    private static readonly object Sync = new();
    private static bool _initialized;

    public static string RootDirectory
    {
        get
        {
            EnsureInitialized();
            return Path.Combine(GetCommonApplicationData(), CurrentRootFolderName);
        }
    }

    public static string LogsDirectory
    {
        get
        {
            EnsureInitialized();
            var logsDirectory = Path.Combine(RootDirectory, "Logs");
            Directory.CreateDirectory(logsDirectory);
            return logsDirectory;
        }
    }

    public static string DefaultCertificatePath => Path.Combine(RootDirectory, "host_cert.pfx");

    private static string GetCommonApplicationData() =>
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            var newRootDirectory = Path.Combine(GetCommonApplicationData(), CurrentRootFolderName);
            var newLogsDirectory = Path.Combine(newRootDirectory, "Logs");

            Directory.CreateDirectory(newRootDirectory);
            Directory.CreateDirectory(newLogsDirectory);

            _initialized = true;
        }
    }
}
