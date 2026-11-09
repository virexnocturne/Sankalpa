using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocker.Core;

public static class CommandNames
{
    public const string Ping = "Ping";
    public const string ListProfiles = "ListProfiles";
    public const string GetProfile = "GetProfile";
    public const string SaveProfile = "SaveProfile";
    public const string DeleteProfile = "DeleteProfile";
    public const string StartSession = "StartSession";
    public const string StopSession = "StopSession";
    public const string GetStatus = "GetStatus";
    public const string CleanHosts = "CleanHosts";
    public const string HostsStatus = "HostsStatus";
    public const string GetEvents = "GetEvents";
    public const string GetUnlockChallenge = "GetUnlockChallenge";
    public const string EnableProfile = "EnableProfile";
    public const string DisableProfile = "DisableProfile";
    public const string GetActiveWebRules = "GetActiveWebRules";
    public const string ExtHeartbeat = "ExtHeartbeat";
    public const string EmergencyUnlock = "EmergencyUnlock";
    public const string GetDnsConfig = "GetDnsConfig";
    public const string SaveDnsConfig = "SaveDnsConfig";
    public const string LockDns = "LockDns";
    public const string UnlockDns = "UnlockDns";
    public const string RenameProfile = "RenameProfile";
    public const string AddWebsite = "AddWebsite";
    public const string GetDnsChallenge = "GetDnsChallenge";
    public const string RequestDelayedUnlock = "RequestDelayedUnlock";
    public const string CancelDelayedUnlock = "CancelDelayedUnlock";
    public const string StartMeditation = "StartMeditation";
    public const string StopMeditation = "StopMeditation";
    public const string GetMeditationStatus = "GetMeditationStatus";
    public const string SaveMeditationNote = "SaveMeditationNote";
    public const string GetMeditationHistory = "GetMeditationHistory";
    public const string SaveMeditationSchedule = "SaveMeditationSchedule";
    public const string GetMeditationSchedules = "GetMeditationSchedules";
    public const string SnoozeMeditationSchedule = "SnoozeMeditationSchedule";
    public const string DeleteMeditationSchedule = "DeleteMeditationSchedule";
    public const string ConfirmTimerReady = "ConfirmTimerReady";
}

public class ActiveWebRuleDto
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    [JsonPropertyName("allow")]
    public bool Allow { get; set; }
}

public class ActiveProfileDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("blockAllWebsites")]
    public bool BlockAllWebsites { get; set; }

    [JsonPropertyName("webRules")]
    public List<ActiveWebRuleDto> WebRules { get; set; } = new();

    [JsonPropertyName("profileName")]
    public string ProfileName { get => Name; set => Name = value; }

    [JsonPropertyName("activeUntil")]
    public string? ActiveUntil { get; set; }

    [JsonPropertyName("activeUntilLocal")]
    public DateTime? ActiveUntilLocal { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;

    [JsonPropertyName("lockMode")]
    public string LockMode { get; set; } = "None";

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }
}

public class ProfileSummaryDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    [JsonPropertyName("lockMode")]
    public string LockMode { get; set; } = "None";

    [JsonPropertyName("ruleCount")]
    public int RuleCount { get; set; }

    [JsonPropertyName("blockAllWebsites")]
    public bool BlockAllWebsites { get; set; }
}

public class ActiveWebRulesResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("rulesVersion")]
    public int RulesVersion { get; set; }

    [JsonPropertyName("activeProfiles")]
    public List<ActiveProfileDto> ActiveProfiles { get; set; } = new();

    [JsonPropertyName("allProfiles")]
    public List<ProfileSummaryDto> AllProfiles { get; set; } = new();

    [JsonPropertyName("profiles")]
    public List<ActiveProfileDto> Profiles => ActiveProfiles;

    [JsonPropertyName("extensionTheme")]
    public string? ExtensionTheme { get; set; }

    [JsonPropertyName("cached")]
    public bool Cached { get; set; } = false;

    [JsonPropertyName("serviceConnected")]
    public bool ServiceConnected { get; set; } = true;
}

public class FlexibleRulesVersionConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetInt32().ToString();
        }
        return reader.GetString();
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (int.TryParse(value, out int intVal))
        {
            writer.WriteNumberValue(intVal);
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}

public class ActiveWebRulesData
{
    public List<ActiveProfileDto> Profiles { get; set; } = new();

    [JsonConverter(typeof(FlexibleRulesVersionConverter))]
    public string RulesVersion { get; set; } = string.Empty;
}

public class Request
{
    public string Command { get; set; } = string.Empty;
    public Dictionary<string, string>? Args { get; set; }
}

public class Response
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Data { get; set; }

    public static Response Success(string? data = null) => new() { Ok = true, Data = data };
    public static Response Fail(string error) => new() { Ok = false, Error = error };
}

public class StartMeditationRequest
{
    [JsonPropertyName("targetDurationSeconds")]
    public long TargetDurationSeconds { get; set; }

    [JsonPropertyName("allowedApps")]
    public List<string> AllowedApps { get; set; } = new();

    [JsonPropertyName("lockMode")]
    public string LockMode { get; set; } = "None";

    [JsonPropertyName("soundscape")]
    public string? Soundscape { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

public class MeditationStatusDto
{
    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Active";

    [JsonPropertyName("sessionId")]
    public int? SessionId { get; set; }

    [JsonPropertyName("startUtc")]
    public DateTime? StartUtc { get; set; }

    [JsonPropertyName("targetDurationSeconds")]
    public long TargetDurationSeconds { get; set; }

    [JsonPropertyName("remainingSeconds")]
    public long RemainingSeconds { get; set; }

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    [JsonPropertyName("lockMode")]
    public string LockMode { get; set; } = "None";

    [JsonPropertyName("allowedApps")]
    public List<string> AllowedApps { get; set; } = new();

    [JsonPropertyName("soundscape")]
    public string? Soundscape { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public class MeditationHistoryItemDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("startUtc")]
    public DateTime StartUtc { get; set; }

    [JsonPropertyName("targetDurationSeconds")]
    public long TargetDurationSeconds { get; set; }

    [JsonPropertyName("completedDurationSeconds")]
    public long CompletedDurationSeconds { get; set; }

    [JsonPropertyName("completed")]
    public bool Completed { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("soundscape")]
    public string? Soundscape { get; set; }

    [JsonPropertyName("allowedAppsCount")]
    public int AllowedAppsCount { get; set; }
}

public class SaveMeditationNoteRequest
{
    [JsonPropertyName("sessionId")]
    public int SessionId { get; set; }

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;
}

public class MeditationScheduleDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; } = 1;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("daysOfWeek")]
    public List<string> DaysOfWeek { get; set; } = new();

    [JsonPropertyName("timeOfDay")]
    public string TimeOfDay { get; set; } = "15:00";

    [JsonPropertyName("durationSeconds")]
    public long DurationSeconds { get; set; } = 900;

    [JsonPropertyName("allowedApps")]
    public List<string> AllowedApps { get; set; } = new();

    [JsonPropertyName("lockMode")]
    public string LockMode { get; set; } = "None";

    [JsonPropertyName("soundscape")]
    public string? Soundscape { get; set; }

    [JsonPropertyName("lastSnoozedUtc")]
    public DateTime? LastSnoozedUtc { get; set; }
}

public class SnoozeMeditationScheduleRequest
{
    [JsonPropertyName("scheduleId")]
    public int ScheduleId { get; set; }
}

public class ConfirmTimerReadyRequest
{
    [JsonPropertyName("sessionId")]
    public int SessionId { get; set; }

    [JsonPropertyName("visibleWindowPids")]
    public List<int> VisibleWindowPids { get; set; } = new();

    [JsonPropertyName("allowedAppRoots")]
    public Dictionary<string, string> AllowedAppRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
