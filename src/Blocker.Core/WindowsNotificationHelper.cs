using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Blocker.Core;

/// <summary>
/// Provides utilities to check and synchronize with Windows OS notification settings,
/// verify truthful window usability, and launch Windows Notification settings directly.
/// </summary>
public static class WindowsNotificationHelper
{
    private const string SankalpaNotificationRegPath = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Sankalpa";
    private const string GlobalNotificationRegPath = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";

    /// <summary>
    /// Test hook to simulate usable window check results deterministically in unit tests.
    /// </summary>
    public static Func<string, bool>? UsableWindowCheckOverride { get; set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// Checks whether the given browser has a USABLE window according to Shared Contract C-1:
    /// a visible top-level window of at least 100x100 owned by a process whose image path is inside the pinned install root.
    /// </summary>
    public static bool HasUsableWindow(string browserExe, IReadOnlyList<string>? pinnedRoots = null)
    {
        if (UsableWindowCheckOverride != null)
        {
            return UsableWindowCheckOverride(browserExe);
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        string normExe = browserExe.Trim();
        if (!normExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normExe += ".exe";
        }

        // Resolve pinned roots if not explicitly provided
        var effectiveRoots = pinnedRoots?.ToList() ?? GetPinnedRootsForBrowser(normExe);

        bool usableWindowFound = false;

        try
        {
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindow(hWnd) || !IsWindowVisible(hWnd))
                {
                    return true;
                }

                uint pid = 0;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid == 0)
                {
                    return true;
                }

                // Exclude dummy, tool, and IME window classes
                var sbClass = new StringBuilder(128);
                GetClassNameW(hWnd, sbClass, sbClass.Capacity);
                string cls = sbClass.ToString();

                if (cls.StartsWith("UAC_", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "UAC Input Indicator", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Touch Tooltip Window", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "CicLoaderWndClass", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "CiceroUIWndFrame", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "MSCTFIME UI", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Default IME", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "IME", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Static", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "GDI+ Hook Window Class", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "QTrayIconMessageWindowClass", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Verify process identity and image path
                string? imagePath = GetProcessImagePath(pid);
                if (string.IsNullOrEmpty(imagePath))
                {
                    return true;
                }

                string fileName = Path.GetFileName(imagePath);
                if (!string.Equals(fileName, normExe, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Verify image path is inside pinned install roots
                if (effectiveRoots.Count > 0)
                {
                    bool insidePinnedRoot = effectiveRoots.Any(root =>
                        imagePath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(imagePath, root, StringComparison.OrdinalIgnoreCase));

                    if (!insidePinnedRoot)
                    {
                        return true;
                    }
                }

                // Check window dimensions
                int w = 0, h = 0;
                bool isIconic = IsIconic(hWnd);
                if (isIconic)
                {
                    var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                    if (GetWindowPlacement(hWnd, ref wp))
                    {
                        w = wp.rcNormalPosition.Right - wp.rcNormalPosition.Left;
                        h = wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top;
                    }
                }
                else if (GetWindowRect(hWnd, out RECT r))
                {
                    w = r.Right - r.Left;
                    h = r.Bottom - r.Top;
                }

                var sbTitle = new StringBuilder(256);
                GetWindowTextW(hWnd, sbTitle, sbTitle.Capacity);
                bool hasTitle = sbTitle.Length > 0;

                if ((w >= 100 && h >= 100) || (isIconic && hasTitle))
                {
                    usableWindowFound = true;
                    return false; // Stop enumeration
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Fail safe on error
            return false;
        }

        return usableWindowFound;
    }

    private static string? GetProcessImagePath(uint pid)
    {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var sb = new StringBuilder(1024);
            int capacity = sb.Capacity;
            if (QueryFullProcessImageNameW(hProcess, 0, sb, ref capacity))
            {
                return sb.ToString();
            }
            return null;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static List<string> GetPinnedRootsForBrowser(string normExe)
    {
        var roots = new List<string>();
        try
        {
            var browserInfo = BrowserCatalog.SupportedBrowsers.FirstOrDefault(b =>
                string.Equals(b.ExeName, normExe, StringComparison.OrdinalIgnoreCase));

            if (browserInfo != null)
            {
                string[] standardPrefixes =
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                };

                foreach (var prefix in standardPrefixes)
                {
                    if (string.IsNullOrWhiteSpace(prefix)) continue;
                    foreach (var rel in browserInfo.RelativeExePaths)
                    {
                        string dir = Path.GetDirectoryName(Path.Combine(prefix, rel)) ?? string.Empty;
                        if (!string.IsNullOrEmpty(dir) && !roots.Contains(dir, StringComparer.OrdinalIgnoreCase))
                        {
                            roots.Add(dir);
                        }
                    }
                }
            }
        }
        catch { }

        return roots;
    }

    /// <summary>
    /// Checks whether notifications for Sankalpa are currently disabled by Windows
    /// either per-application in Windows Settings or via global system push notification settings.
    /// </summary>
    public static bool IsNotificationDisabledInWindows()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            // 1. Check if global toast notifications are disabled at the system level
            using (var globalKey = Registry.CurrentUser.OpenSubKey(GlobalNotificationRegPath))
            {
                if (globalKey != null)
                {
                    var toastEnabled = globalKey.GetValue("ToastEnabled");
                    if (toastEnabled is int intVal && intVal == 0)
                    {
                        return true;
                    }
                }
            }

            // 2. Check per-app notification setting for Sankalpa
            using (var appKey = Registry.CurrentUser.OpenSubKey(SankalpaNotificationRegPath))
            {
                if (appKey != null)
                {
                    var enabledVal = appKey.GetValue("Enabled");
                    if (enabledVal is int enabledInt && enabledInt == 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the Windows Settings page directly to Notifications & actions.
    /// </summary>
    public static bool OpenWindowsNotificationSettings()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo("ms-settings:notifications")
                {
                    UseShellExecute = true
                };
                Process.Start(psi);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Explicitly enables Windows notifications for Sankalpa in the current user registry.
    /// Sets Enabled = 1 and ShowInActionCenter = 1 under Notifications\Settings\Sankalpa,
    /// and ensures global push notifications ToastEnabled is not 0.
    /// </summary>
    public static bool EnableNotificationInWindows()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using (var appKey = Registry.CurrentUser.CreateSubKey(SankalpaNotificationRegPath, writable: true))
            {
                if (appKey != null)
                {
                    appKey.SetValue("Enabled", 1, RegistryValueKind.DWord);
                    appKey.SetValue("ShowInActionCenter", 1, RegistryValueKind.DWord);
                }
            }

            using (var pushKey = Registry.CurrentUser.CreateSubKey(GlobalNotificationRegPath, writable: true))
            {
                if (pushKey != null)
                {
                    var val = pushKey.GetValue("ToastEnabled");
                    if (val is int intVal && intVal == 0)
                    {
                        pushKey.SetValue("ToastEnabled", 1, RegistryValueKind.DWord);
                    }
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
