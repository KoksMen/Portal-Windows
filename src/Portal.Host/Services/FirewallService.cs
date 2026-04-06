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
        string logonUIPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "LogonUI.exe"
        );

        string credUIBrokerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "CredentialUIBroker.exe"
        );

        string hostAppPath = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrEmpty(hostAppPath))
        {
            Logger.LogError("[FirewallService] Cannot determine host app path.");
            return false;
        }

        string combinedPorts = string.Join(",", new[] { port.ToString(), "5353" }.Distinct(StringComparer.OrdinalIgnoreCase));
        string[] programs = { logonUIPath, credUIBrokerPath, hostAppPath };
        string[] directions = { "in", "out" };
        string[] protocols = { "TCP", "UDP" };

        var firewallRules = new List<string>();

        foreach (string program in programs)
        {
            foreach (string protocol in protocols)
            {
                foreach (string direction in directions)
                {
                    string portType = direction == "in" ? "localport" : "remoteport";
                    string ruleName = BuildRuleName(protocol, direction, program, port);

                    firewallRules.Add($"netsh advfirewall firewall add rule name=\"{ruleName}\" dir={direction} action=allow protocol={protocol} {portType}={combinedPorts} profile=any program=\"{program}\"");
                }
            }
        }

        // Use && so that if any rule fails to add, we know about it
        string command = string.Join(" && ", firewallRules);
        var result = await RunProcessAsync("cmd.exe", $"/c {command}", cancellationToken);

        Logger.Log($"[FirewallService] AddFirewallRule result: Success={result.IsSuccess}, ExitCode={result.ExitCode}");
        if (!string.IsNullOrWhiteSpace(result.Error))
            Logger.LogWarning($"[FirewallService] AddFirewallRule stderr: {result.Error}");

        return result.IsSuccess;
    }

    public async Task<bool> RemoveFirewallRule(CancellationToken cancellationToken = default)
    {
        // Use PowerShell to delete all rules matching our prefix.
        // This is port-agnostic, so old rules from different ports are cleaned up too.
        var deleteResult = await RunProcessAsync("powershell.exe",
            $"-NoProfile -Command \"Get-NetFirewallRule -DisplayName '{RulePrefix}*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue\"",
            cancellationToken);

        Logger.Log($"[FirewallService] RemoveFirewallRule result: ExitCode={deleteResult.ExitCode}");
        if (!string.IsNullOrWhiteSpace(deleteResult.Error))
            Logger.LogWarning($"[FirewallService] RemoveFirewallRule stderr: {deleteResult.Error}");

        // Always return true — even if no rules existed, that's fine
        return true;
    }

    public async Task<bool> CheckFirewallRule(int? configuredPort = null, CancellationToken cancellationToken = default)
    {
        if (configuredPort.HasValue)
        {
            var expectedRuleNames = BuildExpectedRuleNames(configuredPort.Value);
            var expectedRulesArray = string.Join(", ", expectedRuleNames.Select(ruleName => $"'{EscapePowerShellSingleQuotedString(ruleName)}'"));
            var script = $"$expected = @({expectedRulesArray}); $existing = @(Get-NetFirewallRule -DisplayName '{RulePrefix}*' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty DisplayName); if ($existing.Count -ne $expected.Count) {{ exit 1 }}; foreach ($name in $expected) {{ if ($existing -notcontains $name) {{ exit 1 }} }}; exit 0";
            var strictResult = await RunProcessAsync("powershell.exe",
                $"-NoProfile -Command \"{script}\"",
                cancellationToken);

            Logger.Log($"[FirewallService] CheckFirewallRule(strict) result: ExitCode={strictResult.ExitCode}, HasRules={strictResult.ExitCode == 0}");
            return strictResult.ExitCode == 0;
        }

        // Use PowerShell to check if ANY firewall rules with our prefix exist
        var result = await RunProcessAsync("powershell.exe",
            $"-NoProfile -Command \"$rules = Get-NetFirewallRule -DisplayName '{RulePrefix}*' -ErrorAction SilentlyContinue; if ($rules -and $rules.Count -gt 0) {{ exit 0 }} else {{ exit 1 }}\"",
            cancellationToken);

        Logger.Log($"[FirewallService] CheckFirewallRule result: ExitCode={result.ExitCode}, HasRules={result.ExitCode == 0}");
        return result.ExitCode == 0;
    }

    private static string BuildRuleName(string protocol, string direction, string programPath, int port)
    {
        string fileName = Path.GetFileNameWithoutExtension(programPath);
        string appName = fileName.Equals("LogonUI", StringComparison.OrdinalIgnoreCase)
            ? "LogonUI Rule"
            : fileName.Equals("CredentialUIBroker", StringComparison.OrdinalIgnoreCase)
                ? "CredUIBroker Rule"
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

        string hostAppPath = Environment.ProcessPath ?? string.Empty;
        string[] programs = { logonUIPath, credUIBrokerPath, hostAppPath };
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
