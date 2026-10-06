using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Portal.Common.Helpers;

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
            // Harden root storage directory: Administrators/SYSTEM full control, Users read only
            SecurityHardeningHelper.HardenDirectoryPermissions(newRootDirectory);

            Directory.CreateDirectory(newLogsDirectory);
            // Allow Authenticated Users to write log files in dedicated Logs subfolder
            EnsureLogsDirectoryPermissions(newLogsDirectory);

            _initialized = true;
        }
    }

    private static void EnsureLogsDirectoryPermissions(string logsDirectoryPath)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var dirInfo = new DirectoryInfo(logsDirectoryPath);
            var dirSecurity = dirInfo.GetAccessControl();

            var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            var accessRule = new FileSystemAccessRule(
                authenticatedUsers,
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);

            dirSecurity.AddAccessRule(accessRule);
            dirInfo.SetAccessControl(dirSecurity);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[PortalStoragePaths] Failed to set directory ACLs on '{logsDirectoryPath}': {ex.Message}");
        }
    }
}
