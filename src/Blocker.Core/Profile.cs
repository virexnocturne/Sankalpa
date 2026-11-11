using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Blocker.Core;

public enum LockMode
{
    None = 0,
    Locked = 1,
    TypingChallenge = 2,
    Password = 3,
    DelayedUnlock = 4,
    HardLocked = 5
}

public class Profile
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> BlockedProcesses { get; set; } = new();
    public List<string> BlockedDomains { get; set; } = new();
    public List<WebRule> WebRules { get; set; } = new();
    public bool BlockAllWebsites { get; set; } = false;
    public bool SystemWide { get; set; } = false;

    public bool Enabled { get; set; } = false;
    public List<ScheduleRule> Schedule { get; set; } = new();
    public AllowanceConfig Allowance { get; set; } = new();
    public LockMode LockMode { get; set; } = LockMode.None;
    public int DelayedUnlockMinutes { get; set; } = 15;

    /// <summary>
    /// UTC timestamp until which the currently active schedule window is bypassed
    /// (for example after an emergency unlock), preserving recurring schedule rules.
    /// </summary>
    public DateTime? ScheduleBypassedUntilUtc { get; set; }

    // Password credentials are never serialized across the client protocol
    [JsonIgnore]
    public string? PasswordSalt { get; set; }

    [JsonIgnore]
    public string? PasswordHash { get; set; }
}
