using System;
using System.Collections.Generic;

namespace Blocker.Core;

/// <summary>
/// Implements the exact allowed-application predicate for Zen Focus mode.
/// Adheres to Shared Contract C-3: single allowed-app rule matching service enforcement.
/// Watchdog must never be more permissive or less permissive than this predicate.
/// </summary>
public static class ZenLaunchMatcher
{
    public static bool IsAppAllowed(
        string processName,
        string exeName,
        IEnumerable<string>? allowedApps,
        string? imagePath,
        IReadOnlyDictionary<string, string>? allowedAppRoots)
    {
        string normProc = ProcessMatcher.NormalizeProcessName(processName);
        string normExe = ProcessMatcher.NormalizeProcessName(exeName);

        // Always allow Snipping Tool and Screen Clipping Host during meditation, verified against system folders if imagePath provided
        if (string.Equals(normProc, "screenclippinghost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normExe, "screenclippinghost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normProc, "snippingtool", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normExe, "snippingtool", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return true;
            }
            return ProtectedProcesses.IsProtected(normProc, imagePath) || ProtectedProcesses.IsProtected(normExe, imagePath);
        }

        if (allowedApps == null)
        {
            return false;
        }

        foreach (var allowed in allowedApps)
        {
            if (string.IsNullOrWhiteSpace(allowed)) continue;

            string normAllowed = ProcessMatcher.NormalizeProcessName(allowed);
            bool nameMatch = (!string.IsNullOrEmpty(normProc) && string.Equals(normProc, normAllowed, StringComparison.OrdinalIgnoreCase)) ||
                             (!string.IsNullOrEmpty(normExe) && string.Equals(normExe, normAllowed, StringComparison.OrdinalIgnoreCase));

            if (nameMatch)
            {
                if (allowedAppRoots != null &&
                    allowedAppRoots.TryGetValue(normAllowed, out var rootDir) &&
                    !string.IsNullOrWhiteSpace(rootDir))
                {
                    if (!string.IsNullOrWhiteSpace(imagePath))
                    {
                        if (ProtectedProcesses.IsPathInsideDirectory(imagePath, rootDir))
                        {
                            return true;
                        }
                        // Executable path does not match the pinned root
                        continue;
                    }
                }
                return true;
            }
        }

        return false;
    }
}
