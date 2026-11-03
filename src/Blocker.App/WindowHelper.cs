using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Blocker.App;

public static class WindowHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void EnableDarkMode(Window window)
    {
        UpdateTitleBarTheme(window, ThemeManager.IsDark);
    }

    public static void UpdateTitleBarTheme(Window window, bool isDark)
    {
        if (window == null) return;

        void Apply()
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int useDarkMode = isDark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDarkMode, sizeof(int));
                }
            }
            catch
            {
                // Ignore if DWM is unavailable or unsupported on the OS
            }
        }

        if (window.IsLoaded)
        {
            Apply();
        }
        else
        {
            window.SourceInitialized += (s, e) => Apply();
        }
    }
}
