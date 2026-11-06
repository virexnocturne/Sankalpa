using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Blocker.Core;

public class MeditationSchedule
{
    public int Id { get; set; }
    public bool Enabled { get; set; } = true;
    public string DaysOfWeekJson { get; set; } = "[]";
    public string TimeOfDay { get; set; } = "15:00";
    public long DurationSeconds { get; set; } = 900;
    public string AllowedAppsJson { get; set; } = "[]";
    public LockMode LockMode { get; set; } = LockMode.None;
    public string? Soundscape { get; set; }
    public DateTime? LastSnoozedUtc { get; set; }

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
}
