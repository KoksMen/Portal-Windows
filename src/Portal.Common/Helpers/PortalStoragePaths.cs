using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Portal.Common.Helpers;

namespace Portal.Common;

/// <summary>
/// Centralized storage layout under %ProgramData%\Portal-Windows with distinct security boundaries:
/// - config\        (Admin/SYSTEM: FullControl, Users: Read)
/// - runtime\       (Admin/SYSTEM: FullControl, Users: Modify)
/// - certificates\  (Admin/SYSTEM: FullControl, Users: Read)
/// - logs\          (Admin/SYSTEM: FullControl, Users: Modify)
/// - updates\       (Admin/SYSTEM: FullControl, Users: Read only - prevents LPE/tampering)
/// </summary>
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

    public static string ConfigDirectory
    {
        get
        {
            EnsureInitialized();
            var dir = Path.Combine(RootDirectory, "config");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string RuntimeDirectory
    {
        get
        {
            EnsureInitialized();
            var dir = Path.Combine(RootDirectory, "runtime");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string CertificatesDirectory
    {
        get
        {
            EnsureInitialized();
            var dir = Path.Combine(RootDirectory, "certificates");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string LogsDirectory
    {
        get
        {
            EnsureInitialized();
            var dir = Path.Combine(RootDirectory, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string UpdatesDirectory
    {
        get
        {
            EnsureInitialized();
            var dir = Path.Combine(RootDirectory, "updates");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DefaultCertificatePath
    {
        get
        {
            var certInSubdir = Path.Combine(CertificatesDirectory, "host_cert.pfx");
            if (File.Exists(certInSubdir))
            {
                return certInSubdir;
            }

            var certInRoot = Path.Combine(RootDirectory, "host_cert.pfx");
            if (File.Exists(certInRoot))
            {
                try
                {
                    File.Copy(certInRoot, certInSubdir, overwrite: false);
                    return certInSubdir;
                }
                catch
                {
                    return certInRoot;
                }
            }

            var certInConfig = Path.Combine(ConfigDirectory, "host_cert.pfx");
            if (File.Exists(certInConfig))
            {
                try
                {
                    File.Copy(certInConfig, certInSubdir, overwrite: false);
                    return certInSubdir;
                }
                catch
                {
                    return certInConfig;
                }
            }

            return certInSubdir;
        }
    }

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
                var root = Path.Combine(GetCommonApplicationData(), CurrentRootFolderName);
                Directory.CreateDirectory(root);

                var configDir = Path.Combine(root, "config");
                var runtimeDir = Path.Combine(root, "runtime");
                var certsDir = Path.Combine(root, "certificates");
                var logsDir = Path.Combine(root, "logs");
                var updatesDir = Path.Combine(root, "updates");

                Directory.CreateDirectory(configDir);
                Directory.CreateDirectory(runtimeDir);
                Directory.CreateDirectory(certsDir);
                Directory.CreateDirectory(logsDir);
                Directory.CreateDirectory(updatesDir);

                // Apply individual hardened ACLs to enforce privilege boundaries
                ApplyPrivilegedDirectoryAcl(updatesDir);
                ApplyPrivilegedDirectoryAcl(certsDir);
                ApplyPrivilegedDirectoryAcl(configDir);
                ApplySharedDirectoryAcl(runtimeDir);
                ApplySharedDirectoryAcl(logsDir);

                // Backward compatibility migrations
                MigrateLegacyFiles(root, configDir, certsDir, logsDir);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PortalStoragePaths] Error during directory initialization: {ex}");
            }
        }
    }

    private static void MigrateLegacyFiles(string root, string configDir, string certsDir, string logsDir)
    {
        try
        {
            // Migrate config.json from root to config\
            var rootConfig = Path.Combine(root, "config.json");
            var targetConfig = Path.Combine(configDir, "config.json");
            if (File.Exists(rootConfig) && !File.Exists(targetConfig))
            {
                try { File.Copy(rootConfig, targetConfig, overwrite: false); } catch { }
            }

            // Migrate host_cert.pfx from root/config to certificates\
            var rootCert = Path.Combine(root, "host_cert.pfx");
            var configCert = Path.Combine(configDir, "host_cert.pfx");
            var targetCert = Path.Combine(certsDir, "host_cert.pfx");

            if (!File.Exists(targetCert))
            {
                if (File.Exists(rootCert))
                {
                    try { File.Copy(rootCert, targetCert, overwrite: false); } catch { }
                }
                else if (File.Exists(configCert))
                {
                    try { File.Copy(configCert, targetCert, overwrite: false); } catch { }
                }
            }

            // Also synchronize to config directory if possible to guarantee legacy readers don't fail
            if (File.Exists(targetCert) && !File.Exists(configCert))
            {
                try { File.Copy(targetCert, configCert, overwrite: false); } catch { }
            }

            // Migrate legacy uppercase Logs directory if present
            var legacyLogs = Path.Combine(root, "Logs");
            if (Directory.Exists(legacyLogs) && !string.Equals(legacyLogs, logsDir, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var file in Directory.GetFiles(legacyLogs))
                {
                    var dest = Path.Combine(logsDir, Path.GetFileName(file));
                    if (!File.Exists(dest))
                    {
                        try { File.Copy(file, dest, overwrite: false); } catch { }
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Restricts directory access to SYSTEM and Administrators (FullControl).
    /// Standard users receive ReadAndExecute only (NO Write/Modify), preventing LPE / hijacking.
    /// </summary>
    private static void ApplyPrivilegedDirectoryAcl(string directoryPath)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Directory.Exists(directoryPath))
            {
                return;
            }

            var dirInfo = new DirectoryInfo(directoryPath);
            var dirSecurity = new DirectorySecurity();
            dirSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var authUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            dirSecurity.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirSecurity.AddAccessRule(new FileSystemAccessRule(
                adminSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirSecurity.AddAccessRule(new FileSystemAccessRule(
                authUsersSid,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirSecurity.AddAccessRule(new FileSystemAccessRule(
                usersSid,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirInfo.SetAccessControl(dirSecurity);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PortalStoragePaths] Failed privileged ACL on '{directoryPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Sets ACL allowing Authenticated Users to write/modify (needed for logs and runtime IPC).
    /// </summary>
    private static void ApplySharedDirectoryAcl(string directoryPath)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Directory.Exists(directoryPath))
            {
                return;
            }

            var dirInfo = new DirectoryInfo(directoryPath);
            var dirSecurity = new DirectorySecurity();
            dirSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var authUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

            dirSecurity.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirSecurity.AddAccessRule(new FileSystemAccessRule(
                adminSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirSecurity.AddAccessRule(new FileSystemAccessRule(
                authUsersSid,
                FileSystemRights.Modify | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirInfo.SetAccessControl(dirSecurity);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PortalStoragePaths] Failed shared ACL on '{directoryPath}': {ex.Message}");
        }
    }
}
