using System.Runtime.InteropServices;
using Portal.Common;

namespace Portal.CredentialProvider.Services;

internal static class CursorRecoveryService
{
    private const int CURSOR_SHOWING = 0x00000001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ShowCursor(bool bShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    internal static void TriggerAfterUnlock(string reason)
    {
        // Recover immediately and then retry for a short period because
        // Winlogon/desktop transitions can override cursor visibility.
        ApplyRecovery($"immediate ({reason})");

        _ = Task.Run(async () =>
        {
            var delaysMs = new[] { 300, 700, 1400, 2200, 3200, 4600 };
            foreach (var delay in delaysMs)
            {
                await Task.Delay(delay).ConfigureAwait(false);
                ApplyRecovery($"retry +{delay}ms ({reason})");
            }
        });
    }

    private static void ApplyRecovery(string reason)
    {
        try
        {
            int showCount = int.MinValue;
            for (int i = 0; i < 64; i++)
            {
                showCount = ShowCursor(true);
                if (showCount >= 0)
                {
                    break;
                }
            }

            // Do not reload cursors via SPI_SETCURSORS here: it can reset custom user cursor schemes.
            var ci = new CURSORINFO { cbSize = (uint)Marshal.SizeOf<CURSORINFO>() };
            bool hasInfo = GetCursorInfo(ref ci);
            string visible = hasInfo && (ci.flags & CURSOR_SHOWING) != 0 ? "visible" : "hidden";

            Logger.Log($"[CursorRecoveryService] Cursor recovery applied ({reason}). ShowCursorCount={showCount}, Cursor={visible}, HasInfo={hasInfo}.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[CursorRecoveryService] Failed to recover cursor ({reason}): {ex.Message}");
        }
    }
}
