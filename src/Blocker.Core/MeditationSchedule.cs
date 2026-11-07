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
    public const int DefaultId = 1;
    public int Id { get; set; } = DefaultId;
    public bool IsSingleton => Id == DefaultId;
    public bool Enabled { get; set; } = true;

    public static MeditationSchedule CreateDefault() => new()
    {
        Id = DefaultId,
        Enabled = false,
        DaysOfWeekJson = "[]",
        TimeOfDay = "15:00",
        EndTimeOfDay = "16:00",
        DurationSeconds = 900,
        AllowedAppsJson = "[]",
        LockMode = LockMode.None,
        Soundscape = null
    };
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

            // 2. Resolve via comprehensive AppPathResolver
            return AppPathResolver.ResolveExecutablePath(cleanExe) != null;
        }
        catch
        {
            return false;
        }
    }
}
