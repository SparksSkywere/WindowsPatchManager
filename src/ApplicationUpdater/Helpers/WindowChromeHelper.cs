using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ApplicationUpdater.Helpers;

/// <summary>
/// Forces Windows 10/11 title bar / caption / border into light or dark mode
/// (standard WPF windows ignore app theme for the non-client chrome).
/// </summary>
public static class WindowChromeHelper
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwaColorDefault = unchecked((int)0xFFFFFFFF);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void ApplyTheme(Window window, bool dark)
    {
        if (window is null) return;

        void apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                // Handle not ready yet — apply on SourceInitialized once
                void OnSourceInitialized(object? s, EventArgs e)
                {
                    window.SourceInitialized -= OnSourceInitialized;
                    ApplyToHwnd(new WindowInteropHelper(window).Handle, dark);
                }

                window.SourceInitialized += OnSourceInitialized;
                return;
            }

            ApplyToHwnd(hwnd, dark);
        }

        if (window.Dispatcher.CheckAccess())
            apply();
        else
            window.Dispatcher.Invoke(apply);
    }

    public static void ApplyToAllOpenWindows(bool dark)
    {
        if (Application.Current is null) return;
        foreach (Window w in Application.Current.Windows)
            ApplyTheme(w, dark);
    }

    private static void ApplyToHwnd(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var useDark = dark ? 1 : 0;
            // Prefer attribute 20 (Win10 20H1+); fall back to 19 on older builds
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));

            // Windows 11: paint caption + border to match app surfaces
            if (dark)
            {
                var caption = ColorToColorRef(Color.FromRgb(0x1E, 0x1E, 0x1E));
                var border = ColorToColorRef(Color.FromRgb(0x3A, 0x3A, 0x3A));
                var text = ColorToColorRef(Color.FromRgb(0xF3, 0xF3, 0xF3));
                DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
            }
            else
            {
                // Reset to system defaults on light mode
                var def = DwmwaColorDefault;
                DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref def, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref def, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref def, sizeof(int));
            }
        }
        catch
        {
            // DWM not available (remote session / older OS) — ignore
        }
    }

    /// <summary>COLORREF is 0x00BBGGRR.</summary>
    private static int ColorToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}
