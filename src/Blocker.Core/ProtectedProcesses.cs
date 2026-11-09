using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Blocker.Core;

public static class ProtectedProcesses
{
    private static readonly string[] WellKnown =
    [
        "explorer",
        "csrss",
        "winlogon",
        "wininit",
        "services",
        "lsass",
        "svchost",
        "system",
        "smss",
        "dwm",
        "taskmgr",
        "screenclippinghost",
        "snippingtool",
        "audiodg",
        "blocker",
        "blocker.service",
        "sankalpa",
        "sankalpa.service"
    ];

    private static readonly HashSet<string> WellKnownSet = new(WellKnown, StringComparer.OrdinalIgnoreCase);
    private static readonly string? OwnProcessName;
    private static readonly string? OwnNormalizedProcessName;
    private static readonly IReadOnlyList<string> CachedNames;

    private static readonly string WinDir = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private static readonly string Sys32Dir = Path.Combine(WinDir, "System32");
    private static readonly string SysAppsDir = Path.Combine(WinDir, "SystemApps");
    private static readonly string WinAppsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

    private static readonly HashSet<string> Sys32Processes = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "winlogon", "wininit", "services", "lsass", "svchost", "smss", "dwm", "taskmgr", "audiodg"
    };

    private static readonly HashSet<string> OwnProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sankalpa", "sankalpa.service", "blocker", "blocker.service", "blocker.nativehost", "sankalpa-cli"
    };

    private static readonly List<string> TrustedProductDirs = InitializeTrustedProductDirs();

    private static List<string> InitializeTrustedProductDirs()
    {
        var dirs = new List<string>();
        try
        {
            string baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                dirs.Add(Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }
        catch { }

        try
        {
            string? procPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(procPath))
            {
                string? dir = Path.GetDirectoryName(procPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    dirs.Add(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
            }
        }
        catch { }

        try
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf))
            {
                dirs.Add(Path.Combine(pf, "Sankalpa"));
            }
        }
        catch { }

        try
        {
            string pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pfX86))
            {
                dirs.Add(Path.Combine(pfX86, "Sankalpa"));
            }
        }
        catch { }

        try
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localApp))
            {
                dirs.Add(Path.Combine(localApp, "Sankalpa"));
                dirs.Add(Path.Combine(localApp, "Programs", "Sankalpa"));
            }
        }
        catch { }

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static ProtectedProcesses()
    {
        OwnProcessName = GetCurrentProcessName();
        OwnNormalizedProcessName = !string.IsNullOrEmpty(OwnProcessName)
            ? ProcessMatcher.NormalizeProcessName(OwnProcessName)
            : null;

        var list = new List<string>(WellKnown);
        if (!string.IsNullOrWhiteSpace(OwnProcessName) && !list.Contains(OwnProcessName, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(OwnProcessName);
        }
        CachedNames = list.AsReadOnly();
    }

    public static IReadOnlyList<string> Names => CachedNames;

    public static bool Contains(string? processName) => IsProtected(processName);

    public static bool IsProtected(string? processName)
    {
        string normalized = ProcessMatcher.NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        if (WellKnownSet.Contains(normalized))
        {
            return true;
        }

        if (OwnNormalizedProcessName != null &&
            string.Equals(normalized, OwnNormalizedProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Path-pinned protection check. Verifies process name AND checks image path against canonical directories.
    /// If imagePath is null or empty, falls back gracefully to IsProtected(processName).
    /// </summary>
    public static bool IsProtected(string? processName, string? imagePath)
    {
        string normalized = ProcessMatcher.NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        // System kernel process (PID 4) has no image path
        if (string.Equals(normalized, "system", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return IsProtected(processName);
        }

        string full;
        try
        {
            full = Path.GetFullPath(imagePath);
        }
        catch
        {
            return false;
        }

        string fileWithoutExt = Path.GetFileNameWithoutExtension(full);
        string dir = Path.GetDirectoryName(full) ?? "";

        // Verify that the executable file name matches the claimed process name
        if (!string.Equals(fileWithoutExt, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 1. Windows system binaries
        if (string.Equals(normalized, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            return dir.Equals(WinDir, StringComparison.OrdinalIgnoreCase);
        }

        if (Sys32Processes.Contains(normalized))
        {
            return dir.Equals(Sys32Dir, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(normalized, "snippingtool", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "screenclippinghost", StringComparison.OrdinalIgnoreCase))
        {
            return dir.Equals(Sys32Dir, StringComparison.OrdinalIgnoreCase) ||
                   dir.StartsWith(SysAppsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   dir.StartsWith(WinAppsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   SystemHelperGuard.IsTrusted(full);
        }

        // 2. Product binaries (Sankalpa, Blocker)
        if (IsOwnProcess(normalized, full))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates whether a process is an authentic Sankalpa / Blocker product binary located inside a trusted install directory.
    /// </summary>
    public static bool IsOwnProcess(string? processName, string? imagePath)
    {
        string normalized = ProcessMatcher.NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        bool isOwnName = OwnProcessNames.Contains(normalized) ||
                         (OwnNormalizedProcessName != null && string.Equals(normalized, OwnNormalizedProcessName, StringComparison.OrdinalIgnoreCase));

        if (!isOwnName)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return true;
        }

        string full;
        try
        {
            full = Path.GetFullPath(imagePath);
        }
        catch
        {
            return false;
        }

        string dir = Path.GetDirectoryName(full) ?? "";
        foreach (var trustedDir in TrustedProductDirs)
        {
            if (dir.Equals(trustedDir, StringComparison.OrdinalIgnoreCase) ||
                dir.StartsWith(trustedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a file path is located inside the specified directory (canonicalized).
    /// </summary>
    public static bool IsPathInsideDirectory(string filePath, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        try
        {
            string fullFilePath = Path.GetFullPath(filePath);
            string fullDirPath = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return fullFilePath.StartsWith(fullDirPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? GetCurrentProcessName()
    {
        try
        {
            return Process.GetCurrentProcess().ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
