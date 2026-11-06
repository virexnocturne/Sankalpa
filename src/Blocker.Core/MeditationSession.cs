using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Blocker.Core;

public class MeditationSession
{
    public int Id { get; set; }
    public DateTime StartUtc { get; set; }
    public long TargetDurationSeconds { get; set; }
    public long CompletedDurationSeconds { get; set; }
    public bool Completed { get; set; }
    public string? Notes { get; set; }
    public string AllowedAppsJson { get; set; } = "[]";
    public LockMode LockMode { get; set; } = LockMode.None;
    public string? Soundscape { get; set; }
    public DateTime? EndedUtc { get; set; }
    public string? PasswordHash { get; set; }

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
