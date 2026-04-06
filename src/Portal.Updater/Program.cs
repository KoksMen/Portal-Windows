using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Portal.Common;
using Portal.Common.Models;

Logger.Initialize("updater.log");

if (args.Length == 0)
{
    Logger.LogError("[Updater] Missing update session path.");
    return 1;
}

AppUpdateSession? session = null;
var rollbackAttempted = false;
var rollbackSucceeded = false;
var updateFailed = false;
var updateErrorCode = "UNKNOWN";
var updateErrorMessage = "Unknown update failure.";

try
{
    var sessionPath = args[0];
    if (!File.Exists(sessionPath))
    {
        throw new FileNotFoundException("Update session file not found.", sessionPath);
    }

    var sessionJson = await File.ReadAllTextAsync(sessionPath);
    session = JsonSerializer.Deserialize<AppUpdateSession>(sessionJson)
        ?? throw new InvalidOperationException("Failed to deserialize update session.");

    Logger.Log($"[Updater] Starting update session {session.SessionId} for version {session.TargetVersion}.");

    await WaitForHostExitAsync(session.HostProcessId);
    CloseApplicationProcesses(session.ApplicationDirectory);

    var sourceRoot = ResolveStagingRoot(session.StagingDirectory);
    ReplaceDirectoryContents(sourceRoot, session.ApplicationDirectory);

    var providerDllPath = Path.Combine(
        session.ApplicationDirectory,
        session.ProviderDllRelativePath ?? Path.Combine("CredentialProvider", "Portal.CredentialProvider.comhost.dll"));

    var providerReinstallSucceeded = await ReinstallProviderAsync(providerDllPath);
    if (!providerReinstallSucceeded)
    {
        throw new InvalidOperationException("Credential Provider reinstall failed.");
    }

    await WriteResultAsync(session.ResultFilePath, new AppUpdateResult
    {
        Success = true,
        TargetVersion = session.TargetVersion,
        Summary = $"Update {session.TargetVersion} installed successfully.",
        Details = "Files were replaced and Credential Provider was reinstalled.",
        RequiresProviderReinstall = true,
        ProviderReinstallSucceeded = true,
        RollbackAttempted = false,
        RollbackSucceeded = false
    });

    RelaunchHost(session.HostExecutablePath);
    return 0;
}
catch (Exception ex)
{
    updateFailed = true;
    updateErrorMessage = ex.Message;
    updateErrorCode = ex switch
    {
        FileNotFoundException => "FILE_NOT_FOUND",
        DirectoryNotFoundException => "DIRECTORY_NOT_FOUND",
        UnauthorizedAccessException => "ACCESS_DENIED",
        InvalidOperationException => "INVALID_OPERATION",
        IOException => "IO_ERROR",
        _ => "UNHANDLED_EXCEPTION"
    };
    Logger.LogError("[Updater] Update failed.", ex);
}

if (updateFailed && session != null)
{
    var backupDirectory = session.BackupDirectory;
    if (Directory.Exists(backupDirectory))
    {
        rollbackAttempted = true;
        try
        {
            Logger.Log("[Updater] Attempting rollback from backup.");
            ReplaceDirectoryContents(backupDirectory, session.ApplicationDirectory);
            rollbackSucceeded = true;
            Logger.Log("[Updater] Rollback completed successfully.");
        }
        catch (Exception rollbackEx)
        {
            rollbackSucceeded = false;
            Logger.LogError("[Updater] Rollback failed.", rollbackEx);
            updateErrorMessage = $"{updateErrorMessage} Rollback failed: {rollbackEx.Message}";
        }
    }

    try
    {
        await WriteResultAsync(session.ResultFilePath, new AppUpdateResult
        {
            Success = false,
            TargetVersion = session.TargetVersion,
            Summary = $"Update {session.TargetVersion} failed.",
            Details = updateErrorMessage,
            RequiresProviderReinstall = session.RequiresProviderReinstall,
            ProviderReinstallSucceeded = false,
            RollbackAttempted = rollbackAttempted,
            RollbackSucceeded = rollbackSucceeded,
            ErrorCode = updateErrorCode
        });

        if (!string.IsNullOrWhiteSpace(session.HostExecutablePath) && File.Exists(session.HostExecutablePath))
        {
            RelaunchHost(session.HostExecutablePath);
        }
    }
    catch (Exception nestedEx)
    {
        Logger.LogError("[Updater] Failed to persist update error state.", nestedEx);
    }
}

return 1;

static async Task WaitForHostExitAsync(int processId)
{
    try
    {
        var process = Process.GetProcessById(processId);
        Logger.Log($"[Updater] Waiting for Host process {processId} to exit.");
        await process.WaitForExitAsync();
    }
    catch (ArgumentException)
    {
        Logger.Log($"[Updater] Host process {processId} is already closed.");
    }
}

static void CloseApplicationProcesses(string applicationDirectory)
{
    var normalizedAppDir = NormalizePath(applicationDirectory);
    foreach (var processName in new[] { "Portal.Host", "Portal.Client" })
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                var processPath = NormalizePath(process.MainModule?.FileName);
                if (!string.Equals(processPath != null ? Path.GetDirectoryName(processPath) : null, normalizedAppDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Logger.Log($"[Updater] Closing process {process.ProcessName} ({process.Id}).");
                if (!process.CloseMainWindow())
                {
                    process.Kill(true);
                }

                process.WaitForExit(10_000);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Updater] Failed to close process {process.ProcessName} ({process.Id}): {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}

static string ResolveStagingRoot(string stagingDirectory)
{
    var root = NormalizePath(stagingDirectory) ?? throw new InvalidOperationException("Staging directory is invalid.");
    if (!Directory.Exists(root))
    {
        throw new DirectoryNotFoundException($"Staging directory not found: {root}");
    }

    var files = Directory.GetFiles(root);
    var directories = Directory.GetDirectories(root);
    if (files.Length == 0 && directories.Length == 1)
    {
        return directories[0];
    }

    return root;
}

static void ReplaceDirectoryContents(string sourceDirectory, string destinationDirectory)
{
    if (!Directory.Exists(sourceDirectory))
    {
        throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");
    }

    if (!Directory.Exists(destinationDirectory))
    {
        Directory.CreateDirectory(destinationDirectory);
    }

    ClearDirectoryContents(destinationDirectory);
    CopyDirectoryContents(sourceDirectory, destinationDirectory);
}

static void ClearDirectoryContents(string directoryPath)
{
    foreach (var file in Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
    {
        File.SetAttributes(file, FileAttributes.Normal);
        File.Delete(file);
    }

    foreach (var directory in Directory.GetDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly))
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
{
    Directory.CreateDirectory(destinationDirectory);

    foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
    {
        var relativePath = Path.GetRelativePath(sourceDirectory, directory);
        Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
    }

    foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
    {
        var relativePath = Path.GetRelativePath(sourceDirectory, file);
        var destinationPath = Path.Combine(destinationDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(file, destinationPath, overwrite: true);
    }
}

static async Task<bool> ReinstallProviderAsync(string dllPath)
{
    if (!File.Exists(dllPath))
    {
        Logger.LogWarning($"[Updater] Provider reinstall skipped because DLL was not found: {dllPath}");
        return false;
    }

    var regsvrResult = await RunProcessAsync("regsvr32", $"/s \"{dllPath}\"");
    Logger.Log($"[Updater] regsvr32 output: {regsvrResult}");

    RegisterCredentialProvider("{4F507F6A-5A02-4F19-86B3-1C04F0E8C2E5}", "Portal-Windows Remote Unlock");
    CleanupLegacyReverseProviderRegistration();

    return true;
}

static void RegisterCredentialProvider(string guid, string name)
{
    const string credProvRegPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers";
    using var key = Registry.LocalMachine.CreateSubKey($@"{credProvRegPath}\{guid}");
    key?.SetValue("", name);
}

static void CleanupLegacyReverseProviderRegistration()
{
    const string credProvRegPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers";
    const string reverseProviderGuid = "{A3F2B8C1-7D4E-4F9A-B6E5-1C8D3A2F0E9C}";
    try
    {
        Registry.LocalMachine.DeleteSubKeyTree($@"{credProvRegPath}\{reverseProviderGuid}", false);
    }
    catch
    {
    }
}

static async Task<string> RunProcessAsync(string fileName, string arguments)
{
    var process = Process.Start(new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    });

    if (process == null)
    {
        return "Process start failed.";
    }

    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await outputTask;
    var error = await errorTask;
    process.Dispose();

    return string.IsNullOrWhiteSpace(error) ? output : $"{output}{Environment.NewLine}{error}";
}

static async Task WriteResultAsync(string resultPath, AppUpdateResult result)
{
    Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(resultPath, json);
}

static void RelaunchHost(string hostExecutablePath)
{
    Logger.Log($"[Updater] Relaunching Host from {hostExecutablePath}.");
    Process.Start(new ProcessStartInfo
    {
        FileName = hostExecutablePath,
        WorkingDirectory = Path.GetDirectoryName(hostExecutablePath) ?? AppDomain.CurrentDomain.BaseDirectory,
        UseShellExecute = true
    });
}

static string? NormalizePath(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return null;
    }

    try
    {
        return Path.GetFullPath(path.Trim().Trim('"'));
    }
    catch
    {
        return path.Trim().Trim('"');
    }
}
