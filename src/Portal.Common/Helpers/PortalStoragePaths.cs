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
        if (_initialized)
        {
            return;
        }

        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            try
            {
                var newRootDirectory = Path.Combine(GetCommonApplicationData(), CurrentRootFolderName);
                var newLogsDirectory = Path.Combine(newRootDirectory, "Logs");

                Directory.CreateDirectory(newRootDirectory);
                EnsureDirectoryPermissions(newRootDirectory);

                Directory.CreateDirectory(newLogsDirectory);
                EnsureDirectoryPermissions(newLogsDirectory);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PortalStoragePaths] Error during directory initialization: {ex}");
            }
        }
    }

    private static void EnsureDirectoryPermissions(string directoryPath)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Directory.Exists(directoryPath))
            {
                return;
            }

            var dirInfo = new DirectoryInfo(directoryPath);
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
            System.Diagnostics.Debug.WriteLine($"[PortalStoragePaths] Failed to set directory ACLs on '{directoryPath}': {ex.Message}");
        }
    }
}
