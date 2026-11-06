using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace Blocker.Core;

public class MeditationSchedule
{
    public int Id { get; set; }
    public bool Enabled { get; set; } = true;
    public string DaysOfWeekJson { get; set; } = "[]";
    public string TimeOfDay { get; set; } = "15:00";
    public string EndTimeOfDay { get; set; } = "16:00";
    public long DurationSeconds { get; set; } = 900;
    public string AllowedAppsJson { get; set; } = "[]";
    public LockMode LockMode { get; set; } = LockMode.None;
    public string? Soundscape { get; set; }
    public DateTime? LastSnoozedUtc { get; set; }
    public string? PasswordHash { get; set; }

    public List<DayOfWeek> GetDays()
    {
        if (string.IsNullOrWhiteSpace(DaysOfWeekJson))
        {
            return new List<DayOfWeek>();
        }

        try
        {
            using var doc = JsonDocument.Parse(DaysOfWeekJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new List<DayOfWeek>();
            }

            var result = new List<DayOfWeek>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    if (Enum.TryParse<DayOfWeek>(el.GetString(), true, out var day))
                    {
                        result.Add(day);
                    }
                }
                else if (el.ValueKind == JsonValueKind.Number)
                {
                    result.Add((DayOfWeek)el.GetInt32());
                }
            }
            return result;
        }
        catch
        {
            return new List<DayOfWeek>();
        }
    }

    public void SetDays(List<DayOfWeek> days)
    {
        var dayNames = (days ?? new List<DayOfWeek>()).Select(d => d.ToString()).ToList();
        DaysOfWeekJson = JsonSerializer.Serialize(dayNames);
    }

    public List<string> GetAllowedApps()
    {
        if (string.IsNullOrWhiteSpace(AllowedAppsJson))
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(AllowedAppsJson) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public void SetAllowedApps(List<string> apps)
    {
        AllowedAppsJson = JsonSerializer.Serialize(apps ?? new List<string>());
    }

    public static bool TryCalculateDurationMinutes(string startTime, string endTime, out int durationMinutes)
    {
        durationMinutes = 0;
        if (string.IsNullOrWhiteSpace(startTime) || string.IsNullOrWhiteSpace(endTime))
        {
            return false;
        }

        if (!TimeSpan.TryParse(startTime.Trim(), out var start) || !TimeSpan.TryParse(endTime.Trim(), out var end))
        {
            return false;
        }

        if (start < TimeSpan.Zero || start >= TimeSpan.FromHours(24) || end < TimeSpan.Zero || end >= TimeSpan.FromHours(24))
        {
            return false;
        }

        var diff = end - start;
        if (diff <= TimeSpan.Zero)
        {
            diff += TimeSpan.FromHours(24);
        }

        durationMinutes = (int)Math.Round(diff.TotalMinutes);
        return true;
    }

    public static int CalculateDurationMinutes(string startTime, string endTime)
    {
        if (TryCalculateDurationMinutes(startTime, endTime, out int mins))
        {
            return mins;
        }
        return 0;
    }

    public int CalculateDurationMinutes() => CalculateDurationMinutes(TimeOfDay, EndTimeOfDay);

    public long CalculateDurationSeconds() => (long)CalculateDurationMinutes() * 60;

    public void UpdateDurationFromTimeRange()
    {
        int mins = CalculateDurationMinutes();
        if (mins > 0)
        {
            DurationSeconds = (long)mins * 60;
        }
    }

    public static string CalculateEndTime(string startTime, int durationMinutes)
    {
        if (TimeSpan.TryParse(startTime, out var start) && start >= TimeSpan.Zero && start < TimeSpan.FromHours(24))
        {
            var end = start + TimeSpan.FromMinutes(durationMinutes);
            int totalMins = ((int)end.TotalMinutes % 1440 + 1440) % 1440;
            return $"{totalMins / 60:D2}:{totalMins % 60:D2}";
        }
        return "16:00";
    }

    public void UpdateEndTimeFromDuration()
    {
        int mins = (int)(DurationSeconds / 60);
        if (mins > 0)
        {
            EndTimeOfDay = CalculateEndTime(TimeOfDay, mins);
        }
    }
}

public static class InstalledAppScanner
{
    public static bool IsAppInstalled(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;

        string cleanExe = exeName.Trim().Trim('"', '\'');
        string nameWithoutExt = Path.GetFileNameWithoutExtension(cleanExe);
        string fileName = cleanExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? cleanExe
            : cleanExe + ".exe";

        try
        {
            // 1. Check if process is currently running with that process name (without .exe)
            try
            {
                var running = Process.GetProcessesByName(nameWithoutExt);
                if (running != null && running.Length > 0)
                {
                    foreach (var p in running) { try { p.Dispose(); } catch { } }
                    return true;
                }
            }
            catch { }

            // 2. Check registry keys on Windows
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var hklmAppPath = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{fileName}");
                    if (hklmAppPath != null)
                    {
                        object? val = hklmAppPath.GetValue(null);
                        if (val is string p && !string.IsNullOrWhiteSpace(p))
                        {
                            string target = p.Trim('"', ' ');
                            if (File.Exists(target)) return true;
                        }
                        return true;
                    }
                }
                catch { }

                try
                {
                    using var hkcuAppPath = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{fileName}");
                    if (hkcuAppPath != null)
                    {
                        object? val = hkcuAppPath.GetValue(null);
                        if (val is string p && !string.IsNullOrWhiteSpace(p))
                        {
                            string target = p.Trim('"', ' ');
                            if (File.Exists(target)) return true;
                        }
                        return true;
                    }
                }
                catch { }
            }

            // 3. Check SystemDirectory and Windows directory (e.g. C:\Windows\System32\<exeName>)
            try
            {
                string sysDir = Environment.SystemDirectory;
                if (!string.IsNullOrEmpty(sysDir) && File.Exists(Path.Combine(sysDir, fileName)))
                {
                    return true;
                }

                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(winDir) && File.Exists(Path.Combine(winDir, fileName)))
                {
                    return true;
                }
            }
            catch { }

            // 4. Check SpecialFolder.LocalApplicationData (\Programs\..., \Microsoft\WindowsApps\...)
            try
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localApp))
                {
                    string localPrograms = Path.Combine(localApp, "Programs");
                    if (Directory.Exists(localPrograms))
                    {
                        if (File.Exists(Path.Combine(localPrograms, fileName))) return true;
                        if (File.Exists(Path.Combine(localPrograms, nameWithoutExt, fileName))) return true;
                        if (File.Exists(Path.Combine(localPrograms, "Microsoft VS Code", fileName))) return true;
                    }

                    string winApps = Path.Combine(localApp, "Microsoft", "WindowsApps");
                    if (Directory.Exists(winApps) && File.Exists(Path.Combine(winApps, fileName)))
                    {
                        return true;
                    }

                    if (File.Exists(Path.Combine(localApp, nameWithoutExt, fileName))) return true;
                }
            }
            catch { }

            // 5. Check SpecialFolder.ApplicationData (e.g. %APPDATA%\Spotify\Spotify.exe)
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(appData))
                {
                    if (File.Exists(Path.Combine(appData, nameWithoutExt, fileName))) return true;
                    if (File.Exists(Path.Combine(appData, "Spotify", "Spotify.exe")) && string.Equals(fileName, "spotify.exe", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }

            // 6. Check ProgramFiles and ProgramFilesX86
            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrEmpty(pf))
                {
                    if (File.Exists(Path.Combine(pf, fileName))) return true;
                    if (File.Exists(Path.Combine(pf, nameWithoutExt, fileName))) return true;
                    if (File.Exists(Path.Combine(pf, "VideoLAN", "VLC", fileName))) return true;
                    if (File.Exists(Path.Combine(pf, "Google", "Chrome", "Application", fileName))) return true;
                    if (File.Exists(Path.Combine(pf, "Microsoft VS Code", fileName))) return true;
                    if (File.Exists(Path.Combine(pf, "Spotify", fileName))) return true;
                }

                string pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrEmpty(pfX86))
                {
                    if (File.Exists(Path.Combine(pfX86, fileName))) return true;
                    if (File.Exists(Path.Combine(pfX86, nameWithoutExt, fileName))) return true;
                    if (File.Exists(Path.Combine(pfX86, "VideoLAN", "VLC", fileName))) return true;
                    if (File.Exists(Path.Combine(pfX86, "Google", "Chrome", "Application", fileName))) return true;
                    if (File.Exists(Path.Combine(pfX86, "Spotify", fileName))) return true;
                }
            }
            catch { }

            // 7. Check PATH environment variable
            try
            {
                string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrWhiteSpace(pathEnv))
                {
                    var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var dir in paths)
                    {
                        try
                        {
                            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, fileName)))
                            {
                                return true;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
        catch { }

        return false;
    }
}
