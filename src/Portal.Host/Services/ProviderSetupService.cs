using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Portal.Common;

namespace Portal.Host.Services;

public sealed class ProviderHealthStatus
{
    public bool CredentialProviderGuidsOk { get; init; }
    public bool ComRegistrationOk { get; init; }
    public bool FilesOk { get; init; }
    public bool IsHealthy => CredentialProviderGuidsOk && ComRegistrationOk && FilesOk;
    public IReadOnlyList<string> FailureReasons { get; init; } = Array.Empty<string>();
}

public class ProviderSetupService
{
    private const string ProviderGuid = "{4F507F6A-5A02-4F19-86B3-1C04F0E8C2E5}";
    private const string ReverseProviderGuid = "{A3F2B8C1-7D4E-4F9A-B6E5-1C8D3A2F0E9C}";
    private const string CredProvRegPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers";

    // Current architecture uses a single shared tile/provider class.
    // Reverse GUID is treated as legacy and should not block health-checks.
    private static readonly string[] RequiredGuids = { ProviderGuid };

    public bool InstallProvider(string dllPath)
        => InstallProviderAsync(dllPath).GetAwaiter().GetResult();

    public async Task<bool> InstallProviderAsync(string dllPath, CancellationToken cancellationToken = default)
    {
        Logger.Log($"[ProviderSetup] Installing Credential Provider from: {dllPath}");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            ValidateProviderPathIsOnCurrentVolume(dllPath);

            if (!File.Exists(dllPath))
            {
                Logger.LogError($"[ProviderSetup] DLL not found at: {dllPath}");
                return false;
            }

            ValidateRequiredRuntimeFiles(dllPath);

            var result = await RunProcessAsync("regsvr32", $"/s \"{dllPath}\"", cancellationToken);
            Logger.Log($"[ProviderSetup] Executed regsvr32. Output: {result}");

            cancellationToken.ThrowIfCancellationRequested();

            Logger.Log($"[ProviderSetup] Registering Provider GUID: {ProviderGuid}");
            RegisterCredentialProvider(ProviderGuid, "PortalWin Remote Unlock");

            // Legacy reverse provider is no longer used by the shared-tile flow.
            CleanupLegacyReverseProviderRegistration();

            cancellationToken.ThrowIfCancellationRequested();

            var health = CheckProviderHealth(dllPath);
            if (!health.IsHealthy)
            {
                var reason = BuildHealthFailureMessage(health);
                Logger.LogError($"[ProviderSetup] Post-install health check failed: {reason}");
                throw new InvalidOperationException(reason);
            }

            Logger.Log("[ProviderSetup] Provider installation sequence completed successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError("[ProviderSetup] Install failed with exception.", ex);
            throw;
        }
    }

    public bool UninstallProvider(string? dllPath)
        => UninstallProviderAsync(dllPath).GetAwaiter().GetResult();

    public async Task<bool> UninstallProviderAsync(string? dllPath, CancellationToken cancellationToken = default)
    {
        Logger.Log($"[ProviderSetup] Uninstalling Credential Provider: {dllPath ?? "(no DLL path)"}");
        try
        {
            if (!string.IsNullOrEmpty(dllPath) && File.Exists(dllPath))
            {
                ValidateProviderPathIsOnCurrentVolume(dllPath);
                Logger.Log("[ProviderSetup] Unregistering COM DLL...");
                await RunProcessAsync("regsvr32", $"/u /s \"{dllPath}\"", cancellationToken);
            }
            else
            {
                Logger.LogWarning("[ProviderSetup] DLL not found, skipping COM deregistration. Registry cleanup will still proceed.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            Logger.Log($"[ProviderSetup] Removing Registry Key: {ProviderGuid}");
            UnregisterCredentialProvider(ProviderGuid);

            Logger.Log($"[ProviderSetup] Removing Registry Key: {ReverseProviderGuid}");
            UnregisterCredentialProvider(ReverseProviderGuid);

            Logger.Log("[ProviderSetup] Provider uninstalled.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError("[ProviderSetup] Uninstall failed with exception.", ex);
            return false;
        }
    }

    public bool CheckProviderInstalled()
    {
        return CheckProviderHealth().IsHealthy;
    }

    public ProviderHealthStatus CheckProviderHealth(string? dllPath = null)
    {
        var failures = new List<string>();

        try
        {
            var expectedComhostPath = ResolveExpectedProviderComhostPath(dllPath);

            var guidRegistryOk = CheckCredentialProviderRegistryKeys(failures);
            var comRegistrationOk = CheckComRegistrations(expectedComhostPath, failures);
            var filesOk = CheckProviderFiles(expectedComhostPath, failures);

            // Optional legacy signal (non-blocking): stale reverse registration is logged for diagnostics.
            CheckLegacyReverseComRegistration(expectedComhostPath);

            return new ProviderHealthStatus
            {
                CredentialProviderGuidsOk = guidRegistryOk,
                ComRegistrationOk = comRegistrationOk,
                FilesOk = filesOk,
                FailureReasons = failures.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }
        catch (Exception ex)
        {
            failures.Add($"Provider health check failed unexpectedly: {ex.Message}");
            return new ProviderHealthStatus
            {
                CredentialProviderGuidsOk = false,
                ComRegistrationOk = false,
                FilesOk = false,
                FailureReasons = failures
            };
        }
    }

    private static void ValidateRequiredRuntimeFiles(string dllPath)
    {
        var dir = Path.GetDirectoryName(dllPath);
        var baseName = Path.GetFileNameWithoutExtension(dllPath).Replace(".comhost", "", StringComparison.OrdinalIgnoreCase);

        var requiredFiles = new[]
        {
            Path.Combine(dir!, $"{baseName}.dll"),
            Path.Combine(dir!, $"{baseName}.deps.json"),
            Path.Combine(dir!, $"{baseName}.runtimeconfig.json")
        };

        foreach (var file in requiredFiles)
        {
            if (!File.Exists(file))
            {
                Logger.LogError($"[ProviderSetup] Missing required COM dependency: {file}");
                throw new FileNotFoundException($"Missing required file for .NET COM hosting. Please make sure '{Path.GetFileName(file)}' is located in the same folder as the .comhost.dll!");
            }
        }
    }

    private static void ValidateProviderPathIsOnCurrentVolume(string dllPath)
    {
        var normalizedPath = NormalizePath(dllPath) ?? throw new InvalidOperationException("Provider path could not be resolved.");
        var currentAppBaseDir = NormalizePath(AppDomain.CurrentDomain.BaseDirectory) ?? throw new InvalidOperationException("Application base directory could not be resolved.");
        var providerVolume = Path.GetPathRoot(normalizedPath);
        var currentVolume = Path.GetPathRoot(currentAppBaseDir);

        if (!string.Equals(providerVolume, currentVolume, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Provider registration is allowed only on the current application volume. Current volume: {currentVolume}. Provider volume: {providerVolume}. Path: {normalizedPath}");
        }
    }

    private bool CheckCredentialProviderRegistryKeys(List<string> failures)
    {
        var ok = true;
        foreach (var guid in RequiredGuids)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{CredProvRegPath}\{guid}");
            if (key == null)
            {
                failures.Add($"Credential Provider GUID key is missing: {guid}");
                ok = false;
            }
        }

        return ok;
    }

    private void CleanupLegacyReverseProviderRegistration()
    {
        try
        {
            Logger.Log($"[ProviderSetup] Cleaning up legacy reverse provider GUID: {ReverseProviderGuid}");
            UnregisterCredentialProvider(ReverseProviderGuid);
        }
        catch
        {
        }
    }

    private static bool CheckComRegistrations(string? expectedComhostPath, List<string> failures)
    {
        var ok = true;
        foreach (var guid in RequiredGuids)
        {
            using var inprocKey = Registry.ClassesRoot.OpenSubKey($@"CLSID\{guid}\InprocServer32");
            if (inprocKey == null)
            {
                failures.Add($"COM registration is missing for GUID: {guid}");
                ok = false;
                continue;
            }

            var rawRegisteredPath = (inprocKey.GetValue(null) ?? inprocKey.GetValue(string.Empty)) as string;
            var registeredPath = NormalizePath(rawRegisteredPath);

            if (string.IsNullOrWhiteSpace(registeredPath))
            {
                failures.Add($"COM registration has empty InprocServer32 path for GUID: {guid}");
                ok = false;
                continue;
            }

            if (!File.Exists(registeredPath))
            {
                failures.Add($"COM registration points to missing file for GUID {guid}: {registeredPath}");
                ok = false;
            }

            if (!string.IsNullOrWhiteSpace(expectedComhostPath) &&
                !string.Equals(registeredPath, expectedComhostPath, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"COM registration path mismatch for GUID {guid}. Expected: {expectedComhostPath}. Actual: {registeredPath}");
                ok = false;
            }
        }

        return ok;
    }

    private static void CheckLegacyReverseComRegistration(string? expectedComhostPath)
    {
        try
        {
            using var inprocKey = Registry.ClassesRoot.OpenSubKey($@"CLSID\{ReverseProviderGuid}\InprocServer32");
            if (inprocKey == null)
            {
                return;
            }

            var rawRegisteredPath = (inprocKey.GetValue(null) ?? inprocKey.GetValue(string.Empty)) as string;
            var registeredPath = NormalizePath(rawRegisteredPath);
            if (string.IsNullOrWhiteSpace(registeredPath))
            {
                Logger.LogWarning($"[ProviderSetup] Legacy reverse GUID COM key has empty path: {ReverseProviderGuid}");
                return;
            }

            if (!string.IsNullOrWhiteSpace(expectedComhostPath) &&
                !string.Equals(registeredPath, expectedComhostPath, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning($"[ProviderSetup] Legacy reverse GUID COM path differs from active provider path and is ignored. GUID={ReverseProviderGuid}, path={registeredPath}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[ProviderSetup] Failed to inspect legacy reverse GUID COM registration: {ex.Message}");
        }
    }

    private static bool CheckProviderFiles(string? comhostPath, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(comhostPath))
        {
            failures.Add("Provider comhost path could not be resolved.");
            return false;
        }

        var dir = Path.GetDirectoryName(comhostPath);
        if (string.IsNullOrWhiteSpace(dir))
        {
            failures.Add($"Invalid provider comhost path: {comhostPath}");
            return false;
        }

        var baseName = Path.GetFileNameWithoutExtension(comhostPath).Replace(".comhost", "", StringComparison.OrdinalIgnoreCase);
        var requiredFiles = new[]
        {
            comhostPath,
            Path.Combine(dir, $"{baseName}.dll"),
            Path.Combine(dir, $"{baseName}.deps.json"),
            Path.Combine(dir, $"{baseName}.runtimeconfig.json")
        };

        var ok = true;
        foreach (var file in requiredFiles)
        {
            if (!File.Exists(file))
            {
                failures.Add($"Required provider file is missing: {file}");
                ok = false;
            }
        }

        return ok;
    }

    private static string? ResolveExpectedProviderComhostPath(string? dllPath)
    {
        if (!string.IsNullOrWhiteSpace(dllPath))
        {
            return NormalizePath(dllPath);
        }

        var defaultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CredentialProvider", "Portal.CredentialProvider.comhost.dll");
        return NormalizePath(defaultPath);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().Trim('"');
        var expanded = Environment.ExpandEnvironmentVariables(trimmed);
        try
        {
            return Path.GetFullPath(expanded);
        }
        catch
        {
            return expanded;
        }
    }

    private static string BuildHealthFailureMessage(ProviderHealthStatus status)
    {
        if (status.FailureReasons.Count == 0)
        {
            return "Provider health-check failed for unknown reason.";
        }

        return $"Provider health-check failed: {string.Join("; ", status.FailureReasons)}";
    }

    private void RegisterCredentialProvider(string guid, string name)
    {
        using var key = Registry.LocalMachine.CreateSubKey($@"{CredProvRegPath}\{guid}");
        key?.SetValue("", name);
    }

    private void UnregisterCredentialProvider(string guid)
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree($@"{CredProvRegPath}\{guid}", false);
        }
        catch
        {
        }
    }

    private async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            process = Process.Start(psi);
            if (process == null) return "Process start failed";
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKillProcess(process);
                throw;
            }

            var output = await outputTask;
            var error = await errorTask;

            return string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
            // Best-effort cancellation.
        }
    }
}
