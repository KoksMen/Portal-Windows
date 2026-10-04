using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Portal.Host.Helpers;

public enum BackdropType
{
    None = 0,
    Mica = 2,
    Acrylic = 3,
    MicaAlt = 4
}

public enum DwmWindowCornerPreference
{
    Default = 0,
    DoNotRound = 1,
    Round = 2,
    RoundSmall = 3
}

/// <summary>
/// Provides Desktop Window Manager (DWM) interop for Windows 11 Fluent backdrops (Mica, Acrylic, Mica Alt),
/// dark mode caption rendering, and rounded corner preferences with safe Windows 10 fallbacks.
/// </summary>
public static class DwmBackdropHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaMicaEffect = 1029; // Undocumented Windows 11 21H2 (Build 22000)

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    /// <summary>
    /// Checks whether the current operating system is Windows 11 or newer (Build >= 22000).
    /// </summary>
    public static bool IsWindows11OrGreater =>
        Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;

    /// <summary>
    /// Checks whether the current operating system is Windows 11 22H2 or newer (Build >= 22621),
    /// which provides full native support for DWMWA_SYSTEMBACKDROP_TYPE (Mica, Acrylic, Mica Alt).
    /// </summary>
    public static bool IsWindows11_22H2OrGreater =>
        Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22621;

    /// <summary>
    /// Applies Immersive Dark Mode to the window title bar and caption buttons.
    /// Works on Windows 10 (1903+) and Windows 11.
    /// </summary>
    public static bool ApplyDarkMode(Window window, bool enableDark = true)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int dark = enableDark ? 1 : 0;
            return DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets native Windows 11 window corner rounding preference.
    /// </summary>
    public static bool SetCornerPreference(Window window, DwmWindowCornerPreference cornerPreference)
    {
        if (!IsWindows11OrGreater)
        {
            return false;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int pref = (int)cornerPreference;
            return DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extends DWM frame margins into the entire window client area (-1 margins).
    /// </summary>
    public static bool ExtendFrameIntoClientArea(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var margins = new Margins { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            return DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Applies the specified system backdrop (Mica, Acrylic, Mica Alt) to the window.
    /// Returns true if backdrop was applied successfully; false if running on Windows 10 or if DWM rejected it.
    /// </summary>
    public static bool ApplyBackdrop(Window window, BackdropType type)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        // Always enable immersive dark mode if possible
        ApplyDarkMode(window, true);

        if (!IsWindows11OrGreater)
        {
            // Windows 10 does not support Mica or Acrylic system backdrops
            return false;
        }

        SetCornerPreference(window, DwmWindowCornerPreference.Round);

        if (type == BackdropType.None)
        {
            if (IsWindows11_22H2OrGreater)
            {
                int disable = 1; // DWMSBT_DISABLE
                DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref disable, sizeof(int));
            }
            return false;
        }

        try
        {
            // Windows 11 22H2+ (Build >= 22621): Public DWMWA_SYSTEMBACKDROP_TYPE API
            if (IsWindows11_22H2OrGreater)
            {
                int backdropValue = (int)type;
                int hr = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropValue, sizeof(int));
                return hr == 0;
            }

            // Windows 11 21H2 (Build >= 22000 and < 22621): Mica via undocumented attribute
            if (type == BackdropType.Mica)
            {
                int micaVal = 1;
                int hr = DwmSetWindowAttribute(hwnd, DwmwaMicaEffect, ref micaVal, sizeof(int));
                return hr == 0;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
