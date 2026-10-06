using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Portal.Common.Helpers;

namespace Portal.Common.Services;

public sealed class ProviderGuardState
{
    public int CrashCount { get; set; }
    public DateTime? LastStartupAttemptUtc { get; set; }
    public DateTime? LastSuccessfulInitUtc { get; set; }
    public bool IsDisabledByGuard { get; set; }
    public string? DisarmReason { get; set; }
    public DateTime? DisarmedAtUtc { get; set; }
}

/// <summary>
/// Fail-Safe Lockout Guard:
/// Protects against logonui.exe boot-loops or crashes by tracking consecutive initialization failures.
/// If 2 or more crashes occur within a 90-second window during logonui startup, the guard automatically
/// unhooks the Credential Provider GUID from Windows registry, restoring standard Windows password/PIN tiles.
/// </summary>
public static class FailSafeLockoutGuard
{
    private const string ProviderGuid = "{4F507F6A-5A02-4F19-86B3-1C04F0E8C2E5}";
    private const string CredProvRegPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers";
    private static readonly object Sync = new();

    private static string GuardFilePath => Path.Combine(PortalStoragePaths.RootDirectory, "provider_guard.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static ProviderGuardState LoadState()
    {
        lock (Sync)
        {
            try
            {
                if (File.Exists(GuardFilePath))
                {
                    var json = File.ReadAllText(GuardFilePath);
                    return JsonSerializer.Deserialize<ProviderGuardState>(json, JsonOptions) ?? new ProviderGuardState();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[FailSafeGuard] Could not read guard state: {ex.Message}");
            }

            return new ProviderGuardState();
        }
    }

    public static void SaveState(ProviderGuardState state)
    {
        lock (Sync)
        {
            try
            {
                var json = JsonSerializer.Serialize(state, JsonOptions);
                File.WriteAllText(GuardFilePath, json);
                SecurityHardeningHelper.HardenFilePermissions(GuardFilePath);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[FailSafeGuard] Could not save guard state: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Invoked upon entry of Credential Provider (IsUsageScenarioSupported).
    /// Returns true if execution is permitted, or false if the guard tripped and the provider was safely disarmed.
    /// </summary>
    public static bool CheckAndRecordStartup()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        lock (Sync)
        {
            var state = LoadState();

            if (state.IsDisabledByGuard)
            {
                Logger.LogWarning($"[FailSafeGuard] Provider is disarmed due to earlier safety trip: '{state.DisarmReason}' at {state.DisarmedAtUtc}. Blocking tile appearance.");
                SafelyDeregisterFromRegistry();
                return false;
            }

            var now = DateTime.UtcNow;

            // Check if there was an in-flight startup attempt that never finished successfully within the last 90 seconds
            if (state.LastStartupAttemptUtc.HasValue)
            {
                var timeSinceLastAttempt = now - state.LastStartupAttemptUtc.Value;
                var hadUnfinishedCrash = !state.LastSuccessfulInitUtc.HasValue || state.LastSuccessfulInitUtc.Value < state.LastStartupAttemptUtc.Value;

                if (hadUnfinishedCrash && timeSinceLastAttempt.TotalSeconds < 90)
                {
                    state.CrashCount++;
                    Logger.LogWarning($"[FailSafeGuard] Incomplete startup detected within {timeSinceLastAttempt.TotalSeconds:F0}s. Consecutive crash count: {state.CrashCount}");

                    if (state.CrashCount >= 2)
                    {
                        // Trip guard!
                        state.IsDisabledByGuard = true;
                        state.DisarmReason = $"Multiple consecutive startup crashes detected in logonui.exe (count={state.CrashCount}).";
                        state.DisarmedAtUtc = now;
                        SaveState(state);

                        SafelyDeregisterFromRegistry();
                        ActivityJournal.Record(
                            "system",
                            "🚨",
                            "Fail-Safe Guard activated",
                            "Credential Provider was automatically unhooked from Windows logon to prevent system lockout after repeated startup crashes.",
                            false);

                        Logger.LogError($"[FailSafeGuard] GUARD TRIPPED! Unhooked provider GUID {ProviderGuid} from registry to preserve standard Windows password login.");
                        return false;
                    }
                }
                else if (timeSinceLastAttempt.TotalSeconds >= 90)
                {
                    // Decay old crash counters
                    state.CrashCount = 0;
                }
            }

            state.LastStartupAttemptUtc = now;
            SaveState(state);
            return true;
        }
    }

    /// <summary>
    /// Marks initialization as completed and stable. Resets consecutive crash count.
    /// </summary>
    public static void RecordSuccessfulInitialization()
    {
        lock (Sync)
        {
            try
            {
                var state = LoadState();
                state.CrashCount = 0;
                state.LastSuccessfulInitUtc = DateTime.UtcNow;
                SaveState(state);
                Logger.Log("[FailSafeGuard] Initialization marked successful. Crash counter reset to 0.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[FailSafeGuard] Failed to record successful initialization: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Resets the guard, clearing any lockout condition so that the user or admin can re-arm the provider.
    /// </summary>
    public static void ResetGuard()
    {
        lock (Sync)
        {
            try
            {
                var state = new ProviderGuardState
                {
                    CrashCount = 0,
                    IsDisabledByGuard = false,
                    DisarmReason = null,
                    DisarmedAtUtc = null,
                    LastStartupAttemptUtc = null,
                    LastSuccessfulInitUtc = DateTime.UtcNow
                };
                SaveState(state);
                Logger.Log("[FailSafeGuard] Guard state reset successfully.");
            }
            catch (Exception ex)
            {
                Logger.LogError("[FailSafeGuard] Failed to reset guard", ex);
            }
        }
    }

    private static void SafelyDeregisterFromRegistry()
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree($@"{CredProvRegPath}\{ProviderGuid}", false);
            Logger.Log($"[FailSafeGuard] Removed {ProviderGuid} from {CredProvRegPath}.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[FailSafeGuard] Could not remove registry key: {ex.Message}");
        }
    }
}
