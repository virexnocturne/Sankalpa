using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Blocker.Core;

/// <summary>
/// Provides utilities to check and synchronize with Windows OS notification settings
/// and launch Windows Notification settings directly for user configuration.
/// </summary>
public static class WindowsNotificationHelper
{
    private const string SankalpaNotificationRegPath = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Sankalpa";
    private const string GlobalNotificationRegPath = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";

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
}
