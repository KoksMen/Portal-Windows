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
    [DllImport("user32.dll", SetLastError = true)]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // Win32 Virtual Key Codes
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_CONTROL = 0x11;
    private const int VK_LMENU = 0xA4; // Left Alt
    private const int VK_RMENU = 0xA5; // Right Alt
    private const int VK_MENU = 0x12; // Alt
    private const int VK_SPACE = 0x20; // Spacebar

    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_WRITEOBJECTS = 0x0080;

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

            IntPtr callerDesktop = IntPtr.Zero;
            try
            {
                callerDesktop = GetThreadDesktop(GetCurrentThreadId());
            }
            catch { }

            _monitorThread = new Thread(state => MonitorLoop(state, callerDesktop))
            {
                IsBackground = true,
                Name = "EmergencyCancelMonitor",
                Priority = ThreadPriority.AboveNormal
            };
            _monitorThread.Start(_cts.Token);
            Logger.Log($"[EmergencyCancelService] Started keyboard monitor for emergency rollback. CallerDesktop={callerDesktop}");
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

    private static void TryAttachToActiveDesktop(IntPtr fallbackDesktop)
    {
        try
        {
            IntPtr hInput = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS);
            if (hInput != IntPtr.Zero)
            {
                SetThreadDesktop(hInput);
                CloseDesktop(hInput);
                return;
            }
        }
        catch { }

        if (fallbackDesktop != IntPtr.Zero)
        {
            try
            {
                SetThreadDesktop(fallbackDesktop);
            }
            catch { }
        }
    }

    private static void MonitorLoop(object? state, IntPtr callerDesktop)
    {
        var token = state is CancellationToken ct ? ct : CancellationToken.None;

        TryAttachToActiveDesktop(callerDesktop);

        bool emergencyCancelEnabled = true;
        int holdDurationMs = 0;
        try
        {
            var config = PortalWinConfig.Load();
            emergencyCancelEnabled = config.EmergencyCancelEnabled;
            holdDurationMs = Math.Max(0, config.EmergencyCancelHoldDurationMs);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[EmergencyCancelService] Failed to load config, using default {holdDurationMs}ms: {ex.Message}");
        }

        const int pollIntervalMs = 10;
        int currentHoldMs = 0;
        bool triggered = false;
        int desktopReattachCounter = 0;
        bool wasSpaceDown = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (++desktopReattachCounter % 50 == 0) // periodically re-verify active desktop attachment
                {
                    TryAttachToActiveDesktop(callerDesktop);
                }

                bool isCtrlDown = (GetAsyncKeyState(VK_LCONTROL) < 0) || (GetAsyncKeyState(VK_CONTROL) < 0) || (GetAsyncKeyState(VK_RCONTROL) < 0);
                bool isAltDown = (GetAsyncKeyState(VK_LMENU) < 0) || (GetAsyncKeyState(VK_MENU) < 0) || (GetAsyncKeyState(VK_RMENU) < 0);
                bool isSpaceDown = (GetAsyncKeyState(VK_SPACE) < 0);

                if (isSpaceDown && !wasSpaceDown && !isCtrlDown && !isAltDown)
                {
                    try
                    {
                        PortalWinTile.TryTriggerSpaceRetry();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("[EmergencyCancelService] Error during space retry trigger", ex);
                    }
                }
                wasSpaceDown = isSpaceDown;

                if (emergencyCancelEnabled && isCtrlDown && isAltDown)
                {
                    currentHoldMs += pollIntervalMs;

                    if (currentHoldMs >= holdDurationMs)
                    {
                        if (!triggered)
                        {
                            triggered = true;
                            Logger.LogWarning("[EmergencyCancelService] Emergency rollback shortcut detected! (Ctrl + Alt pressed).");
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
                Thread.Sleep(200);
            }
        }
    }
}
