using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Portal.Common.Helpers;

/// <summary>
/// Hardens directory and file ACLs to defend against DLL hijacking and unauthorized local modification.
/// Restricts write/modify privileges strictly to SYSTEM and Administrators, while ensuring
/// ordinary users only possess Read and Execute permissions.
/// </summary>
public static class SecurityHardeningHelper
{
    private const int LOAD_LIBRARY_SEARCH_APPLICATION_DIR = 0x00000200;
    private const int LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(int directoryFlags);

    /// <summary>
    /// Configures the safe DLL search directory flags for the process, restricting search to
    /// System32 and the application directory, mitigating CWE-427 (Untrusted Search Path / DLL Hijacking).
    /// </summary>
    public static void EnableSafeDllSearchMode()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_APPLICATION_DIR);
            Logger.Log("[SecurityHardening] SetDefaultDllDirectories configured for safe DLL search path.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[SecurityHardening] Failed to set default DLL directories: {ex.Message}");
        }
    }

    /// <summary>
    /// Hardens access control lists (ACL) on a directory:
    /// - LocalSystem: FullControl
    /// - Builtin Administrators: FullControl
    /// - Authenticated Users / Users: ReadAndExecute, Synchronize (NO Write/Modify)
    /// - Disables inheritance to cut off unsafe parent permissions.
    /// </summary>
    public static bool HardenDirectoryPermissions(string directoryPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(directoryPath))
            return false;

        try
        {
            if (!Directory.Exists(directoryPath))
                Directory.CreateDirectory(directoryPath);

            var dirInfo = new DirectoryInfo(directoryPath);
            var dirSecurity = new DirectorySecurity();

            // Protect against inheritance, stripping any unsafe rules inherited from parent directories
            dirSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var authUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

            // Grant SYSTEM FullControl
            dirSecurity.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            // Grant Administrators FullControl
            dirSecurity.AddAccessRule(new FileSystemAccessRule(
                adminSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            // Grant Users Read & Execute only
            dirSecurity.AddAccessRule(new FileSystemAccessRule(
                usersSid,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            // Grant Authenticated Users Read & Execute only
            dirSecurity.AddAccessRule(new FileSystemAccessRule(
                authUsersSid,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirInfo.SetAccessControl(dirSecurity);
            Logger.Log($"[SecurityHardening] Hardened directory permissions on '{directoryPath}'.");
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.LogWarning($"[SecurityHardening] Insufficient privileges to set ACLs on '{directoryPath}': {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[SecurityHardening] Failed to harden directory '{directoryPath}'", ex);
            return false;
        }
    }

    /// <summary>
    /// Hardens access control lists on an individual file (e.g. host_cert.pfx or config.json).
    /// </summary>
    public static bool HardenFilePermissions(string filePath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath))
            return false;

        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileSecurity = new FileSecurity();

            fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            fileSecurity.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));

            fileSecurity.AddAccessRule(new FileSystemAccessRule(
                adminSid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));

            // File read only for standard users
            fileSecurity.AddAccessRule(new FileSystemAccessRule(
                usersSid,
                FileSystemRights.Read | FileSystemRights.Synchronize,
                AccessControlType.Allow));

            fileInfo.SetAccessControl(fileSecurity);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[SecurityHardening] Failed to harden file '{filePath}': {ex.Message}");
            return false;
        }
    }
}
