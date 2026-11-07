using System;
using System.IO;
using Microsoft.Win32;

namespace Blocker.Core;

/// <summary>
/// Robust, centralized resolver for application executables on Windows.
/// Locates real executable paths across App Paths registry, standard install locations,
/// LocalAppData Programs, WindowsApps, Program Files, AppData, and system PATH.
/// </summary>
public static class AppPathResolver
{
    public static string? ResolveExecutablePath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string clean = input.Trim().Trim('"', '\'');

        // 1. Direct path check
        try
        {
            if (File.Exists(clean))
            {
                return Path.GetFullPath(clean);
            }
        }
        catch { }

        string fileName = clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? clean
            : clean + ".exe";
        string nameWithoutExt = Path.GetFileNameWithoutExtension(clean);

        // 2. Windows Registry App Paths (HKLM and HKCU)
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var hklm = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{fileName}");
                if (hklm != null)
                {
                    object? val = hklm.GetValue(null);
                    if (val is string p && !string.IsNullOrWhiteSpace(p))
                    {
                        string target = p.Trim('"', ' ');
                        if (File.Exists(target))
                        {
                            return Path.GetFullPath(target);
                        }
                    }
                }
            }
            catch { }

            try
            {
                using var hkcu = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{fileName}");
                if (hkcu != null)
                {
                    object? val = hkcu.GetValue(null);
                    if (val is string p && !string.IsNullOrWhiteSpace(p))
                    {
                        string target = p.Trim('"', ' ');
                        if (File.Exists(target))
                        {
                            return Path.GetFullPath(target);
                        }
                    }
                }
            }
            catch { }
        }

        // 3. System and Windows directories (e.g. C:\Windows\System32\notepad.exe)
        try
        {
            string sysDir = Environment.SystemDirectory;
            if (!string.IsNullOrEmpty(sysDir))
            {
                string target = Path.Combine(sysDir, fileName);
                if (File.Exists(target)) return target;
            }

            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(winDir))
            {
                string target = Path.Combine(winDir, fileName);
                if (File.Exists(target)) return target;
            }
        }
        catch { }

        // 4. LocalApplicationData (\Programs\..., \Microsoft\WindowsApps\...)
        // (Obsidian, VS Code, Discord, Windows Terminal, Slack, etc.)
        try
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localApp))
            {
                string localPrograms = Path.Combine(localApp, "Programs");
                if (Directory.Exists(localPrograms))
                {
                    string target = Path.Combine(localPrograms, fileName);
                    if (File.Exists(target)) return target;

                    target = Path.Combine(localPrograms, nameWithoutExt, fileName);
                    if (File.Exists(target)) return target;

                    target = Path.Combine(localPrograms, "Microsoft VS Code", fileName);
                    if (File.Exists(target)) return target;

                    target = Path.Combine(localPrograms, "Notion", fileName);
                    if (File.Exists(target)) return target;
                }

                string winApps = Path.Combine(localApp, "Microsoft", "WindowsApps");
                if (Directory.Exists(winApps))
                {
                    string target = Path.Combine(winApps, fileName);
                    if (File.Exists(target)) return target;
                }

                string appTarget = Path.Combine(localApp, nameWithoutExt, fileName);
                if (File.Exists(appTarget)) return appTarget;
            }
        }
        catch { }

        // 5. ApplicationData (e.g. %APPDATA%\Spotify\Spotify.exe)
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                string target = Path.Combine(appData, nameWithoutExt, fileName);
                if (File.Exists(target)) return target;

                if (string.Equals(fileName, "spotify.exe", StringComparison.OrdinalIgnoreCase))
                {
                    target = Path.Combine(appData, "Spotify", "Spotify.exe");
                    if (File.Exists(target)) return target;
                }
            }
        }
        catch { }

        // 6. ProgramFiles and ProgramFilesX86 (e.g. VLC, Chrome, VS Code, Slack, etc.)
        try
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf))
            {
                string target = Path.Combine(pf, fileName);
                if (File.Exists(target)) return target;

                target = Path.Combine(pf, nameWithoutExt, fileName);
                if (File.Exists(target)) return target;

                target = Path.Combine(pf, "VideoLAN", "VLC", fileName);
                if (File.Exists(target)) return target;

                target = Path.Combine(pf, "Google", "Chrome", "Application", fileName);
                if (File.Exists(target)) return target;

                target = Path.Combine(pf, "Microsoft VS Code", fileName);
                if (File.Exists(target)) return target;
            }

            string pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pfX86))
            {
                string target = Path.Combine(pfX86, fileName);
                if (File.Exists(target)) return target;

                target = Path.Combine(pfX86, nameWithoutExt, fileName);
                if (File.Exists(target)) return target;

                target = Path.Combine(pfX86, "VideoLAN", "VLC", fileName);
                if (File.Exists(target)) return target;

                target = Path.Combine(pfX86, "Google", "Chrome", "Application", fileName);
                if (File.Exists(target)) return target;
            }
        }
        catch { }

        // 7. System PATH environment variable
        try
        {
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathEnv))
            {
                foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    try
                    {
                        if (Directory.Exists(dir))
                        {
                            string target = Path.Combine(dir, fileName);
                            if (File.Exists(target)) return target;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        return null;
    }

    public static string ResolveOrOriginal(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        return ResolveExecutablePath(input) ?? input;
    }
}
