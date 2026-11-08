using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Blocker.Core;
using Microsoft.Win32;

namespace Blocker.App;

public static class InstalledAppScanner
{
    private static readonly HashSet<string> ExcludedExePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "unins", "uninstall", "helper", "crashpad", "update", "installer", "setup", "elevate",
        "notification_helper", "vcredist", "dxsetup", "touchup", "dotnet", "vc_redist"
    };

    public static bool IsAppInstalled(string exeName)
    {
        return Blocker.Core.InstalledAppScanner.IsAppInstalled(exeName);
    }

    public static List<AppPickerItem> ScanRunningProcessesOnly()
    {
        var rawItems = new List<AppPickerItem>();
        ScanRunningProcesses(rawItems);
        return DeduplicateAndCategorize(rawItems);
    }

    public static List<AppPickerItem> ScanAll()
    {
        var rawItems = new List<AppPickerItem>();

        // 1. Scan Windows Uninstall Registry (HKLM 64-bit, HKLM 32-bit, HKCU 64-bit, and HKCU 32-bit)
        ScanUninstallRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, rawItems);
        ScanUninstallRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, rawItems);
        ScanUninstallRegistry(RegistryHive.CurrentUser, RegistryView.Registry64, rawItems);
        ScanUninstallRegistry(RegistryHive.CurrentUser, RegistryView.Registry32, rawItems);

        // 2. Scan App Paths Registry (registered application executable aliases)
        ScanAppPaths(RegistryHive.LocalMachine, RegistryView.Registry64, rawItems);
        ScanAppPaths(RegistryHive.CurrentUser, RegistryView.Default, rawItems);

        // 3. Scan standard Program Files and AppData Program directories
        ScanProgramFilesDirectories(rawItems);

        // 4. Scan Start Menu Shortcuts (.lnk files)
        ScanStartMenuShortcuts(rawItems);

        // 5. Scan currently running processes (with active windows)
        ScanRunningProcesses(rawItems);

        // 6. Deduplicate by normalized ExeName and classify into categories
        return DeduplicateAndCategorize(rawItems);
    }

    private static void ScanUninstallRegistry(RegistryHive hive, RegistryView view, List<AppPickerItem> items)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstallKey == null) return;

            foreach (string subKeyName in uninstallKey.GetSubKeyNames())
            {
                try
                {
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    // Skip Windows internal system components and hotfixes/updates
                    object? sysComp = subKey.GetValue("SystemComponent");
                    if (sysComp is int sysInt && sysInt == 1) continue;
                    if (subKey.GetValue("ParentKeyName") != null) continue;

                    string? displayName = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)) continue;

                    string? displayIcon = subKey.GetValue("DisplayIcon") as string;
                    string? installLocation = subKey.GetValue("InstallLocation") as string;

                    string? resolvedExePath = null;

                    // Try to resolve exe from DisplayIcon
                    if (!string.IsNullOrWhiteSpace(displayIcon))
                    {
                        string cleanIcon = displayIcon.Trim('"', ' ');
                        int commaIdx = cleanIcon.IndexOf(',');
                        if (commaIdx > 0)
                        {
                            cleanIcon = cleanIcon.Substring(0, commaIdx).Trim('"', ' ');
                        }

                        if (cleanIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(cleanIcon))
                        {
                            resolvedExePath = cleanIcon;
                        }
                    }

                    // Try to resolve exe from InstallLocation
                    if (resolvedExePath == null && !string.IsNullOrWhiteSpace(installLocation))
                    {
                        string cleanLoc = installLocation.Trim('"', ' ');
                        if (Directory.Exists(cleanLoc))
                        {
                            resolvedExePath = FindMainExeInDirectory(cleanLoc);
                        }
                    }

                    // Try to resolve exe from UninstallString
                    if (resolvedExePath == null)
                    {
                        string? uninst = subKey.GetValue("UninstallString") as string;
                        if (!string.IsNullOrWhiteSpace(uninst))
                        {
                            string cleanUninst = uninst.Trim('"', ' ');
                            int exeIdx = cleanUninst.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                            if (exeIdx > 0)
                            {
                                string uninstExe = cleanUninst.Substring(0, exeIdx + 4).Trim('"', ' ');
                                string? uninstDir = Path.GetDirectoryName(uninstExe);
                                if (!string.IsNullOrEmpty(uninstDir) && Directory.Exists(uninstDir))
                                {
                                    resolvedExePath = FindMainExeInDirectory(uninstDir);
                                }
                            }
                        }
                    }

                    if (resolvedExePath != null && File.Exists(resolvedExePath))
                    {
                        string exeName = Path.GetFileName(resolvedExePath);
                        if (IsExcludedExe(exeName)) continue;

                        bool isSystem = AppPickerLogic.IsSystemProcess(resolvedExePath, displayName, true);

                        items.Add(new AppPickerItem
                        {
                            DisplayName = displayName.Trim(),
                            ExeName = AppPickerLogic.NormalizeExeName(exeName),
                            Path = resolvedExePath,
                            Category = isSystem ? AppPickerLogic.CatSystem : AppPickerLogic.CatInstalled,
                            IsSystemProcess = isSystem
                        });
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static void ScanAppPaths(RegistryHive hive, RegistryView view, List<AppPickerItem> items)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var appPathsKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (appPathsKey == null) return;

            foreach (string subKeyName in appPathsKey.GetSubKeyNames())
            {
                try
                {
                    using var subKey = appPathsKey.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    object? val = subKey.GetValue(null);
                    if (val is string pathStr && !string.IsNullOrWhiteSpace(pathStr))
                    {
                        string cleanPath = pathStr.Trim('"', ' ');
                        if (cleanPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(cleanPath))
                        {
                            string exeName = Path.GetFileName(cleanPath);
                            if (IsExcludedExe(exeName)) continue;

                            string disp = AppPickerLogic.GetDisplayName(exeName);
                            bool isSystem = AppPickerLogic.IsSystemProcess(cleanPath, disp, true);

                            items.Add(new AppPickerItem
                            {
                                DisplayName = disp,
                                ExeName = AppPickerLogic.NormalizeExeName(exeName),
                                Path = cleanPath,
                                Category = isSystem ? AppPickerLogic.CatSystem : AppPickerLogic.CatInstalled,
                                IsSystemProcess = isSystem
                            });
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static void ScanProgramFilesDirectories(List<AppPickerItem> items)
    {
        var scanRoots = new List<string>();

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(pf) && Directory.Exists(pf)) scanRoots.Add(pf);

        string pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pfX86) && Directory.Exists(pfX86) && !string.Equals(pf, pfX86, StringComparison.OrdinalIgnoreCase))
        {
            scanRoots.Add(pfX86);
        }

        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApp))
        {
            string localPrograms = Path.Combine(localApp, "Programs");
            if (Directory.Exists(localPrograms)) scanRoots.Add(localPrograms);

            // WindowsApps (UWP / Modern Windows Store apps like Notepad, Paint, Claude Desktop, Terminal)
            string winApps = Path.Combine(localApp, "Microsoft", "WindowsApps");
            if (Directory.Exists(winApps))
            {
                try
                {
                    foreach (var exeFile in Directory.EnumerateFiles(winApps, "*.exe", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            string exeName = Path.GetFileName(exeFile);
                            if (IsExcludedExe(exeName)) continue;
                            string disp = AppPickerLogic.GetDisplayName(exeName);
                            bool isSystem = AppPickerLogic.IsSystemProcess(exeFile, disp, false);

                            items.Add(new AppPickerItem
                            {
                                DisplayName = disp,
                                ExeName = AppPickerLogic.NormalizeExeName(exeName),
                                Path = exeFile,
                                Category = isSystem ? AppPickerLogic.CatSystem : AppPickerLogic.CatInstalled,
                                IsSystemProcess = isSystem
                            });
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        // Steam common games library
        if (!string.IsNullOrWhiteSpace(pfX86))
        {
            string steamCommon = Path.Combine(pfX86, "Steam", "steamapps", "common");
            if (Directory.Exists(steamCommon)) scanRoots.Add(steamCommon);
        }

        foreach (var root in scanRoots)
        {
            try
            {
                foreach (string appDir in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        string dirName = Path.GetFileName(appDir);
                        // Skip common non-user folders
                        if (dirName.Equals("Common Files", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("Windows Defender", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("Windows Defender Advanced Threat Protection", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("Windows NT", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("Windows Mail", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("Windows Media Player", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("Windows Photo Viewer", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("WindowsPowerShell", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("Uninstall Information", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string? mainExe = FindMainExeInDirectory(appDir);
                        if (mainExe != null && File.Exists(mainExe))
                        {
                            string exeName = Path.GetFileName(mainExe);
                            if (IsExcludedExe(exeName)) continue;

                            string disp = AppPickerLogic.GetDisplayName(exeName);
                            bool isSystem = AppPickerLogic.IsSystemProcess(mainExe, disp, true);

                            items.Add(new AppPickerItem
                            {
                                DisplayName = !string.IsNullOrWhiteSpace(dirName) ? dirName : disp,
                                ExeName = AppPickerLogic.NormalizeExeName(exeName),
                                Path = mainExe,
                                Category = isSystem ? AppPickerLogic.CatSystem : AppPickerLogic.CatInstalled,
                                IsSystemProcess = isSystem
                            });
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static readonly HashSet<string> IgnoredFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Common Files", "dotnet", "Windows Defender", "Windows Defender Advanced Threat Protection",
        "Windows NT", "Windows Mail", "Windows Media Player", "Windows Photo Viewer", "WindowsPowerShell",
        "Uninstall Information", "locales", "node_modules", "resources", "swiftshader", "drivers",
        "Temp", "Logs", "Packages"
    };

    private static string? FindMainExeInDirectory(string dirPath, int currentDepth = 0, int maxDepth = 2)
    {
        try
        {
            if (!Directory.Exists(dirPath)) return null;
            string dirName = Path.GetFileName(dirPath);

            // 1. Direct match: dirName.exe (e.g. C:\Program Files\7-Zip\7zFM.exe or ...\namida\namida.exe)
            string directExe = Path.Combine(dirPath, dirName + ".exe");
            if (File.Exists(directExe) && !IsExcludedExe(Path.GetFileName(directExe))) return directExe;

            // 2. Look for .exe files in root of appDir
            var exeFiles = Directory.EnumerateFiles(dirPath, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(f => !IsExcludedExe(Path.GetFileName(f)))
                .ToList();

            if (exeFiles.Count > 0)
            {
                // Prefer exe with matching name or shortest name
                var exact = exeFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(dirName, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;

                return exeFiles.OrderBy(f => Path.GetFileName(f).Length).First();
            }

            // 3. Search subdirectories if within maxDepth
            if (currentDepth < maxDepth)
            {
                foreach (string subDir in Directory.EnumerateDirectories(dirPath))
                {
                    string subName = Path.GetFileName(subDir);
                    if (IgnoredFolderNames.Contains(subName)) continue;

                    string? found = FindMainExeInDirectory(subDir, currentDepth + 1, maxDepth);
                    if (found != null) return found;
                }
            }
        }
        catch { }

        return null;
    }

    private static void ScanStartMenuShortcuts(List<AppPickerItem> items)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;

            var folders = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
            };

            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;

                foreach (var file in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories))
                {
                    try
                    {
                        dynamic shortcut = shell.CreateShortcut(file);
                        string targetPath = (string)shortcut.TargetPath;
                        if (!string.IsNullOrWhiteSpace(targetPath) &&
                            targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(targetPath))
                        {
                            string exeName = Path.GetFileName(targetPath);
                            if (IsExcludedExe(exeName)) continue;

                            string shortcutName = Path.GetFileNameWithoutExtension(file);
                            bool isSystem = AppPickerLogic.IsSystemProcess(targetPath, shortcutName, true);

                            items.Add(new AppPickerItem
                            {
                                DisplayName = !string.IsNullOrWhiteSpace(shortcutName) ? shortcutName : AppPickerLogic.GetDisplayName(exeName),
                                ExeName = AppPickerLogic.NormalizeExeName(exeName),
                                Path = targetPath,
                                Category = isSystem ? AppPickerLogic.CatSystem : AppPickerLogic.CatInstalled,
                                IsSystemProcess = isSystem
                            });
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static void ScanRunningProcesses(List<AppPickerItem> items)
    {
        try
        {
            var processes = Process.GetProcesses();
            foreach (var p in processes)
            {
                try
                {
                    string procName = p.ProcessName;
                    if (string.IsNullOrWhiteSpace(procName)) continue;

                    bool hasWindow = p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle);
                    string? exePath = null;
                    try
                    {
                        exePath = p.MainModule?.FileName;
                    }
                    catch { }

                    bool isSystem = AppPickerLogic.IsSystemProcess(exePath, procName, hasWindow);
                    string category = isSystem ? AppPickerLogic.CatSystem : AppPickerLogic.CatRunning;

                    string exeName = AppPickerLogic.NormalizeExeName(procName);
                    if (IsExcludedExe(exeName)) continue;

                    items.Add(new AppPickerItem
                    {
                        DisplayName = AppPickerLogic.GetDisplayName(procName),
                        ExeName = exeName,
                        Path = exePath,
                        Category = category,
                        IsSystemProcess = isSystem
                    });
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }
    }

    private static List<AppPickerItem> DeduplicateAndCategorize(List<AppPickerItem> rawItems)
    {
        var deduplicated = new Dictionary<string, AppPickerItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in rawItems)
        {
            string key = AppPickerLogic.NormalizeExeName(item.ExeName);
            if (string.IsNullOrEmpty(key)) continue;

            if (ProtectedProcesses.IsProtected(key) ||
                ProtectedProcesses.IsProtected(item.ExeName) ||
                string.Equals(key, "sankalpa", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "blocker", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "nativehost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "sankalpa-cli", StringComparison.OrdinalIgnoreCase) ||
                item.DisplayName.StartsWith("sankalpa", StringComparison.OrdinalIgnoreCase) ||
                item.DisplayName.StartsWith("blocker", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (deduplicated.TryGetValue(key, out var existing))
            {
                // Merge info: prefer installed/running category over system, existing path, and friendly display name
                if (existing.Category == AppPickerLogic.CatSystem && item.Category != AppPickerLogic.CatSystem)
                {
                    existing.Category = item.Category;
                    existing.IsSystemProcess = false;
                }
                else if (existing.Category == AppPickerLogic.CatInstalled && item.Category == AppPickerLogic.CatRunning)
                {
                    existing.Category = AppPickerLogic.CatRunning;
                }

                if (string.IsNullOrEmpty(existing.Path) && !string.IsNullOrEmpty(item.Path))
                {
                    existing.Path = item.Path;
                }

                if (existing.DisplayName.Equals(key, StringComparison.OrdinalIgnoreCase) && !item.DisplayName.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    existing.DisplayName = item.DisplayName;
                }
            }
            else
            {
                deduplicated[key] = item;
            }
        }

        return deduplicated.Values
            .OrderBy(i => i.Category switch
            {
                AppPickerLogic.CatRunning => 0,
                AppPickerLogic.CatInstalled => 1,
                _ => 2
            })
            .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsExcludedExe(string exeName)
    {
        string name = Path.GetFileNameWithoutExtension(exeName);
        if (ProtectedProcesses.IsProtected(name) ||
            ProtectedProcesses.IsProtected(exeName) ||
            name.StartsWith("sankalpa", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("blocker", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var prefix in ExcludedExePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
