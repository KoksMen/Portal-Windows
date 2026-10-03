using System;
using System.Runtime.InteropServices;
using System.Threading;
using Portal.Common;
using Portal.Common.Helpers;

namespace Portal.CredentialProvider.Services;

/// <summary>
/// Monitors physical keyboard state for the emergency rollback shortcut
/// (by default holding Left Ctrl + Left Alt). When triggered, immediately
/// aborts any in-flight unlock requests, severs network/bluetooth connections,
/// clears pending credentials, and resets tile UI.
/// </summary>
public static class EmergencyCancelService
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // Win32 Virtual Key Codes
    private const int VK_LCONTROL = 0xA2;
    private const int VK_LMENU = 0xA4; // Left Alt

    private static readonly object _lock = new();
    private static Thread? _monitorThread;
    private static CancellationTokenSource? _cts;
    private static bool _isRunning;

    public static void Start()
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
            _cts = new CancellationTokenSource();
            _monitorThread = new Thread(MonitorLoop)
            {
                IsBackground = true,
                Name = "EmergencyCancelMonitor",
                Priority = ThreadPriority.AboveNormal
            };
            _monitorThread.Start(_cts.Token);
            Logger.Log("[EmergencyCancelService] Started keyboard monitor for emergency rollback.");
        }
    }

    public static void Stop()
    {
        lock (_lock)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }
            catch { }
            _cts = null;
            _monitorThread = null;
            Logger.Log("[EmergencyCancelService] Stopped keyboard monitor.");
        }
    }

    private static void MonitorLoop(object? state)
    {
        var token = state is CancellationToken ct ? ct : CancellationToken.None;

        int holdDurationMs = 350;
        try
        {
            var config = PortalWinConfig.Load();
            if (!config.EmergencyCancelEnabled)
            {
                Logger.Log("[EmergencyCancelService] Emergency cancel disabled in configuration.");
                return;
            }
            holdDurationMs = Math.Max(100, config.EmergencyCancelHoldDurationMs);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[EmergencyCancelService] Failed to load config, using default {holdDurationMs}ms: {ex.Message}");
        }

        const int pollIntervalMs = 30;
        int currentHoldMs = 0;
        bool triggered = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                bool isLeftCtrlDown = (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0;
                bool isLeftAltDown = (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0;

                if (isLeftCtrlDown && isLeftAltDown)
                {
                    currentHoldMs += pollIntervalMs;

                    if (currentHoldMs >= holdDurationMs)
                    {
                        if (!triggered)
                        {
                            triggered = true;
                            Logger.LogWarning($"[EmergencyCancelService] Emergency rollback shortcut detected! Held for {currentHoldMs}ms.");
                            try
                            {
                                PortalWinTile.TriggerEmergencyRollback();
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError("[EmergencyCancelService] Error during emergency rollback trigger", ex);
                            }
                        }
                    }
                }
                else
                {
                    currentHoldMs = 0;
                    triggered = false;
                }

                Thread.Sleep(pollIntervalMs);
            }
            catch (ThreadAbortException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError("[EmergencyCancelService] Error in monitor loop", ex);
                Thread.Sleep(500);
            }
        }
    }
}
