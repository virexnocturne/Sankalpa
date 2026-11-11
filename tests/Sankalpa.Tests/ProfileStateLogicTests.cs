using System;
using System.Collections.Generic;
using System.Linq;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class ProfileStateLogicTests
{
    private class TestBlockStore : IBlockStore
    {
        public List<Profile> Profiles { get; } = new();
        public List<BlockSession> Sessions { get; } = new();
        public bool EmergencyUnlockExists { get; set; } = false;
        public bool EmergencyUnlockConsumed { get; set; } = false;
        public bool CleanHostsCalled { get; set; } = false;
        private int _nextProfileId = 1;

        public Profile? GetProfileByName(string name) => Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        public Profile? GetProfileById(int id) => Profiles.FirstOrDefault(p => p.Id == id);
        public List<Profile> ListProfiles() => Profiles.ToList();

        public Profile SaveProfile(Profile profile)
        {
            var existing = GetProfileByName(profile.Name);
            if (existing != null)
            {
                existing.BlockedProcesses = profile.BlockedProcesses;
                existing.BlockedDomains = profile.BlockedDomains;
                existing.WebRules = profile.WebRules;
                existing.BlockAllWebsites = profile.BlockAllWebsites;
                existing.Enabled = profile.Enabled;
                existing.Schedule = profile.Schedule;
                existing.LockMode = profile.LockMode;
                existing.PasswordSalt = profile.PasswordSalt;
                existing.PasswordHash = profile.PasswordHash;
                existing.ScheduleBypassedUntilUtc = profile.ScheduleBypassedUntilUtc;
                return existing;
            }

            var newProfile = new Profile
            {
                Id = _nextProfileId++,
                Name = profile.Name,
                BlockedProcesses = profile.BlockedProcesses,
                BlockedDomains = profile.BlockedDomains,
                WebRules = profile.WebRules,
                BlockAllWebsites = profile.BlockAllWebsites,
                Enabled = profile.Enabled,
                Schedule = profile.Schedule,
                LockMode = profile.LockMode,
                PasswordSalt = profile.PasswordSalt,
                PasswordHash = profile.PasswordHash
            };
            Profiles.Add(newProfile);
            return newProfile;
        }

        public void DisableAllProfiles()
        {
            foreach (var p in Profiles) p.Enabled = false;
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

        public bool RenameProfile(int id, string newName)
        {
            var p = GetProfileById(id);
            if (p != null) { p.Name = newName; return true; }
            return false;
        }

        public bool DeleteProfileByName(string name)
        {
            var p = GetProfileByName(name);
            if (p != null) { Profiles.Remove(p); return true; }
            return false;
        }

        public bool DeleteProfile(int id)
        {
            var p = GetProfileById(id);
            if (p != null) { Profiles.Remove(p); return true; }
            return false;
        }

        public List<BlockSession> GetActiveSessions() => Sessions.Where(s => s.EndedUtc == null && s.IsActive(DateTime.UtcNow)).ToList();
        public BlockSession? GetActiveSession() => Sessions.LastOrDefault(s => s.EndedUtc == null && s.IsActive(DateTime.UtcNow));
        public BlockSession? GetActiveSession(int? profileId)
        {
            if (profileId.HasValue) return Sessions.LastOrDefault(s => s.ProfileId == profileId.Value && s.EndedUtc == null && s.IsActive(DateTime.UtcNow));
            return GetActiveSession();
        }
        public BlockSession? GetSessionById(int id) => Sessions.FirstOrDefault(s => s.Id == id);
        public BlockSession StartSession(int profileId, long durationSeconds, bool locked) => throw new NotImplementedException();
        public void EndActiveSession() { }
        public void EndSession(int sessionId) { }
        public bool IsEmergencyUnlockPresent() => EmergencyUnlockExists;
        public void ConsumeEmergencyUnlock() => EmergencyUnlockConsumed = true;
        public bool CleanHosts() { CleanHostsCalled = true; return true; }

        public bool ServiceElevated { get; set; } = true;
        public bool HostsBlockingOk { get; set; } = true;
        public HostsState HostsState { get; set; } = HostsState.Idle;
        public int HostsDomainCount { get; set; } = 0;
        public string? HostsLastError { get; set; }
        public List<string> CurrentHostsDomains { get; set; } = new();
        public List<string> GetCurrentHostsDomains() => CurrentHostsDomains.ToList();

        public List<BlockEvent> Events { get; } = new();
        public void RecordBlockEvent(BlockEvent evt) => Events.Add(evt);
        public List<BlockEvent> GetBlockEvents(long sinceId) => Events.Where(e => e.Id > sinceId).ToList();

        public FallbackPlanner FallbackPlanner { get; } = new();
        public FallbackPlanResult? LastFallbackPlan { get; set; }

        public DnsConfig DnsSettings { get; set; } = new();
        public DnsConfig GetDnsConfig() => DnsSettings;
        public void SaveDnsConfig(DnsConfig config) => DnsSettings = config;
    }

    [Fact]
    public void IsProfileActive_WhenManualSessionRunning_ReturnsTrueManual()
    {
        var profile = new Profile { Id = 1, Name = "Study", Enabled = false };
        var session = new BlockSession { ProfileId = 1 };

        bool active = ProfileStateLogic.IsProfileActive(profile, session, DateTime.Now, out string reason);
        Assert.True(active);
        Assert.Equal("Manual", reason);
    }

    [Fact]
    public void IsProfileActive_WhenDisabledAndNoSession_ReturnsFalse()
    {
        var profile = new Profile { Id = 1, Name = "Study", Enabled = false };
        bool active = ProfileStateLogic.IsProfileActive(profile, null, DateTime.Now, out string reason);
        Assert.False(active);
        Assert.Equal("None", reason);
    }

    [Fact]
    public void IsProfileActive_WhenEnabledWithNoSchedule_ReturnsTrueAlwaysOn()
    {
        var profile = new Profile { Id = 1, Name = "Study", Enabled = true, Schedule = new() };
        bool active = ProfileStateLogic.IsProfileActive(profile, null, DateTime.Now, out string reason);
        Assert.True(active);
        Assert.Equal("AlwaysOn", reason);
    }

    [Fact]
    public void IsProfileActive_WhenEnabledWithSchedule_EvaluatesSchedule()
    {
        var profile = new Profile
        {
            Id = 1,
            Name = "Work",
            Enabled = true,
            Schedule = new()
            {
                new() { Days = DaysOfWeek.Mon, StartMinute = 540, EndMinute = 1020 } // Mon 09:00 - 17:00
            }
        };

        DateTime monDuring = new DateTime(2026, 11, 2, 11, 0, 0); // Monday
        bool active1 = ProfileStateLogic.IsProfileActive(profile, null, monDuring, out string reason1);
        Assert.True(active1);
        Assert.Equal("Schedule", reason1);

        DateTime tueDuring = new DateTime(2026, 11, 3, 11, 0, 0); // Tuesday
        bool active2 = ProfileStateLogic.IsProfileActive(profile, null, tueDuring, out string reason2);
        // Under Patch 10: outside schedule window, an enabled profile remains active (AlwaysOn) but unlocked & editable
        Assert.True(active2);
        Assert.Equal("AlwaysOn", reason2);

        // Outside schedule window, profile is unlocked
        bool lockedOutside = ProfileStateLogic.IsProfileLockedNow(profile, null, tueDuring);
        Assert.False(lockedOutside);

        // Inside schedule window, profile is strictly locked
        bool lockedInside = ProfileStateLogic.IsProfileLockedNow(profile, null, monDuring);
        Assert.True(lockedInside);
    }

    [Fact]
    public void IsProfileActive_WhenDisabledWithActiveSchedule_ReturnsTrueSchedule()
    {
        var profile = new Profile
        {
            Id = 1,
            Name = "Work",
            Enabled = false,
            Schedule = new()
            {
                new() { Days = DaysOfWeek.Mon, StartMinute = 540, EndMinute = 1020 } // Mon 09:00 - 17:00
            }
        };

        DateTime monDuring = new DateTime(2026, 11, 2, 11, 0, 0); // Monday
        bool active = ProfileStateLogic.IsProfileActive(profile, null, monDuring, out string reason);
        Assert.True(active);
        Assert.Equal("Schedule", reason);

        bool activeWithSessions = ProfileStateLogic.IsProfileActiveWithSessions(profile, null, monDuring, out string reasonSessions);
        Assert.True(activeWithSessions);
        Assert.Equal("Schedule", reasonSessions);
    }

    [Fact]
    public void IsProfileActive_WhenDisabledWithInactiveSchedule_ReturnsFalse()
    {
        var profile = new Profile
        {
            Id = 1,
            Name = "Work",
            Enabled = false,
            Schedule = new()
            {
                new() { Days = DaysOfWeek.Mon, StartMinute = 540, EndMinute = 1020 } // Mon 09:00 - 17:00
            }
        };

        DateTime monAfter = new DateTime(2026, 11, 2, 18, 0, 0); // Monday evening
        bool active1 = ProfileStateLogic.IsProfileActive(profile, null, monAfter, out string reason1);
        Assert.False(active1);
        Assert.Equal("None", reason1);

        DateTime tueDuring = new DateTime(2026, 11, 3, 11, 0, 0); // Tuesday
        bool active2 = ProfileStateLogic.IsProfileActive(profile, null, tueDuring, out string reason2);
        Assert.False(active2);
        Assert.Equal("None", reason2);
    }

    [Fact]
    public void ComputeActiveUnion_CombinesAllActiveProfiles()
    {
        var p1 = new Profile
        {
            Id = 1,
            Name = "Work",
            Enabled = true,
            BlockedProcesses = new() { "discord.exe", "slack.exe" },
            BlockedDomains = new() { "reddit.com" }
        };

        var p2 = new Profile
        {
            Id = 2,
            Name = "Gaming",
            Enabled = true,
            BlockedProcesses = new() { "steam.exe", "discord.exe" },
            BlockedDomains = new() { "twitch.tv", "reddit.com" }
        };

        var p3 = new Profile
        {
            Id = 3,
            Name = "Off",
            Enabled = false,
            BlockedProcesses = new() { "notepad.exe" },
            BlockedDomains = new() { "example.com" }
        };

        var (apps, domains) = ProfileStateLogic.ComputeActiveUnion(new[] { p1, p2, p3 }, null, DateTime.Now);

        Assert.Equal(3, apps.Count);
        Assert.Contains("discord.exe", apps, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("slack.exe", apps, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("steam.exe", apps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("notepad.exe", apps, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, domains.Count);
        Assert.Contains("reddit.com", domains, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("twitch.tv", domains, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", domains, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsProfileLockedNow_OnlyTrueWhenActiveAndLockModeNotNone()
    {
        var p1 = new Profile { Id = 1, Enabled = true, LockMode = LockMode.None };
        Assert.False(ProfileStateLogic.IsProfileLockedNow(p1, null, DateTime.Now));

        var p2 = new Profile { Id = 2, Enabled = true, LockMode = LockMode.Locked };
        Assert.True(ProfileStateLogic.IsProfileLockedNow(p2, null, DateTime.Now));

        var p3 = new Profile { Id = 3, Enabled = false, LockMode = LockMode.Locked };
        Assert.False(ProfileStateLogic.IsProfileLockedNow(p3, null, DateTime.Now));
    }

    [Fact]
    public void IsProfileLockedNow_WithSchedule_LockedDuringActiveWindow_EvenWhenLockModeNone()
    {
        var profile = new Profile
        {
            Id = 1,
            Name = "Work",
            Enabled = true,
            LockMode = LockMode.None,
            Schedule = new()
            {
                new() { Days = DaysOfWeek.Mon, StartMinute = 540, EndMinute = 1020 } // Mon 09:00 - 17:00
            }
        };

        DateTime monDuring = new DateTime(2026, 11, 2, 11, 0, 0); // Monday 11:00 AM
        bool locked = ProfileStateLogic.IsProfileLockedNow(profile, null, monDuring);
        Assert.True(locked);

        bool lockedWithSessions = ProfileStateLogic.IsProfileLockedNowWithSessions(profile, null, monDuring);
        Assert.True(lockedWithSessions);
    }

    [Fact]
    public void IsProfileLockedNow_WithSchedule_NotLockedOutsideWindow()
    {
        var profile = new Profile
        {
            Id = 1,
            Name = "Work",
            Enabled = true,
            LockMode = LockMode.None,
            Schedule = new()
            {
                new() { Days = DaysOfWeek.Mon, StartMinute = 540, EndMinute = 1020 } // Mon 09:00 - 17:00
            }
        };

        DateTime monAfter = new DateTime(2026, 11, 2, 18, 0, 0); // Monday 18:00
        bool locked = ProfileStateLogic.IsProfileLockedNow(profile, null, monAfter);
        Assert.False(locked);

        DateTime tueDuring = new DateTime(2026, 11, 3, 11, 0, 0); // Tuesday 11:00 AM
        bool lockedTue = ProfileStateLogic.IsProfileLockedNow(profile, null, tueDuring);
        Assert.False(lockedTue);
    }

    [Fact]
    public void IsProfileLockedNow_WithSchedule_LockedDuringActiveWindow_EvenIfDisabled()
    {
        var profile = new Profile
        {
            Id = 1,
            Name = "Work",
            Enabled = false,
            LockMode = LockMode.None,
            Schedule = new()
            {
                new() { Days = DaysOfWeek.Mon, StartMinute = 540, EndMinute = 1020 } // Mon 09:00 - 17:00
            }
        };

        DateTime monDuring = new DateTime(2026, 11, 2, 11, 0, 0); // Monday 11:00 AM
        bool locked = ProfileStateLogic.IsProfileLockedNow(profile, null, monDuring);
        Assert.True(locked);

        bool lockedWithSessions = ProfileStateLogic.IsProfileLockedNowWithSessions(profile, null, monDuring);
        Assert.True(lockedWithSessions);

        DateTime monAfter = new DateTime(2026, 11, 2, 18, 0, 0); // Monday 18:00
        bool lockedAfter = ProfileStateLogic.IsProfileLockedNow(profile, null, monAfter);
        Assert.False(lockedAfter);
    }

    [Fact]
    public void CommandHandler_DisableProfile_DuringActiveSchedule_IsRefused()
    {
        var store = new TestBlockStore();
        store.Profiles.Add(new Profile
        {
            Id = 1,
            Name = "ScheduledBlock",
            Enabled = true,
            LockMode = LockMode.None,
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        var req = new Request { Command = "DISABLEPROFILE", Args = new() { ["name"] = "ScheduledBlock" } };
        var res = CommandHandler.Handle(req, store, ClientIdentity.StandardUser());

        Assert.False(res.Ok);
        Assert.StartsWith("Refused: Profile 'ScheduledBlock' is locked until", res.Error);
    }

    [Fact]
    public void CommandHandler_SaveProfile_DuringActiveSchedule_IsRefused()
    {
        var store = new TestBlockStore();
        store.Profiles.Add(new Profile
        {
            Id = 1,
            Name = "ScheduledBlock",
            Enabled = true,
            LockMode = LockMode.None,
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        var req = new Request { Command = "SAVEPROFILE", Args = new() { ["name"] = "ScheduledBlock", ["block"] = "notepad.exe" } };
        var res = CommandHandler.Handle(req, store, ClientIdentity.StandardUser());

        Assert.False(res.Ok);
        Assert.StartsWith("Refused: Profile 'ScheduledBlock' is locked until", res.Error);
    }

    [Fact]
    public void CommandHandler_DisableProfile_DuringActiveSchedule_WithPasswordOrChallenge_IsRefused()
    {
        var store = new TestBlockStore();
        PasswordSecurity.HashPassword("secret123", out string salt, out string hash);
        store.Profiles.Add(new Profile
        {
            Id = 1,
            Name = "SchedPwd",
            Enabled = true,
            LockMode = LockMode.Password,
            PasswordSalt = salt,
            PasswordHash = hash,
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        var reqPwd = new Request
        {
            Command = "DISABLEPROFILE",
            Args = new() { ["name"] = "SchedPwd", ["unlock_password"] = "secret123" }
        };
        var resPwd = CommandHandler.Handle(reqPwd, store, ClientIdentity.StandardUser());
        Assert.False(resPwd.Ok);
        Assert.StartsWith("Refused: Profile 'SchedPwd' is locked until", resPwd.Error);

        store.Profiles.Add(new Profile
        {
            Id = 2,
            Name = "SchedTyping",
            Enabled = true,
            LockMode = LockMode.TypingChallenge,
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        string challenge = TypingChallengeManager.GenerateChallenge("SchedTyping");
        var reqTyping = new Request
        {
            Command = "DISABLEPROFILE",
            Args = new() { ["name"] = "SchedTyping", ["unlock_text"] = challenge }
        };
        var resTyping = CommandHandler.Handle(reqTyping, store, ClientIdentity.StandardUser());
        Assert.False(resTyping.Ok);
        Assert.StartsWith("Refused: Profile 'SchedTyping' is locked until", resTyping.Error);
    }

    [Fact]
    public void CommandHandler_SaveProfile_DuringActiveSchedule_NonAdditive_IsRefusedEvenWithPassword()
    {
        var store = new TestBlockStore();
        PasswordSecurity.HashPassword("secret123", out string salt, out string hash);
        store.Profiles.Add(new Profile
        {
            Id = 1,
            Name = "SchedNonAdditive",
            Enabled = true,
            LockMode = LockMode.Password,
            PasswordSalt = salt,
            PasswordHash = hash,
            BlockedProcesses = new() { "notepad.exe" },
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        var req = new Request
        {
            Command = "SAVEPROFILE",
            Args = new()
            {
                ["name"] = "SchedNonAdditive",
                ["processes"] = "calc.exe", // removes notepad.exe
                ["unlock_password"] = "secret123"
            }
        };
        var res = CommandHandler.Handle(req, store, ClientIdentity.StandardUser());
        Assert.False(res.Ok);
        Assert.StartsWith("Refused: Profile 'SchedNonAdditive' is locked until", res.Error);
    }

    [Fact]
    public void CommandHandler_DisableProfile_InactiveSchedule_EnforcesPassword()
    {
        var store = new TestBlockStore();
        PasswordSecurity.HashPassword("secret123", out string salt, out string hash);
        var notToday = DateTime.Now.DayOfWeek == DayOfWeek.Sunday ? DaysOfWeek.Mon : DaysOfWeek.Sun;
        store.Profiles.Add(new Profile
        {
            Id = 1,
            Name = "InactiveSched",
            Enabled = true,
            LockMode = LockMode.Password,
            PasswordSalt = salt,
            PasswordHash = hash,
            Schedule = new() { new ScheduleRule { Days = notToday, StartMinute = 0, EndMinute = 1439 } }
        });

        var reqNoPwd = new Request
        {
            Command = "DISABLEPROFILE",
            Args = new() { ["name"] = "InactiveSched" }
        };
        var resNoPwd = CommandHandler.Handle(reqNoPwd, store, ClientIdentity.StandardUser());
        Assert.False(resNoPwd.Ok);
        Assert.Contains("requires an unlock password", resNoPwd.Error);

        var reqOk = new Request
        {
            Command = "DISABLEPROFILE",
            Args = new() { ["name"] = "InactiveSched", ["unlock_password"] = "secret123" }
        };
        var resOk = CommandHandler.Handle(reqOk, store, ClientIdentity.StandardUser());
        Assert.True(resOk.Ok);
        Assert.False(store.GetProfileByName("InactiveSched")!.Enabled);
    }

    [Fact]
    public void CommandHandler_CancelDelayedUnlock_MissingToken_IsRefused()
    {
        var req = new Request
        {
            Command = "CANCELDELAYEDUNLOCK",
            Args = new() { ["name"] = "AnyProfile" }
        };
        var res = CommandHandler.Handle(req, new TestBlockStore(), ClientIdentity.StandardUser());
        Assert.False(res.Ok);
        Assert.Equal("Missing required argument: 'token'.", res.Error);
    }
}
