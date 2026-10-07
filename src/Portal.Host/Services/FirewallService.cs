using Portal.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Portal.Host.Services;

public class FirewallService
{
    private const string RulePrefix = "Portal Win";

    public async Task<bool> AddFirewallRule(int port, CancellationToken cancellationToken = default)
    {
        // 1. Try native Windows Firewall COM API (instantaneous, in-process)
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
            if (policyType != null && ruleType != null)
            {
                dynamic policy = Activator.CreateInstance(policyType)!;
                dynamic rules = policy.Rules;

                string logonUIPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "LogonUI.exe");
                string credUIBrokerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "CredentialUIBroker.exe");
                string consentPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "consent.exe");
                string hostAppPath = Environment.ProcessPath ?? string.Empty;
                if (string.IsNullOrEmpty(hostAppPath))
                {
                    Logger.LogError("[FirewallService] Cannot determine host app path.");
                    return false;
                }

                string combinedPorts = string.Join(",", new[] { port.ToString(), "5353" }.Distinct(StringComparer.OrdinalIgnoreCase));
                string[] programs = { logonUIPath, credUIBrokerPath, consentPath, hostAppPath };
                string[] directions = { "in", "out" };
                string[] protocols = { "TCP", "UDP" };

                int addedCount = 0;
                foreach (string program in programs)
                {
                    foreach (string protocol in protocols)
                    {
                        foreach (string direction in directions)
                        {
                            string ruleName = BuildRuleName(protocol, direction, program, port);
                            dynamic rule = Activator.CreateInstance(ruleType)!;
                            rule.Name = ruleName;
                            rule.Description = $"Portal-Windows {protocol} {direction} rule for {Path.GetFileName(program)}";
                            rule.ApplicationName = program;
                            rule.Protocol = protocol == "TCP" ? 6 : 17;

                            if (direction == "in")
                            {
                                rule.LocalPorts = combinedPorts;
                                rule.Direction = 1; // NET_FW_RULE_DIR_IN
                            }
                            else
                            {
                                rule.RemotePorts = combinedPorts;
                                rule.Direction = 2; // NET_FW_RULE_DIR_OUT
                            }

                            rule.Action = 1; // NET_FW_ACTION_ALLOW
                            rule.Profiles = 0x7FFFFFFF; // NET_FW_PROFILE2_ALL (Domain | Private | Public)
                            rule.Enabled = true;

                            rules.Add(rule);
                            addedCount++;
                        }
                    }
                }

                Logger.Log($"[FirewallService] AddFirewallRule (COM) successfully added {addedCount} rules.");
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[FirewallService] COM AddFirewallRule failed, attempting netsh fallback: {ex.Message}");
        }

        // 2. Fallback via netsh / cmd.exe
        string fallbackLogonUI = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "LogonUI.exe");
        string fallbackCredUI = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "CredentialUIBroker.exe");
        string fallbackConsent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "consent.exe");
        string fallbackHost = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrEmpty(fallbackHost))
        {
            Logger.LogError("[FirewallService] Cannot determine host app path.");
            return false;
        }

        string ports = string.Join(",", new[] { port.ToString(), "5353" }.Distinct(StringComparer.OrdinalIgnoreCase));
        string[] progs = { fallbackLogonUI, fallbackCredUI, fallbackConsent, fallbackHost };
        string[] dirs = { "in", "out" };
        string[] protos = { "TCP", "UDP" };

        var firewallRules = new List<string>();
        foreach (string program in progs)
        {
            foreach (string protocol in protos)
            {
                foreach (string direction in dirs)
                {
                    string portType = direction == "in" ? "localport" : "remoteport";
                    string ruleName = BuildRuleName(protocol, direction, program, port);
                    firewallRules.Add($"netsh advfirewall firewall add rule name=\"{ruleName}\" dir={direction} action=allow protocol={protocol} {portType}={ports} profile=any program=\"{program}\"");
                }
            }
        }

        string command = string.Join(" && ", firewallRules);
        var result = await RunProcessAsync("cmd.exe", $"/c {command}", cancellationToken);

        Logger.Log($"[FirewallService] AddFirewallRule (netsh fallback) result: Success={result.IsSuccess}, ExitCode={result.ExitCode}");
        if (!string.IsNullOrWhiteSpace(result.Error))
            Logger.LogWarning($"[FirewallService] AddFirewallRule stderr: {result.Error}");

        return result.IsSuccess;
    }

    public async Task<bool> RemoveFirewallRule(CancellationToken cancellationToken = default)
    {
        // 1. Try native Windows Firewall COM API (instantaneous, in-process)
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType != null)
            {
                dynamic policy = Activator.CreateInstance(policyType)!;
                dynamic rules = policy.Rules;
                var toRemove = new List<string>();

                foreach (dynamic rule in rules)
                {
                    string name = rule.Name;
                    if (!string.IsNullOrEmpty(name) && name.StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        toRemove.Add(name);
                    }
                }

                foreach (var name in toRemove)
                {
                    try
                    {
                        rules.Remove(name);
                    }
                    catch
                    {
                        // Ignore individual remove errors
                    }
                }

                Logger.Log($"[FirewallService] RemoveFirewallRule (COM) removed {toRemove.Count} matching rules.");
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[FirewallService] COM RemoveFirewallRule failed, attempting fallback: {ex.Message}");
        }

        // 2. Fallback via netsh delete rule
        var netshDeleteResult = await RunProcessAsync("cmd.exe",
            $"/c netsh advfirewall firewall delete rule name=\"{RulePrefix}*\"",
            cancellationToken);

        if (netshDeleteResult.IsSuccess)
        {
            Logger.Log("[FirewallService] RemoveFirewallRule (netsh fallback) succeeded.");
            return true;
        }

        // 3. Fallback via PowerShell if netsh wildcard didn't match
        var deleteResult = await RunProcessAsync("powershell.exe",
            $"-NoProfile -Command \"Get-NetFirewallRule -DisplayName '{RulePrefix}*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue\"",
            cancellationToken);

        Logger.Log($"[FirewallService] RemoveFirewallRule (powershell fallback) result: ExitCode={deleteResult.ExitCode}");
        return true;
    }

    public async Task<bool> CheckFirewallRule(int? configuredPort = null, CancellationToken cancellationToken = default)
    {
        // 1. Try native Windows Firewall COM API (reads directly from in-memory cache, < 5ms)
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType != null)
            {
                dynamic policy = Activator.CreateInstance(policyType)!;
                dynamic rules = policy.Rules;
                var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (dynamic rule in rules)
                {
                    string name = rule.Name;
                    if (!string.IsNullOrEmpty(name) && name.StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        existingNames.Add(name);
                    }
                }

                if (configuredPort.HasValue)
                {
                    var expectedRuleNames = BuildExpectedRuleNames(configuredPort.Value).ToList();
                    if (existingNames.Count < expectedRuleNames.Count)
                    {
                        Logger.Log($"[FirewallService] CheckFirewallRule (COM): Rule count mismatch (found {existingNames.Count}, expected {expectedRuleNames.Count}).");
                        return false;
                    }

                    foreach (var expected in expectedRuleNames)
                    {
                        if (!existingNames.Contains(expected))
                        {
                            Logger.Log($"[FirewallService] CheckFirewallRule (COM): Missing rule '{expected}'.");
                            return false;
                        }
                    }

                    return true;
                }

                return existingNames.Count > 0;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[FirewallService] COM CheckFirewallRule failed, attempting fallback: {ex.Message}");
        }

        // 2. Fallback via PowerShell
        if (configuredPort.HasValue)
        {
            var expectedRuleNames = BuildExpectedRuleNames(configuredPort.Value);
            var expectedRulesArray = string.Join(", ", expectedRuleNames.Select(ruleName => $"'{EscapePowerShellSingleQuotedString(ruleName)}'"));
            var script = $"$expected = @({expectedRulesArray}); $existing = @(Get-NetFirewallRule -DisplayName '{RulePrefix}*' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty DisplayName); if ($existing.Count -ne $expected.Count) {{ exit 1 }}; foreach ($name in $expected) {{ if ($existing -notcontains $name) {{ exit 1 }} }}; exit 0";
            var strictResult = await RunProcessAsync("powershell.exe",
                $"-NoProfile -Command \"{script}\"",
                cancellationToken);

            Logger.Log($"[FirewallService] CheckFirewallRule(strict fallback) result: ExitCode={strictResult.ExitCode}, HasRules={strictResult.ExitCode == 0}");
            return strictResult.ExitCode == 0;
        }

        var result = await RunProcessAsync("powershell.exe",
            $"-NoProfile -Command \"$rules = Get-NetFirewallRule -DisplayName '{RulePrefix}*' -ErrorAction SilentlyContinue; if ($rules -and $rules.Count -gt 0) {{ exit 0 }} else {{ exit 1 }}\"",
            cancellationToken);

        Logger.Log($"[FirewallService] CheckFirewallRule (fallback) result: ExitCode={result.ExitCode}, HasRules={result.ExitCode == 0}");
        return result.ExitCode == 0;
    }

    /// <summary>
    /// Checks and automatically repairs Windows Firewall rules for LogonUI, consent.exe (UAC), CredentialUIBroker, and Host.
    /// If any rule is missing or broken, it cleans up and re-creates all required inbound and outbound rules silently.
    /// </summary>
    public async Task<bool> EnsureFirewallRulesAsync(int port, CancellationToken cancellationToken = default)
    {
        try
        {
            var isOk = await CheckFirewallRule(port, cancellationToken);
            if (isOk)
            {
                return true;
            }

            Logger.Log($"[FirewallService] One or more firewall rules missing for port {port}. Starting automatic repair...");
            await RemoveFirewallRule(cancellationToken);
            var success = await AddFirewallRule(port, cancellationToken);

            if (success)
            {
                Logger.Log($"[FirewallService] Firewall rules auto-repaired successfully for port {port}.");
                ActivityJournal.Record("network", "🛡️", "Firewall rules auto-repaired", $"Restored Windows Firewall rules for LogonUI, UAC, and Host on port {port}.", true);
            }
            else
            {
                Logger.LogWarning($"[FirewallService] Firewall auto-repair could not add all rules for port {port}.");
            }

            return success;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[FirewallService] Exception during firewall auto-repair on port {port}", ex);
            return false;
        }
    }

    private static string BuildRuleName(string protocol, string direction, string programPath, int port)
    {
        string fileName = Path.GetFileNameWithoutExtension(programPath);
        string appName = fileName.Equals("LogonUI", StringComparison.OrdinalIgnoreCase)
            ? "LogonUI Rule"
            : fileName.Equals("CredentialUIBroker", StringComparison.OrdinalIgnoreCase)
                ? "CredUIBroker Rule"
                : fileName.Equals("consent", StringComparison.OrdinalIgnoreCase)
                    ? "Consent Rule"
                    : "HostApp Rule";
        return $"{RulePrefix} - {protocol} - {direction} - {appName} - {port}+5353";
    }

    private static IEnumerable<string> BuildExpectedRuleNames(int port)
    {
        string logonUIPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "LogonUI.exe"
        );

        string credUIBrokerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "CredentialUIBroker.exe"
        );

        string consentPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "consent.exe"
        );

        string hostAppPath = Environment.ProcessPath ?? string.Empty;
        string[] programs = { logonUIPath, credUIBrokerPath, consentPath, hostAppPath };
        string[] directions = { "in", "out" };
        string[] protocols = { "TCP", "UDP" };

        foreach (var program in programs)
        {
            foreach (var protocol in protocols)
            {
                foreach (var direction in directions)
                {
                    yield return BuildRuleName(protocol, direction, program, port);
                }
            }
        }
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
    {
        var result = new ProcessResult();
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
                StandardErrorEncoding = Encoding.UTF8,
            };

            process = Process.Start(psi);
            if (process == null)
            {
                result.Error = "Process start failed";
                result.IsSuccess = false;
                return result;
            }

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

            await Task.WhenAll(outputTask, errorTask);

            result.Output = await outputTask;
            result.Error = await errorTask;
            result.ExitCode = process.ExitCode;
            result.IsSuccess = (process.ExitCode == 0);

            return result;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Operation canceled";
            result.IsSuccess = false;
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError("[FirewallService] RunProcess error", ex);
            result.Error = ex.Message;
            result.IsSuccess = false;
            return result;
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

    private class ProcessResult
    {
        public string Output { get; set; } = "";
        public string Error { get; set; } = "";
        public int ExitCode { get; set; }
        public bool IsSuccess { get; set; }
    }
}
