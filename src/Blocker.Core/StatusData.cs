using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Blocker.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostsState
{
    Idle,
    Blocking,
    Error
}

public class HostsStatusInfo
{
    public HostsState HostsState { get; set; } = HostsState.Idle;
    public int DomainCount { get; set; }
    public string? LastError { get; set; }
    public List<string> Domains { get; set; } = new();
}

public class ProfileStatusItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool IsActive { get; set; }
    public string ActiveReason { get; set; } = "None"; // "Manual", "Schedule", "AlwaysOn", "None"
    public DateTime? NextChangeLocal { get; set; }
    public bool LockedNow { get; set; }
    public LockMode LockMode { get; set; }
    public string ScheduleSummary { get; set; } = string.Empty;
}

public class StatusData
{
    public bool Active { get; set; }
    public string? Message { get; set; }
    public string? Profile { get; set; }
    public List<string> BlockedProcesses { get; set; } = new();
    public List<string> BlockedDomains { get; set; } = new();
    public bool Locked { get; set; }
    public long RemainingSeconds { get; set; }
    public string RemainingFormatted { get; set; } = "00:00";
    public bool ServiceElevated { get; set; }
    public HostsState HostsState { get; set; } = HostsState.Idle;
    public int HostsDomainCount { get; set; }
    public bool HostsBlockingOk { get; set; }
    public string? HostsLastError { get; set; }

    public List<string> ActiveProfileNames { get; set; } = new();
    public List<ProfileStatusItem> ProfileStatuses { get; set; } = new();
    public ExtensionStatusData ExtensionStatus { get; set; } = new();
}

public class ExtensionStatusData
{
    public bool Connected { get; set; }
    public List<BrowserStatusInfo> Browsers { get; set; } = new();
    public bool FallbackActive { get; set; }
    public string? FallbackReason { get; set; }
    public bool BlockAllUncovered { get; set; }
    public List<string> WarningBanners { get; set; } = new();
}
