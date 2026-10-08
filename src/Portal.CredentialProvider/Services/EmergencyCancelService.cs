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
    private const int VK_LSHIFT = 0xA0; // Left Shift
    private const int VK_RSHIFT = 0xA1; // Right Shift
    private const int VK_SHIFT = 0x10; // Shift

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

    private static IntPtr AttachToActiveDesktop(IntPtr fallbackDesktop)
    {
        try
        {
            IntPtr hInput = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS);
            if (hInput != IntPtr.Zero)
            {
                if (SetThreadDesktop(hInput))
                {
                    return hInput;
                }
                CloseDesktop(hInput);
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

        return IntPtr.Zero;
    }

    private static IntPtr RefreshActiveDesktop(IntPtr currentHandle, IntPtr fallbackDesktop)
    {
        try
        {
            IntPtr hInput = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS);
            if (hInput != IntPtr.Zero)
            {
                if (hInput == currentHandle)
                {
                    // Desktop unchanged; close duplicate open handle
                    CloseDesktop(hInput);
                    return currentHandle;
                }

                if (SetThreadDesktop(hInput))
                {
                    if (currentHandle != IntPtr.Zero && currentHandle != fallbackDesktop)
                    {
                        CloseDesktop(currentHandle);
                    }
                    return hInput;
                }
                CloseDesktop(hInput);
            }
        }
        catch { }

        return currentHandle;
    }

    private static void MonitorLoop(object? state, IntPtr callerDesktop)
    {
        var token = state is CancellationToken ct ? ct : CancellationToken.None;

        IntPtr activeDesktopHandle = IntPtr.Zero;
        try
        {
            activeDesktopHandle = AttachToActiveDesktop(callerDesktop);

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

            int currentHoldMs = 0;
            bool triggered = false;
            int desktopReattachCounter = 0;
            int currentRetryHoldMs = 0;
            bool retryTriggered = false;
            DateTime lastRetryTriggerTime = DateTime.MinValue;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool hasActiveUnlock = PortalWinTile.HasActiveUnlockRequest;
                    int pollIntervalMs = hasActiveUnlock ? 15 : 30;

                    // Periodically verify active desktop attachment (~every 2-3 seconds)
                    if (++desktopReattachCounter % 75 == 0)
                    {
                        var newHandle = RefreshActiveDesktop(activeDesktopHandle, callerDesktop);
                        if (newHandle != activeDesktopHandle)
                        {
                            activeDesktopHandle = newHandle;
                        }
                    }

                    bool isCtrlDown = (GetAsyncKeyState(VK_LCONTROL) < 0) || (GetAsyncKeyState(VK_CONTROL) < 0) || (GetAsyncKeyState(VK_RCONTROL) < 0);
                    bool isAltDown = (GetAsyncKeyState(VK_LMENU) < 0) || (GetAsyncKeyState(VK_MENU) < 0) || (GetAsyncKeyState(VK_RMENU) < 0);
                    bool isShiftDown = (GetAsyncKeyState(VK_LSHIFT) < 0) || (GetAsyncKeyState(VK_SHIFT) < 0) || (GetAsyncKeyState(VK_RSHIFT) < 0);

                    // 1. Retry shortcut: Ctrl + Shift (without Alt)
                    bool isRetryDown = isCtrlDown && isShiftDown && !isAltDown;
                    if (isRetryDown)
                    {
                        currentRetryHoldMs += pollIntervalMs;
                        if (!retryTriggered && currentRetryHoldMs >= 30 && (DateTime.UtcNow - lastRetryTriggerTime).TotalMilliseconds > 800)
                        {
                            retryTriggered = true;
                            lastRetryTriggerTime = DateTime.UtcNow;
                            Logger.LogWarning("[EmergencyCancelService] Retry shortcut detected! (Ctrl + Shift pressed).");
                            try
                            {
                                PortalWinTile.TryTriggerRetryShortcut("shortcut_ctrl_shift");
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError("[EmergencyCancelService] Error during retry shortcut trigger (Ctrl + Shift)", ex);
                            }
                        }
                    }
                    else
                    {
                        currentRetryHoldMs = 0;
                        retryTriggered = false;
                    }

                    // 2. Emergency rollback shortcut: Ctrl + Alt (without Shift)
                    if (emergencyCancelEnabled && isCtrlDown && isAltDown && !isShiftDown)
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

                    // 3. Any typing key (letters, digits, space, etc.) without Ctrl or Alt:
                    // Only poll full typing set when a remote unlock request is actually active!
                    if (hasActiveUnlock && !isCtrlDown && !isAltDown && IsAnyTypingKeyPressed())
                    {
                        PortalWinTile.OnTypingKeyDetected();
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
        finally
        {
            if (activeDesktopHandle != IntPtr.Zero && activeDesktopHandle != callerDesktop)
            {
                try
                {
                    CloseDesktop(activeDesktopHandle);
                }
                catch { }
            }
        }
    }

    private static bool IsAnyTypingKeyPressed()
    {
        // 0-9 (0x30 - 0x39)
        for (int vk = 0x30; vk <= 0x39; vk++)
        {
            if (GetAsyncKeyState(vk) < 0) return true;
        }

        // A-Z (0x41 - 0x5A)
        for (int vk = 0x41; vk <= 0x5A; vk++)
        {
            if (GetAsyncKeyState(vk) < 0) return true;
        }

        // Numpad 0-9 (0x60 - 0x69)
        for (int vk = 0x60; vk <= 0x69; vk++)
        {
            if (GetAsyncKeyState(vk) < 0) return true;
        }

        // Space (0x20), Backspace (0x08)
        if (GetAsyncKeyState(0x20) < 0) return true;
        if (GetAsyncKeyState(0x08) < 0) return true;

        // OEM and punctuation keys: semicolon, plus, comma, minus, period, slash, tilde, brackets, quotes
        for (int vk = 0xBA; vk <= 0xC0; vk++)
        {
            if (GetAsyncKeyState(vk) < 0) return true;
        }

        for (int vk = 0xDB; vk <= 0xDF; vk++)
        {
            if (GetAsyncKeyState(vk) < 0) return true;
        }

        return false;
    }
}
