using System;
using System.Collections.Generic;

namespace Blocker.Core;

/// <summary>
/// Matching and filtering logic for allowed applications and genuine top-level application windows.
/// Filters out system helper processes, toasts, tooltips, IME windows, and tray popups.
/// </summary>
public static class ZenLaunchMatcher
{
    private static readonly HashSet<string> ExcludedWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "UAC Input Indicator",
        "Touch Tooltip Window",
        "CicLoaderWndClass",
        "CiceroUIWndFrame",
        "MSCTFIME UI",
        "Default IME",
        "IME",
        "Static",
        "GDI+ Hook Window Class",
        "QTrayIconMessageWindowClass",
        "ToastWnd",
        "tooltips_class32",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
        "MultitaskingViewFrame"
    };

    /// <summary>
    /// Checks whether an application is permitted based on its process/executable name
    /// and its optional pinned installation root directory.
    /// </summary>
    public static bool IsAppAllowed(
        string? processName,
        string? imagePath,
        IReadOnlyCollection<string> allowedProcessNames,
        IReadOnlyDictionary<string, string>? allowedAppRoots)
    {
        if (string.IsNullOrWhiteSpace(processName) || allowedProcessNames == null || allowedProcessNames.Count == 0)
        {
            return false;
        }

        string normProc = ProcessMatcher.NormalizeProcessName(processName);
        bool matches = false;

        foreach (var allowed in allowedProcessNames)
        {
            if (string.IsNullOrWhiteSpace(allowed)) continue;
            string normAllowed = ProcessMatcher.NormalizeProcessName(allowed);
            if (string.Equals(normProc, normAllowed, StringComparison.OrdinalIgnoreCase))
            {
                matches = true;
                break;
            }
        }

        if (!matches)
        {
            return false;
        }

        // Check path pinning if configured for this application
        if (allowedAppRoots != null && allowedAppRoots.TryGetValue(normProc, out var rootDir) && !string.IsNullOrWhiteSpace(rootDir))
        {
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                return ProtectedProcesses.IsPathInsideDirectory(imagePath, rootDir);
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether a window has the characteristics of a genuine top-level application window,
    /// filtering out toasts, tooltips, IME windows, UAC banners, and helper overlays.
    /// </summary>
    public static bool IsRealAppWindow(
        string? className,
        int width,
        int height,
        bool isIconic,
        bool hasTitle)
    {
        if (!string.IsNullOrWhiteSpace(className))
        {
            string trimClass = className.Trim();
            if (trimClass.StartsWith("UAC_", StringComparison.OrdinalIgnoreCase) ||
                ExcludedWindowClasses.Contains(trimClass))
            {
                return false;
            }
        }

        if (isIconic)
        {
            // A minimized window may have 0x0 active rect, but must have a title or standard restored dimensions
            return hasTitle || (width >= ZenLaunchConstants.MinWindowDimension && height >= ZenLaunchConstants.MinWindowDimension);
        }

        // Active non-iconic window must meet minimum dimensions
        return width >= ZenLaunchConstants.MinWindowDimension && height >= ZenLaunchConstants.MinWindowDimension;
    }

    /// <summary>
    /// Checks if a foreground window is considered neutral (e.g., our own HUD/shield/dialog, or trusted OS helper).
    /// Focus on a neutral window should not cause the mini-HUD to hide or Zen shield to restore.
    /// </summary>
    public static bool IsNeutralWindow(
        bool isOwnProcess,
        string? imagePath,
        string? processName)
    {
        if (isOwnProcess)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(imagePath) && SystemHelperGuard.IsTrusted(imagePath))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            string norm = ProcessMatcher.NormalizeProcessName(processName);
            if (string.Equals(norm, "snippingtool", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(norm, "screenclippinghost", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(imagePath) || ProtectedProcesses.IsProtected(norm, imagePath);
            }
        }

        return false;
    }
}
