using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Portal.Common;
using Portal.Common.Helpers;
using Portal.Common.Models;

namespace Portal.Host.Services;

/// <summary>
/// Packages system environment data, component health statuses, sanitized configuration,
/// and recent Host/CredentialProvider logs into a single diagnostic ZIP archive for troubleshooting.
/// </summary>
public sealed class DiagnosticReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ProviderSetupService _providerSetup;
    private readonly FirewallService _firewall;
    private readonly CertificateManager _certManager;
    private readonly NetworkService _networkService;
    private readonly BluetoothService _bluetoothService;

    public DiagnosticReportService(
        ProviderSetupService providerSetup,
        FirewallService firewall,
        CertificateManager certManager,
        NetworkService networkService,
        BluetoothService bluetoothService)
    {
        _providerSetup = providerSetup;
        _firewall = firewall;
        _certManager = certManager;
        _networkService = networkService;
        _bluetoothService = bluetoothService;
    }

    /// <summary>
    /// Generates a diagnostic zip archive containing environment data, health checks,
    /// sanitized config and all available logs.
    /// </summary>
    public async Task<string> CreateDiagnosticArchiveAsync(
        string destinationZipPath,
        PortalWinConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);
        ArgumentNullException.ThrowIfNull(config);

        var destinationDir = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrWhiteSpace(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        // Temporary file in case destination is being replaced
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"portal_diag_{Guid.NewGuid():N}.tmp");

        try
        {
            using (var zipStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 1. Gather component health
                var providerHealth = _providerSetup.CheckProviderHealth();
                var firewallOk = await _firewall.CheckFirewallRule(config.Port, cancellationToken);
                var certOk = _certManager.CheckCertificate();

                // 2. Gather network & bluetooth details
                var networkInterfaces = CollectNetworkInterfaces();
                var preferredMac = _networkService.GetPreferredMacAddress(config.VpnCompatibilityModeEnabled);
                string? bluetoothAddress = null;
                try
                {
                    bluetoothAddress = await _bluetoothService.GetLocalBluetoothAddressAsync();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[DiagnosticReport] Unable to query Bluetooth address: {ex.Message}");
                }

                // 3. Inspect Certificate metadata (sanitized, NO private keys)
                var certMetadata = InspectCertificate();

                // 4. Build sanitized config
                var sanitizedConfig = BuildSanitizedConfig(config);

                // 5. Build structured diagnostic report object
                var report = new
                {
                    title = "Portal-Windows Diagnostic Report",
                    version = "1.5.5-Herta",
                    generated_at_utc = DateTime.UtcNow.ToString("o"),
                    os_environment = new
                    {
                        os_description = RuntimeInformation.OSDescription,
                        os_architecture = RuntimeInformation.OSArchitecture.ToString(),
                        process_architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                        clr_version = Environment.Version.ToString(),
                        machine_name = Environment.MachineName,
                        processor_count = Environment.ProcessorCount,
                        is_64bit_os = Environment.Is64BitOperatingSystem,
                        system_directory = Environment.SystemDirectory,
                        uptime = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\.hh\:mm\:ss")
                    },
                    components = new
                    {
                        credential_provider = new
                        {
                            is_healthy = providerHealth.IsHealthy,
                            guids_ok = providerHealth.CredentialProviderGuidsOk,
                            com_registration_ok = providerHealth.ComRegistrationOk,
                            files_ok = providerHealth.FilesOk,
                            failure_reasons = providerHealth.FailureReasons
                        },
                        firewall = new
                        {
                            port = config.Port,
                            rule_active = firewallOk
                        },
                        certificate = new
                        {
                            is_installed = certOk,
                            details = certMetadata
                        }
                    },
                    network = new
                    {
                        vpn_compatibility_mode = config.VpnCompatibilityModeEnabled,
                        preferred_mac = preferredMac,
                        interfaces = networkInterfaces
                    },
                    bluetooth = new
                    {
                        local_address = bluetoothAddress ?? "Unavailable"
                    },
                    configuration = sanitizedConfig
                };

                // Add system_environment.json
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
                var jsonEntry = archive.CreateEntry("system_environment.json", CompressionLevel.Optimal);
                using (var entryStream = jsonEntry.Open())
                {
                    await entryStream.WriteAsync(jsonBytes, cancellationToken);
                }

                // Add summary.txt
                var summaryText = BuildSummaryText(report, providerHealth, firewallOk, certOk, bluetoothAddress);
                var summaryEntry = archive.CreateEntry("summary.txt", CompressionLevel.Optimal);
                using (var entryStream = summaryEntry.Open())
                using (var writer = new StreamWriter(entryStream, Encoding.UTF8))
                {
                    await writer.WriteAsync(summaryText);
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 6. Collect logs
                await CollectLogsAsync(archive, cancellationToken);
            }

            // Move completed archive to target path
            if (File.Exists(destinationZipPath))
            {
                File.Delete(destinationZipPath);
            }
            File.Move(tempZipPath, destinationZipPath);
            Logger.Log($"[DiagnosticReport] Diagnostic archive generated successfully: {destinationZipPath}");
            return destinationZipPath;
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); } catch { /* ignore temp cleanup errors */ }
            }
        }
    }

    private static object BuildSanitizedConfig(PortalWinConfig config)
    {
        return new
        {
            port = config.Port,
            ui_language = config.UiLanguage,
            window_backdrop = config.WindowBackdrop,
            unlock_mode = config.UnlockMode.ToString(),
            host_request_trigger = config.HostRequestTrigger.ToString(),
            host_request_timeout_minutes = config.HostRequestTimeoutMinutes,
            show_lock_screen_progress = config.ShowLockScreenProgress,
            emergency_cancel_enabled = config.EmergencyCancelEnabled,
            emergency_cancel_hold_duration_ms = config.EmergencyCancelHoldDurationMs,
            emergency_cancel_hotkey = config.EmergencyCancelHotkey,
            enforce_unique_account_per_transport = config.EnforceUniqueAccountPerTransport,
            enforce_unique_account_across_transports = config.EnforceUniqueAccountAcrossTransports,
            vpn_compatibility_mode_enabled = config.VpnCompatibilityModeEnabled,
            auto_update_checks_enabled = config.AutoUpdateChecksEnabled,
            last_update_check_utc = config.LastUpdateCheckUtc,
            last_discovered_update_version = config.LastDiscoveredUpdateVersion,
            devices_count = config.Devices.Count,
            devices = config.Devices.Select(d => new
            {
                name = d.Name,
                client_id = d.ClientId,
                transport = d.TransportType.ToString(),
                paired_at = d.PairedAt.ToString("o"),
                is_enabled = d.IsEnabled,
                accounts_count = d.Accounts.Count
            }).ToList()
        };
    }

    private static object? InspectCertificate()
    {
        try
        {
            var certPath = CertificateService.DefaultCertPath;
            if (!File.Exists(certPath))
            {
                return new { exists = false, message = "Certificate file not found." };
            }

            using var cert = new X509Certificate2(certPath, string.Empty, X509KeyStorageFlags.DefaultKeySet);
            return new
            {
                exists = true,
                subject = cert.Subject,
                issuer = cert.Issuer,
                thumbprint = cert.Thumbprint,
                not_before = cert.NotBefore.ToString("yyyy-MM-dd HH:mm:ss"),
                not_after = cert.NotAfter.ToString("yyyy-MM-dd HH:mm:ss"),
                has_private_key = cert.HasPrivateKey,
                signature_algorithm = cert.SignatureAlgorithm.FriendlyName
            };
        }
        catch (Exception ex)
        {
            return new { exists = false, error = ex.Message };
        }
    }

    private static List<object> CollectNetworkInterfaces()
    {
        var result = new List<object>();
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                var ips = new List<string>();
                try
                {
                    var props = ni.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            ips.Add(addr.Address.ToString());
                        }
                    }
                }
                catch { /* ignore address errors */ }

                result.Add(new
                {
                    name = ni.Name,
                    description = ni.Description,
                    type = ni.NetworkInterfaceType.ToString(),
                    status = ni.OperationalStatus.ToString(),
                    speed_mbps = ni.Speed > 0 ? (ni.Speed / 1_000_000).ToString() : "unknown",
                    ipv4_addresses = ips
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[DiagnosticReport] Failed to enumerate network interfaces: {ex.Message}");
        }

        return result;
    }

    private static async Task CollectLogsAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var visitedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Primary location: C:\ProgramData\Portal-Windows\Logs
        if (Directory.Exists(PortalStoragePaths.LogsDirectory))
        {
            await CopyDirectoryLogsAsync(archive, PortalStoragePaths.LogsDirectory, "logs/primary", visitedFiles, cancellationToken);
        }

        // Secondary / user local location: %LocalAppData%\PortalWin\logs (if exists from legacy or auxiliary tools)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var altLogsDir = Path.Combine(localAppData, "PortalWin", "logs");
        if (Directory.Exists(altLogsDir))
        {
            await CopyDirectoryLogsAsync(archive, altLogsDir, "logs/localappdata", visitedFiles, cancellationToken);
        }
    }

    private static async Task CopyDirectoryLogsAsync(
        ZipArchive archive,
        string sourceDir,
        string zipSubdir,
        HashSet<string> visitedFiles,
        CancellationToken cancellationToken)
    {
        try
        {
            var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fullPath = Path.GetFullPath(filePath);
                if (!visitedFiles.Add(fullPath))
                {
                    continue;
                }

                var fileName = Path.GetFileName(filePath);
                var entryName = $"{zipSubdir}/{fileName}";

                try
                {
                    // Open with FileShare.ReadWrite | FileShare.Delete to avoid locking conflicts with active loggers
                    using var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    await sourceStream.CopyToAsync(entryStream, cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[DiagnosticReport] Failed to archive log file '{filePath}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[DiagnosticReport] Error reading directory '{sourceDir}': {ex.Message}");
        }
    }

    private static string BuildSummaryText(
        dynamic report,
        ProviderHealthStatus providerHealth,
        bool firewallOk,
        bool certOk,
        string? btAddress)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine("                        PORTAL-WINDOWS DIAGNOSTIC REPORT                        ");
        sb.AppendLine("================================================================================");
        sb.AppendLine($"Generated (UTC) : {report.generated_at_utc}");
        sb.AppendLine($"Host Version    : {report.version}");
        sb.AppendLine($"OS Description  : {report.os_environment.os_description} ({report.os_environment.os_architecture})");
        sb.AppendLine($".NET CLR        : {report.os_environment.clr_version}");
        sb.AppendLine($"Machine Name    : {report.os_environment.machine_name}");
        sb.AppendLine($"System Uptime   : {report.os_environment.uptime}");
        sb.AppendLine();
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("HEALTH & ENVIRONMENT CHECKS");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine($"Credential Provider Installed : {(providerHealth.IsHealthy ? "OK" : "FAILED / MISSING")}");
        sb.AppendLine($"  - Provider GUIDs Registered : {(providerHealth.CredentialProviderGuidsOk ? "Yes" : "No")}");
        sb.AppendLine($"  - COM Host Registration     : {(providerHealth.ComRegistrationOk ? "Yes" : "No")}");
        sb.AppendLine($"  - Binary Files Present      : {(providerHealth.FilesOk ? "Yes" : "No")}");
        if (providerHealth.FailureReasons.Count > 0)
        {
            sb.AppendLine("  - Issues Detected:");
            foreach (var reason in providerHealth.FailureReasons)
            {
                sb.AppendLine($"      * {reason}");
            }
        }
        sb.AppendLine($"Windows Firewall Port 29170   : {(firewallOk ? "OK (Active)" : "FAILED / MISSING")}");
        sb.AppendLine($"TLS Host Certificate         : {(certOk ? "OK (Installed)" : "FAILED / MISSING")}");
        sb.AppendLine($"Bluetooth Radio Address       : {btAddress ?? "Not available / Not supported"}");
        sb.AppendLine();
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("ACTIVE NETWORK INTERFACES");
        sb.AppendLine("--------------------------------------------------------------------------------");
        foreach (var iface in report.network.interfaces)
        {
            var ips = string.Join(", ", iface.ipv4_addresses);
            sb.AppendLine($"* {iface.name} ({iface.type}, Status: {iface.status}): {ips}");
        }
        sb.AppendLine();
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("ARCHIVE CONTENTS");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("- summary.txt                : This overview file");
        sb.AppendLine("- system_environment.json   : Complete machine, component, and sanitized configuration data");
        sb.AppendLine("- logs/                     : Host and Credential Provider execution and error logs");
        sb.AppendLine("================================================================================");
        return sb.ToString();
    }
}
