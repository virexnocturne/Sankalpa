using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Blocker.Core;
using Blocker.Service;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Sankalpa.Tests;

public class EmergencyUnlockScheduleTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _tempFiles)
        {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                }
            }
            catch { }
        }
    }

    private string CreateTempDbPath()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"emg_sched_test_{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        return path;
    }

    private sealed class InMemoryScheduleStore : IBlockStore
    {
        private int _nextProfileId = 1;
        private int _nextSessionId = 1;
        private int _nextMedId = 1;

        public List<Profile> Profiles { get; } = new();
        public List<BlockSession> Sessions { get; } = new();
        public List<MeditationSession> MeditationSessions { get; } = new();
        public MeditationSchedule? StoredMeditationSchedule { get; set; }
        public DnsConfig DnsSettings { get; set; } = new();

        public bool EmergencyUnlockExists { get; set; }
        public bool EmergencyUnlockConsumed { get; set; }
        public bool CleanHostsCalled { get; set; }
        public bool ServiceElevated { get; set; } = true;
        public bool HostsBlockingOk { get; set; } = true;
        public HostsState HostsState { get; set; } = HostsState.Idle;
        public int HostsDomainCount { get; set; }
        public string? HostsLastError { get; set; }
        public FallbackPlanner FallbackPlanner { get; } = new();
        public FallbackPlanResult? LastFallbackPlan { get; set; }
        public List<BlockEvent> Events { get; } = new();

        public Profile? GetProfileByName(string name) =>
            Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        public Profile? GetProfileById(int id) => Profiles.FirstOrDefault(p => p.Id == id);
        public List<Profile> ListProfiles() => Profiles.ToList();

        public Profile SaveProfile(Profile profile)
        {
            profile.BlockedDomains = WebRuleMatcher.DeriveBlockedDomains(profile.WebRules, profile.BlockAllWebsites);
            var existing = GetProfileByName(profile.Name);
            if (existing != null)
            {
                existing.BlockedProcesses = profile.BlockedProcesses;
                existing.BlockedDomains = profile.BlockedDomains;
                existing.WebRules = profile.WebRules;
                existing.BlockAllWebsites = profile.BlockAllWebsites;
                existing.SystemWide = profile.SystemWide;
                existing.Enabled = profile.Enabled;
                existing.Schedule = profile.Schedule;
                existing.LockMode = profile.LockMode;
                existing.PasswordSalt = profile.PasswordSalt;
                existing.PasswordHash = profile.PasswordHash;
                existing.Allowance = profile.Allowance;
                existing.DelayedUnlockMinutes = profile.DelayedUnlockMinutes;
                existing.ScheduleBypassedUntilUtc = profile.ScheduleBypassedUntilUtc;
                return existing;
            }

            profile.Id = _nextProfileId++;
            Profiles.Add(profile);
            return profile;
        }

        public bool RenameProfile(int id, string newName)
        {
            var p = GetProfileById(id);
            if (p == null) return false;
            p.Name = newName;
            return true;
        }

        public bool DeleteProfileByName(string name)
        {
            var p = GetProfileByName(name);
            return p != null && Profiles.Remove(p);
        }

        public bool DeleteProfile(int id)
        {
            var p = GetProfileById(id);
            return p != null && Profiles.Remove(p);
        }

        public void DisableAllProfiles()
        {
            foreach (var p in Profiles)
            {
                p.Enabled = false;
            }
        }

        public void BypassActiveSchedules(DateTime localNow)
        {
            foreach (var p in Profiles)
            {
                var until = ProfileStateLogic.ComputeScheduleBypassUntilUtc(p.Schedule, localNow);
                if (until.HasValue && (!p.ScheduleBypassedUntilUtc.HasValue || until.Value > p.ScheduleBypassedUntilUtc.Value))
                {
                    p.ScheduleBypassedUntilUtc = until.Value;
                }
            }
        }

        public List<BlockSession> GetActiveSessions() => Sessions.Where(s => s.EndedUtc == null).ToList();

        public BlockSession? GetActiveSession() => Sessions.FirstOrDefault(s => s.EndedUtc == null);

        public BlockSession? GetActiveSession(int? profileId) =>
            profileId.HasValue
                ? Sessions.FirstOrDefault(s => s.EndedUtc == null && s.ProfileId == profileId.Value)
                : GetActiveSession();

        public BlockSession? GetSessionById(int id) => Sessions.FirstOrDefault(s => s.Id == id);

        public BlockSession StartSession(int profileId, long durationSeconds, bool locked)
        {
            EndActiveSession(profileId);
            var session = new BlockSession
            {
                Id = _nextSessionId++,
                ProfileId = profileId,
                StartUtc = DateTime.UtcNow,
                DurationSeconds = durationSeconds,
                Locked = locked,
                EndedUtc = null
            };
            Sessions.Add(session);
            return session;
        }

        public void EndActiveSession()
        {
            foreach (var s in Sessions.Where(s => s.EndedUtc == null))
            {
                s.EndedUtc = DateTime.UtcNow;
            }
        }

        public void EndActiveSession(int? profileId)
        {
            foreach (var s in Sessions.Where(s => s.EndedUtc == null && (!profileId.HasValue || s.ProfileId == profileId.Value)))
            {
                s.EndedUtc = DateTime.UtcNow;
            }
        }

        public void EndSession(int sessionId)
        {
            var s = Sessions.FirstOrDefault(x => x.Id == sessionId);
            if (s != null && s.EndedUtc == null)
            {
                s.EndedUtc = DateTime.UtcNow;
            }
        }

        public bool IsEmergencyUnlockPresent() => EmergencyUnlockExists;

        public void ConsumeEmergencyUnlock()
        {
            EmergencyUnlockConsumed = true;
            EmergencyUnlockExists = false;
        }

        public bool CleanHosts()
        {
            CleanHostsCalled = true;
            return true;
        }

        public List<string> GetCurrentHostsDomains() => new();
        public void RecordBlockEvent(BlockEvent evt) => Events.Add(evt);
        public List<BlockEvent> GetBlockEvents(long sinceId) => Events.Where(e => e.Id > sinceId).ToList();
        public DnsConfig GetDnsConfig() => DnsSettings;
        public void SaveDnsConfig(DnsConfig config) => DnsSettings = config;

        public MeditationSession StartMeditationSession(long targetDurationSeconds, List<string> allowedApps, LockMode lockMode, string? soundscape = null, string? passwordHash = null, string status = "Active")
        {
            var med = new MeditationSession
            {
                Id = _nextMedId++,
                StartUtc = DateTime.UtcNow,
                TargetDurationSeconds = targetDurationSeconds,
                LockMode = lockMode,
                Soundscape = soundscape,
                PasswordHash = passwordHash,
                Status = status,
                EndedUtc = null
            };
            med.SetAllowedApps(allowedApps);
            MeditationSessions.Add(med);
            return med;
        }

        public MeditationSession? GetActiveMeditationSession() =>
            MeditationSessions.LastOrDefault(m => m.EndedUtc == null);

        public void EndMeditationSession(int id, bool completed, string? finalNotes = null)
        {
            var med = MeditationSessions.FirstOrDefault(m => m.Id == id);
            if (med != null)
            {
                med.EndedUtc = DateTime.UtcNow;
                med.Completed = completed;
                med.Status = completed ? "Completed" : "Stopped";
            }
        }

        public MeditationSchedule? GetMeditationSchedule() => StoredMeditationSchedule;

        public MeditationSchedule SaveMeditationSchedule(MeditationSchedule schedule)
        {
            schedule.Id = 1;
            StoredMeditationSchedule = schedule;
            return schedule;
        }
    }

    private static DaysOfWeek ToFlag(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Monday => DaysOfWeek.Mon,
        DayOfWeek.Tuesday => DaysOfWeek.Tue,
        DayOfWeek.Wednesday => DaysOfWeek.Wed,
        DayOfWeek.Thursday => DaysOfWeek.Thu,
        DayOfWeek.Friday => DaysOfWeek.Fri,
        DayOfWeek.Saturday => DaysOfWeek.Sat,
        DayOfWeek.Sunday => DaysOfWeek.Sun,
        _ => DaysOfWeek.None
    };

    private static List<ScheduleRule> BuildActiveFiniteWindowAroundNow(DateTime localNow)
    {
        int nowMin = localNow.Hour * 60 + localNow.Minute;
        var today = ToFlag(localNow.DayOfWeek);
        var inTwoDays = ToFlag(localNow.AddDays(2).DayOfWeek);

        if (nowMin <= 1370)
        {
            int start = Math.Max(0, nowMin - 30);
            int end = Math.Min(1439, nowMin + 60);
            return new List<ScheduleRule>
            {
                new() { Days = today | inTwoDays, StartMinute = start, EndMinute = end }
            };
        }

        // Near midnight: wrap overnight until 01:30 AM, plus a future day
        return new List<ScheduleRule>
        {
            new() { Days = today | inTwoDays, StartMinute = nowMin - 30, EndMinute = 90 }
        };
    }

    [Fact]
    public void EmergencyUnlock_Command_OpensCurrentlyActiveScheduledProfile_AndPreservesRecurringSchedule()
    {
        var store = new InMemoryScheduleStore();
        DateTime localNow = DateTime.Now;
        var activeSchedule = BuildActiveFiniteWindowAroundNow(localNow);
        var futureDay = ToFlag(localNow.AddDays(3).DayOfWeek);

        var scheduledLocked = store.SaveProfile(new Profile
        {
            Name = "ScheduledWork",
            Enabled = true,
            LockMode = LockMode.Locked,
            BlockedProcesses = new() { "steam.exe" },
            WebRules = new() { new WebRule { Pattern = "youtube.com", Allow = false } },
            Schedule = activeSchedule
        });

        var inactiveScheduled = store.SaveProfile(new Profile
        {
            Name = "FutureOnly",
            Enabled = false,
            LockMode = LockMode.Locked,
            BlockedProcesses = new() { "discord.exe" },
            Schedule = new() { new ScheduleRule { Days = futureDay, StartMinute = 540, EndMinute = 600 } }
        });

        var manualProfile = store.SaveProfile(new Profile
        {
            Name = "ManualFocus",
            Enabled = true,
            BlockedProcesses = new() { "notepad.exe" }
        });
        store.StartSession(manualProfile.Id, 3600, locked: true);

        // Pre-condition: scheduled profile is active and locked
        var preStatusResp = CommandHandler.Handle(new Request { Command = CommandNames.GetStatus }, store, ClientIdentity.Administrator());
        var preStatus = JsonSerializer.Deserialize<StatusData>(preStatusResp.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.True(preStatus.Active);
        Assert.Contains("ScheduledWork", preStatus.ActiveProfileNames);
        Assert.Contains("youtube.com", preStatus.BlockedDomains);
        Assert.True(ProfileStateLogic.IsProfileLockedNow(scheduledLocked, null, localNow));

        // Act: trigger EMERGENCYUNLOCK
        var resp = CommandHandler.Handle(new Request { Command = CommandNames.EmergencyUnlock }, store, ClientIdentity.Administrator());
        Assert.True(resp.Ok, resp.Error);
        Assert.True(store.CleanHostsCalled);
        Assert.Null(store.GetActiveSession());

        // Assert: currently active schedule is bypassed & unlocked, but rules are preserved
        Assert.False(scheduledLocked.Enabled);
        Assert.Single(scheduledLocked.Schedule);
        Assert.NotNull(scheduledLocked.ScheduleBypassedUntilUtc);
        Assert.True(ProfileStateLogic.IsScheduleBypassed(scheduledLocked, DateTime.Now));
        Assert.False(ProfileStateLogic.IsScheduleActive(scheduledLocked, DateTime.Now));
        Assert.False(ProfileStateLogic.IsProfileActive(scheduledLocked, null, DateTime.Now, out string reason));
        Assert.Equal("None", reason);
        Assert.False(ProfileStateLogic.IsProfileLockedNow(scheduledLocked, null, DateTime.Now));

        // Non-active future schedule was NOT given an unnecessary bypass
        Assert.Null(inactiveScheduled.ScheduleBypassedUntilUtc);

        // GetStatus and GetActiveWebRules reflect fully unlocked state
        var postStatusResp = CommandHandler.Handle(new Request { Command = CommandNames.GetStatus }, store, ClientIdentity.Administrator());
        var postStatus = JsonSerializer.Deserialize<StatusData>(postStatusResp.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.False(postStatus.Active);
        Assert.Empty(postStatus.ActiveProfileNames);
        Assert.Empty(postStatus.BlockedProcesses);
        Assert.Empty(postStatus.BlockedDomains);
        var scheduledStatus = postStatus.ProfileStatuses.Single(s => s.Name == "ScheduledWork");
        Assert.False(scheduledStatus.IsActive);
        Assert.False(scheduledStatus.LockedNow);
        Assert.Equal("None", scheduledStatus.ActiveReason);
        Assert.NotNull(scheduledStatus.NextChangeLocal);
        Assert.True(scheduledStatus.NextChangeLocal!.Value.Date > localNow.Date);

        var webRulesResp = CommandHandler.Handle(new Request { Command = CommandNames.GetActiveWebRules }, store, ClientIdentity.Administrator());
        var webRules = JsonSerializer.Deserialize<ActiveWebRulesResponse>(webRulesResp.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Empty(webRules.ActiveProfiles);
        var scheduledSummary = webRules.AllProfiles.Single(p => p.Name == "ScheduledWork");
        Assert.False(scheduledSummary.Active);
    }

    [Fact]
    public void EmergencyUnlock_BypassExpiresAtWindowEnd_AndResumesNextScheduledWindow()
    {
        var store = new InMemoryScheduleStore();
        // Fixed schedule: Mon-Fri 09:00-17:00
        var workdays = DaysOfWeek.Weekdays;
        var profile = store.SaveProfile(new Profile
        {
            Name = "WeekdayFocus",
            Enabled = true,
            LockMode = LockMode.HardLocked,
            Schedule = new() { new ScheduleRule { Days = workdays, StartMinute = 540, EndMinute = 1020 } }
        });

        DateTime tuesdayMorning = new DateTime(2026, 4, 14, 10, 30, 0, DateTimeKind.Local); // Tuesday 10:30
        store.DisableAllProfiles();
        store.BypassActiveSchedules(tuesdayMorning);

        // During remaining Tuesday window -> unlocked and inactive, next change points to Wed 09:00
        Assert.False(ProfileStateLogic.IsScheduleActive(profile, tuesdayMorning));
        Assert.False(ProfileStateLogic.IsProfileActive(profile, null, tuesdayMorning, out _));
        Assert.False(ProfileStateLogic.IsProfileLockedNow(profile, null, tuesdayMorning));

        DateTime tuesdayLateAfternoon = new DateTime(2026, 4, 14, 16, 59, 0, DateTimeKind.Local);
        Assert.False(ProfileStateLogic.IsProfileActive(profile, null, tuesdayLateAfternoon, out _));
        Assert.False(ProfileStateLogic.IsProfileLockedNow(profile, null, tuesdayLateAfternoon));

        var effectiveNext = ProfileStateLogic.GetEffectiveNextChange(profile, tuesdayMorning);
        Assert.Equal(new DateTime(2026, 4, 15, 9, 0, 0, DateTimeKind.Local), effectiveNext);

        // Window ends at 17:00
        DateTime tuesdayFivePm = new DateTime(2026, 4, 14, 17, 0, 0, DateTimeKind.Local);
        Assert.False(ProfileStateLogic.IsProfileActive(profile, null, tuesdayFivePm, out _));

        // Wednesday 09:05 -> recurring schedule activates & locks normally again!
        DateTime wednesdayMorning = new DateTime(2026, 4, 15, 9, 5, 0, DateTimeKind.Local);
        Assert.False(ProfileStateLogic.IsScheduleBypassed(profile, wednesdayMorning));
        Assert.True(ProfileStateLogic.IsScheduleActive(profile, wednesdayMorning));
        Assert.True(ProfileStateLogic.IsProfileActive(profile, null, wednesdayMorning, out string reason));
        Assert.Equal("Schedule", reason);
        Assert.True(ProfileStateLogic.IsProfileLockedNow(profile, null, wednesdayMorning));
    }

    [Fact]
    public void ComputeScheduleBypassUntilUtc_Unbounded24x7Schedule_BypassesFor24Hours()
    {
        var profile = new Profile
        {
            Name = "AlwaysScheduled24x7",
            Enabled = false,
            LockMode = LockMode.HardLocked,
            Schedule = new()
            {
                new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 }
            }
        };

        DateTime nowLocal = new DateTime(2026, 4, 14, 14, 0, 0, DateTimeKind.Local);
        DateTime? untilUtc = ProfileStateLogic.ComputeScheduleBypassUntilUtc(profile.Schedule, nowLocal);
        Assert.NotNull(untilUtc);
        Assert.True(untilUtc!.Value > nowLocal.ToUniversalTime());

        profile.ScheduleBypassedUntilUtc = untilUtc;
        Assert.False(ProfileStateLogic.IsProfileActive(profile, null, nowLocal, out _));
        Assert.False(ProfileStateLogic.IsProfileLockedNow(profile, null, nowLocal));
    }

    [Fact]
    public void StopSession_And_CleanHosts_WithEmergencyUnlock_AlsoBypassActiveSchedule()
    {
        DateTime localNow = DateTime.Now;
        var activeSchedule = BuildActiveFiniteWindowAroundNow(localNow);

        // 1. STOPSESSION with EmergencyUnlock present
        var store1 = new InMemoryScheduleStore { EmergencyUnlockExists = true };
        var p1 = store1.SaveProfile(new Profile
        {
            Name = "SchedStop",
            Enabled = false, // Patch #3: active strictly via schedule
            LockMode = LockMode.Locked,
            Schedule = activeSchedule
        });
        Assert.True(ProfileStateLogic.IsProfileLockedNow(p1, null, localNow));

        var stopResp = CommandHandler.Handle(new Request { Command = CommandNames.StopSession }, store1, ClientIdentity.StandardUser());
        Assert.True(stopResp.Ok, stopResp.Error);
        Assert.True(store1.EmergencyUnlockConsumed);
        Assert.False(ProfileStateLogic.IsProfileActive(p1, null, DateTime.Now, out _));
        Assert.False(ProfileStateLogic.IsProfileLockedNow(p1, null, DateTime.Now));

        // 2. CLEANHOSTS with EmergencyUnlock present
        var store2 = new InMemoryScheduleStore { EmergencyUnlockExists = true };
        var p2 = store2.SaveProfile(new Profile
        {
            Name = "SchedCleanHosts",
            Enabled = false,
            LockMode = LockMode.Locked,
            Schedule = activeSchedule
        });
        Assert.True(ProfileStateLogic.IsProfileLockedNow(p2, null, localNow));

        var cleanResp = CommandHandler.Handle(new Request { Command = CommandNames.CleanHosts }, store2, ClientIdentity.StandardUser());
        Assert.True(cleanResp.Ok, cleanResp.Error);
        Assert.True(store2.EmergencyUnlockConsumed);
        Assert.True(store2.CleanHostsCalled);
        Assert.False(ProfileStateLogic.IsProfileActive(p2, null, DateTime.Now, out _));
        Assert.False(ProfileStateLogic.IsProfileLockedNow(p2, null, DateTime.Now));
    }

    [Fact]
    public void DisableProfile_WithoutEmergencyUnlock_DoesNotBypassActiveSchedule()
    {
        var store = new InMemoryScheduleStore();
        DateTime localNow = DateTime.Now;
        var profile = store.SaveProfile(new Profile
        {
            Name = "NoCheatSchedule",
            Enabled = true,
            LockMode = LockMode.Locked,
            Schedule = BuildActiveFiniteWindowAroundNow(localNow)
        });

        var resp = CommandHandler.Handle(new Request
        {
            Command = CommandNames.DisableProfile,
            Args = new() { ["name"] = "NoCheatSchedule" }
        }, store, ClientIdentity.Administrator());

        Assert.False(resp.Ok);
        Assert.True(profile.Enabled);
        Assert.Null(profile.ScheduleBypassedUntilUtc);

        // Even with Enabled=false directly (Patch #3), without emergency bypass the schedule remains active and locked
        profile.Enabled = false;
        Assert.False(ProfileStateLogic.IsScheduleBypassed(profile, DateTime.Now));
        Assert.True(ProfileStateLogic.IsProfileActive(profile, null, DateTime.Now, out string reason));
        Assert.Equal("Schedule", reason);
        Assert.True(ProfileStateLogic.IsProfileLockedNow(profile, null, DateTime.Now));
    }

    [Fact]
    public void SaveProfile_ModifyingScheduleClearsBypass_EditingOtherFieldsPreservesBypass()
    {
        var store = new InMemoryScheduleStore();
        DateTime localNow = DateTime.Now;
        var originalSchedule = BuildActiveFiniteWindowAroundNow(localNow);

        var profile = store.SaveProfile(new Profile
        {
            Name = "EditableScheduled",
            Enabled = true,
            LockMode = LockMode.Locked,
            BlockedProcesses = new() { "steam.exe" },
            Schedule = originalSchedule
        });

        var emgResp = CommandHandler.Handle(new Request { Command = CommandNames.EmergencyUnlock }, store, ClientIdentity.Administrator());
        Assert.True(emgResp.Ok, emgResp.Error);
        Assert.NotNull(profile.ScheduleBypassedUntilUtc);

        // Editing processes without changing schedule preserves bypass
        var editAppsResp = CommandHandler.Handle(new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "EditableScheduled",
                ["processes"] = "steam.exe,epicgameslauncher.exe"
            }
        }, store, ClientIdentity.Administrator());
        Assert.True(editAppsResp.Ok, editAppsResp.Error);
        Assert.NotNull(profile.ScheduleBypassedUntilUtc);
        Assert.False(ProfileStateLogic.IsProfileActive(profile, null, DateTime.Now, out _));

        // Explicitly changing schedule rules clears the emergency bypass
        var newSchedule = new List<ScheduleRule>
        {
            new() { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 }
        };
        var editSchedResp = CommandHandler.Handle(new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "EditableScheduled",
                ["schedule"] = JsonSerializer.Serialize(newSchedule)
            }
        }, store, ClientIdentity.Administrator());
        Assert.True(editSchedResp.Ok, editSchedResp.Error);
        Assert.Null(profile.ScheduleBypassedUntilUtc);
    }

    [Fact]
    public void EmergencyUnlock_EndsActiveAndPendingMeditation_AndDisablesMeditationSchedule()
    {
        var store = new InMemoryScheduleStore();
        store.SaveMeditationSchedule(new MeditationSchedule
        {
            Enabled = true,
            DaysOfWeekJson = "[0,1,2,3,4,5,6]",
            TimeOfDay = "09:00",
            DurationSeconds = 900,
            LockMode = LockMode.Locked
        });
        var med = store.StartMeditationSession(900, new() { "notepad.exe" }, LockMode.Locked, status: "Pending");

        var resp = CommandHandler.Handle(new Request { Command = CommandNames.EmergencyUnlock }, store, ClientIdentity.Administrator());
        Assert.True(resp.Ok, resp.Error);

        Assert.Null(store.GetActiveMeditationSession());
        Assert.NotNull(med.EndedUtc);
        Assert.Equal("Stopped", med.Status);
        Assert.NotNull(store.GetMeditationSchedule());
        Assert.False(store.GetMeditationSchedule()!.Enabled);
    }

    [Fact]
    public void BlockerDatabase_EmergencyReset_PersistsScheduleBypassAndMeditationDisable_AndUnblocksUninstallGuard()
    {
        string dbPath = CreateTempDbPath();
        var db = new BlockerDatabase(dbPath);
        DateTime localNow = DateTime.Now;

        var scheduledProfile = db.SaveProfile(new Profile
        {
            Name = "RealDbScheduled",
            Enabled = true,
            LockMode = LockMode.Locked,
            BlockedProcesses = new() { "calc.exe" },
            WebRules = new() { new WebRule { Pattern = "reddit.com", Allow = false } },
            Schedule = BuildActiveFiniteWindowAroundNow(localNow)
        });
        db.StartSession(scheduledProfile.Id, 3600, locked: true);
        db.StartMeditationSession(900, new() { "notepad.exe" }, LockMode.Locked, soundscape: null, passwordHash: null, status: "Active");
        db.SaveMeditationSchedule(new MeditationSchedule
        {
            Enabled = true,
            DaysOfWeekJson = "[0,1,2,3,4,5,6]",
            TimeOfDay = "10:00",
            DurationSeconds = 900,
            LockMode = LockMode.Locked
        });

        Assert.True(UninstallGuard.CheckDatabaseForActiveBlock(dbPath, DateTime.UtcNow, localNow, out _));

        CommandHandler.ExecuteEmergencyUnlockReset(db, localNow, cleanHosts: false, resetDns: false);

        // Fresh BlockerDatabase instance verifies SQLite persistence without stale in-memory cache
        var reloadedDb = new BlockerDatabase(dbPath);
        var reloadedProfile = reloadedDb.GetProfileByName("RealDbScheduled");
        Assert.NotNull(reloadedProfile);
        Assert.False(reloadedProfile!.Enabled);
        Assert.Single(reloadedProfile.Schedule);
        Assert.NotNull(reloadedProfile.ScheduleBypassedUntilUtc);
        Assert.False(ProfileStateLogic.IsProfileActive(reloadedProfile, null, localNow, out _));
        Assert.False(ProfileStateLogic.IsProfileLockedNow(reloadedProfile, null, localNow));

        Assert.Null(reloadedDb.GetActiveSession());
        Assert.Null(reloadedDb.GetActiveMeditationSession());
        Assert.False(reloadedDb.GetMeditationSchedule()!.Enabled);
        Assert.False(UninstallGuard.CheckDatabaseForActiveBlock(dbPath, DateTime.UtcNow, localNow, out _));

        // Even if profile.Enabled is true while bypass is active, UninstallGuard respects bypass
        reloadedProfile.Enabled = true;
        reloadedDb.SaveProfile(reloadedProfile);
        Assert.False(UninstallGuard.CheckDatabaseForActiveBlock(dbPath, DateTime.UtcNow, localNow, out _));
    }

    [Fact]
    public void BlockerDatabase_MigratesLegacyProfilesTable_WithScheduleBypassedUntilUtc()
    {
        string dbPath = CreateTempDbPath();
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Profiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    BlockedProcessesJson TEXT NOT NULL,
                    BlockedDomainsJson TEXT NOT NULL DEFAULT '[]',
                    Enabled INTEGER NOT NULL DEFAULT 0,
                    ScheduleJson TEXT NOT NULL DEFAULT '[]',
                    LockMode INTEGER NOT NULL DEFAULT 0,
                    PasswordSalt TEXT NULL,
                    PasswordHash TEXT NULL,
                    WebRulesJson TEXT NOT NULL DEFAULT '[]',
                    BlockAllWebsites INTEGER NOT NULL DEFAULT 0,
                    SystemWide INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO Profiles (Name, BlockedProcessesJson, Enabled, ScheduleJson, LockMode)
                VALUES ('LegacyScheduled', '["notepad.exe"]', 1, '[{"Days":127,"StartMinute":0,"EndMinute":1439}]', 1);
                """;
            cmd.ExecuteNonQuery();
        }

        var migratedDb = new BlockerDatabase(dbPath);
        DateTime localNow = new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Local);
        migratedDb.DisableAllProfiles();
        migratedDb.BypassActiveSchedules(localNow);

        var loaded = migratedDb.GetProfileByName("LegacyScheduled");
        Assert.NotNull(loaded);
        Assert.False(loaded!.Enabled);
        Assert.NotNull(loaded.ScheduleBypassedUntilUtc);
        Assert.False(ProfileStateLogic.IsProfileActive(loaded, null, localNow, out _));
        Assert.False(ProfileStateLogic.IsProfileLockedNow(loaded, null, localNow));
    }
}
