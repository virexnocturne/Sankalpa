using System.Text.Json;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class CommandHandlerTests
{
    private static Response Handle(Request? request, IBlockStore store, IClientIdentity? clientIdentity = null)
        => CommandHandler.Handle(request, store, clientIdentity ?? ClientIdentity.StandardUser());

    private class FakeBlockStore : IBlockStore
    {
        public List<Profile> Profiles { get; } = new();
        public List<BlockSession> Sessions { get; } = new();
        public bool EmergencyUnlockExists { get; set; } = false;
        public bool EmergencyUnlockConsumed { get; set; } = false;
        public bool CleanHostsCalled { get; set; } = false;
        private int _nextProfileId = 1;
        private int _nextSessionId = 1;

        public Profile? GetProfileByName(string name)
        {
            return Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public Profile? GetProfileById(int id)
        {
            return Profiles.FirstOrDefault(p => p.Id == id);
        }

        public List<Profile> ListProfiles()
        {
            return Profiles.ToList();
        }

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
            foreach (var p in Profiles)
            {
                p.Enabled = false;
            }
        }

        public bool RenameProfile(int id, string newName)
        {
            var p = GetProfileById(id);
            if (p != null)
            {
                p.Name = newName;
                return true;
            }
            return false;
        }

        public bool DeleteProfileByName(string name)
        {
            var p = GetProfileByName(name);
            if (p != null)
            {
                Profiles.Remove(p);
                return true;
            }
            return false;
        }

        public bool DeleteProfile(int id)
        {
            var p = GetProfileById(id);
            if (p != null)
            {
                Profiles.Remove(p);
                return true;
            }
            return false;
        }

        public List<BlockSession> GetActiveSessions()
        {
            return Sessions.Where(s => s.EndedUtc == null && s.IsActive(DateTime.UtcNow)).ToList();
        }

        public BlockSession? GetActiveSession()
        {
            var active = Sessions.LastOrDefault(s => s.EndedUtc == null);
            if (active != null && active.IsActive(DateTime.UtcNow))
            {
                return active;
            }
            return null;
        }

        public BlockSession? GetActiveSession(int? profileId)
        {
            if (profileId.HasValue)
            {
                return Sessions.LastOrDefault(s => s.ProfileId == profileId.Value && s.EndedUtc == null && s.IsActive(DateTime.UtcNow));
            }
            return GetActiveSession();
        }

        public BlockSession? GetSessionById(int id)
        {
            return Sessions.FirstOrDefault(s => s.Id == id);
        }

        public BlockSession StartSession(int profileId, long durationSeconds, bool locked)
        {
            foreach (var s in Sessions.Where(s => s.ProfileId == profileId && s.EndedUtc == null))
            {
                s.EndedUtc = DateTime.UtcNow;
            }

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

        public bool ServiceElevated { get; set; } = true;
        public bool HostsBlockingOk { get; set; } = true;
        public HostsState HostsState { get; set; } = HostsState.Idle;
        public int HostsDomainCount { get; set; } = 0;
        public string? HostsLastError { get; set; }
        public List<string> CurrentHostsDomains { get; set; } = new();
        public List<string> GetCurrentHostsDomains() => CurrentHostsDomains.ToList();

        public List<BlockEvent> Events { get; } = new();
        public void RecordBlockEvent(BlockEvent evt)
        {
            evt.Id = Events.Count + 1;
            Events.Add(evt);
        }
        public List<BlockEvent> GetBlockEvents(long sinceId) => Events.Where(e => e.Id > sinceId).ToList();

        public FallbackPlanner FallbackPlanner { get; } = new();
        public FallbackPlanResult? LastFallbackPlan { get; set; }

        public DnsConfig DnsSettings { get; set; } = new();
        public DnsConfig GetDnsConfig() => DnsSettings;
        public void SaveDnsConfig(DnsConfig config) => DnsSettings = config;
    }

    [Fact]
    public void Ping_ReturnsOk_WithPong()
    {
        var store = new FakeBlockStore();
        var resp = Handle(new Request { Command = CommandNames.Ping }, store);

        Assert.True(resp.Ok);
        Assert.Equal("pong", resp.Data);
    }

    [Fact]
    public void UnknownCommand_ReturnsFail()
    {
        var store = new FakeBlockStore();
        var resp = Handle(new Request { Command = "NonExistentCommand" }, store);

        Assert.False(resp.Ok);
        Assert.Contains("Unknown command", resp.Error);
    }

    [Fact]
    public void EmptyOrNullRequest_ReturnsFail()
    {
        var store = new FakeBlockStore();
        var resp1 = Handle(null, store);
        Assert.False(resp1.Ok);

        var resp2 = Handle(new Request { Command = "" }, store);
        Assert.False(resp2.Ok);
    }

    [Fact]
    public void LockedStop_Refused_WithTimeRemaining()
    {
        var store = new FakeBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "Focus" });
        store.StartSession(profile.Id, 3600, locked: true);

        var resp = Handle(new Request { Command = CommandNames.StopSession }, store);

        Assert.False(resp.Ok);
        Assert.Contains("Stop refused", resp.Error);
        Assert.Contains("remaining", resp.Error, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(store.GetActiveSession());
    }

    [Fact]
    public void LockedStop_EmergencyOverride_Allowed()
    {
        var store = new FakeBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "Focus" });
        store.StartSession(profile.Id, 3600, locked: true);
        store.EmergencyUnlockExists = true;

        var resp = Handle(new Request { Command = CommandNames.StopSession }, store);

        Assert.True(resp.Ok);
        Assert.True(store.EmergencyUnlockConsumed);
        Assert.Null(store.GetActiveSession());
        Assert.True(store.CleanHostsCalled);
    }

    [Fact]
    public void LockedProfile_Edit_Refused()
    {
        var store = new FakeBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "Focus", BlockedProcesses = new() { "notepad.exe" } });
        store.StartSession(profile.Id, 3600, locked: true);

        var resp = Handle(new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "Focus",
                ["processes"] = "calc.exe"
            }
        }, store);

        Assert.False(resp.Ok);
        Assert.Contains("Cannot edit profile 'Focus' while an active LOCKED session is using it", resp.Error);
    }

    [Fact]
    public void LockedProfile_Delete_Refused()
    {
        var store = new FakeBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "Focus" });
        store.StartSession(profile.Id, 3600, locked: true);

        var resp = Handle(new Request
        {
            Command = CommandNames.DeleteProfile,
            Args = new() { ["name"] = "Focus" }
        }, store);

        Assert.False(resp.Ok);
        Assert.Contains("Cannot delete profile 'Focus' while an active LOCKED session is using it", resp.Error);
        Assert.NotNull(store.GetProfileByName("Focus"));
    }

    [Fact]
    public void ActiveUnlockedSession_Delete_Refused()
    {
        var store = new FakeBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "ActiveUnlocked" });
        store.StartSession(profile.Id, 1800, locked: false);

        var resp = Handle(new Request
        {
            Command = CommandNames.DeleteProfile,
            Args = new() { ["name"] = "ActiveUnlocked" }
        }, store);

        Assert.False(resp.Ok);
        Assert.Contains("while an active session is running for it", resp.Error);
        Assert.NotNull(store.GetProfileByName("ActiveUnlocked"));
    }

    [Fact]
    public void EnabledProfile_Delete_Refused()
    {
        var store = new FakeBlockStore();
        store.SaveProfile(new Profile { Name = "TurnedOn", Enabled = true });

        var resp = Handle(new Request
        {
            Command = CommandNames.DeleteProfile,
            Args = new() { ["name"] = "TurnedOn" }
        }, store);

        Assert.False(resp.Ok);
        Assert.Contains("while it is turned On", resp.Error);
        Assert.NotNull(store.GetProfileByName("TurnedOn"));
    }

    [Fact]
    public void UnlockedProfile_CanBeEditedAndDeleted()
    {
        var store = new FakeBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "Normal" });

        var editResp = Handle(new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new() { ["name"] = "Normal", ["processes"] = "cmd.exe" }
        }, store);
        Assert.True(editResp.Ok);

        var deleteResp = Handle(new Request
        {
            Command = CommandNames.DeleteProfile,
            Args = new() { ["name"] = "Normal" }
        }, store);
        Assert.True(deleteResp.Ok);
        Assert.Null(store.GetProfileByName("Normal"));
    }

    [Fact]
    public void BadArgs_SaveProfile_EmptyName_ReturnsFail()
    {
        var store = new FakeBlockStore();
        var resp = Handle(new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new() { ["name"] = "   " }
        }, store);

        Assert.False(resp.Ok);
        Assert.Contains("Profile name cannot be empty", resp.Error);
    }

    [Fact]
    public void BadArgs_StartSession_NegativeDuration_ReturnsFail()
    {
        var store = new FakeBlockStore();
        store.SaveProfile(new Profile { Name = "Default" });

        var resp = Handle(new Request
        {
            Command = CommandNames.StartSession,
            Args = new() { ["profile"] = "Default", ["seconds"] = "-10" }
        }, store);

        Assert.False(resp.Ok);
        Assert.Contains("Duration must be greater than zero", resp.Error);
    }

    [Fact]
    public void GetStatus_ReturnsFormattedStatus()
    {
        var store = new FakeBlockStore();
        var statusIdle = Handle(new Request { Command = CommandNames.GetStatus }, store);
        Assert.True(statusIdle.Ok);
        Assert.Contains("\"Active\":false", statusIdle.Data);

        var prof = store.SaveProfile(new Profile { Name = "ActiveWork", BlockedProcesses = new() { "notepad.exe" } });
        store.StartSession(prof.Id, 1800, locked: true);

        var statusActive = Handle(new Request { Command = CommandNames.GetStatus }, store);
        Assert.True(statusActive.Ok);
        Assert.Contains("\"Active\":true", statusActive.Data);
        Assert.Contains("ActiveWork", statusActive.Data);
    }

    [Fact]
    public void GetStatus_ReturnsElevationAndHostsStatus()
    {
        var store = new FakeBlockStore
        {
            ServiceElevated = false,
            HostsBlockingOk = false,
            HostsState = HostsState.Error,
            HostsDomainCount = 0,
            HostsLastError = "UnauthorizedAccessException: Access to the path is denied."
        };

        var resp = Handle(new Request { Command = CommandNames.GetStatus }, store);
        Assert.True(resp.Ok);
        Assert.NotNull(resp.Data);

        var status = JsonSerializer.Deserialize<StatusData>(resp.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(status);
        Assert.False(status.ServiceElevated);
        Assert.False(status.HostsBlockingOk);
        Assert.Equal(HostsState.Error, status.HostsState);
        Assert.Equal(0, status.HostsDomainCount);
        Assert.Equal("UnauthorizedAccessException: Access to the path is denied.", status.HostsLastError);
    }

    [Fact]
    public void GetStatus_NotElevated_IndependentHostsState()
    {
        var store = new FakeBlockStore
        {
            ServiceElevated = false,
            HostsBlockingOk = true,
            HostsState = HostsState.Blocking,
            HostsDomainCount = 2,
            HostsLastError = null
        };
        var profile = store.SaveProfile(new Profile
        {
            Name = "WebBlock",
            BlockedDomains = new List<string> { "reddit.com" }
        });
        store.StartSession(profile.Id, 3600, locked: false);

        var resp = Handle(new Request { Command = CommandNames.GetStatus }, store);
        Assert.True(resp.Ok);
        Assert.NotNull(resp.Data);

        var status = JsonSerializer.Deserialize<StatusData>(resp.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(status);
        Assert.False(status.ServiceElevated);
        Assert.True(status.HostsBlockingOk);
        Assert.Equal(HostsState.Blocking, status.HostsState);
        Assert.Equal(2, status.HostsDomainCount);
        Assert.Null(status.HostsLastError);
    }

    [Fact]
    public void GetEvents_ReturnsEventsGreaterThanSinceId()
    {
        var store = new FakeBlockStore();
        store.RecordBlockEvent(new BlockEvent { Kind = "App", Name = "notepad.exe", ProfileName = "Default" });
        store.RecordBlockEvent(new BlockEvent { Kind = "App", Name = "discord.exe", ProfileName = "Default" });
        store.RecordBlockEvent(new BlockEvent { Kind = "App", Name = "game.exe", ProfileName = "Default" });

        var req = new Request
        {
            Command = CommandNames.GetEvents,
            Args = new() { ["sinceId"] = "1" }
        };

        var resp = Handle(req, store);
        Assert.True(resp.Ok);
        Assert.NotNull(resp.Data);

        var events = JsonSerializer.Deserialize<List<BlockEvent>>(resp.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(events);
        Assert.Equal(2, events.Count);
        Assert.Equal("discord.exe", events[0].Name);
        Assert.Equal("game.exe", events[1].Name);
    }

    [Fact]
    public void SaveProfile_LockedMode_RequiresSchedule_FailsWhenNoSchedule()
    {
        var store = new FakeBlockStore();
        var req = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "StrictStudy",
                ["lock_mode"] = "Locked"
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("requires at least one schedule rule", resp.Error);
    }

    [Fact]
    public void SaveProfile_LockedMode_SucceedsWhenHasSchedule()
    {
        var store = new FakeBlockStore();
        var req = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "StrictStudy",
                ["lock_mode"] = "Locked",
                ["schedule"] = "[{\"Days\":31,\"StartMinute\":480,\"EndMinute\":720}]"
            }
        };

        var resp = Handle(req, store);
        Assert.True(resp.Ok);
        var p = store.GetProfileByName("StrictStudy");
        Assert.NotNull(p);
        Assert.Equal(LockMode.Locked, p.LockMode);
        Assert.Single(p.Schedule);
    }

    [Fact]
    public void SaveProfile_ActiveProfileLockedModeLocked_RefusesEdit()
    {
        var store = new FakeBlockStore();
        // Create active profile covering all times (0-1439 every day)
        var p = store.SaveProfile(new Profile
        {
            Name = "ActiveLocked",
            Enabled = true,
            LockMode = LockMode.Locked,
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        var req = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "ActiveLocked",
                ["block"] = "discord.exe"
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("is locked until", resp.Error);
    }

    [Fact]
    public void SaveProfile_AdditiveOnly_AllowsAddingBlockedWebsitesAndProcesses_WhenLocked()
    {
        var store = new FakeBlockStore();
        store.SaveProfile(new Profile
        {
            Name = "LockedAdditive",
            Enabled = true,
            LockMode = LockMode.Locked,
            BlockedProcesses = new List<string> { "game.exe" },
            WebRules = new List<WebRule> { new WebRule { Pattern = "youtube.com", Allow = false } },
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        // Additive save: preserves game.exe and youtube.com, adds twitch.tv and discord.exe
        var req = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "LockedAdditive",
                ["additive_only"] = "true",
                ["processes"] = "game.exe,discord.exe",
                ["webrules"] = JsonSerializer.Serialize(new List<WebRule>
                {
                    new WebRule { Pattern = "youtube.com", Allow = false },
                    new WebRule { Pattern = "twitch.tv", Allow = false }
                })
            }
        };

        var resp = Handle(req, store);
        Assert.True(resp.Ok, resp.Error);

        var updated = store.GetProfileByName("LockedAdditive");
        Assert.NotNull(updated);
        Assert.Contains("game.exe", updated.BlockedProcesses);
        Assert.Contains("discord.exe", updated.BlockedProcesses);
        Assert.Equal(2, updated.WebRules.Count);
        Assert.Contains(updated.WebRules, r => r.Pattern == "youtube.com" && !r.Allow);
        Assert.Contains(updated.WebRules, r => r.Pattern == "twitch.tv" && !r.Allow);
    }

    [Fact]
    public void SaveProfile_AdditiveOnly_RejectsRemovingExistingBlockedItems_WhenLocked()
    {
        var store = new FakeBlockStore();
        store.SaveProfile(new Profile
        {
            Name = "LockedAdditiveRemove",
            Enabled = true,
            LockMode = LockMode.Locked,
            BlockedProcesses = new List<string> { "game.exe", "discord.exe" },
            WebRules = new List<WebRule>
            {
                new WebRule { Pattern = "youtube.com", Allow = false },
                new WebRule { Pattern = "reddit.com", Allow = false }
            },
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        // Attempt to remove discord.exe and reddit.com while in additive mode
        var req = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "LockedAdditiveRemove",
                ["additive_only"] = "true",
                ["processes"] = "game.exe", // missing discord.exe
                ["webrules"] = JsonSerializer.Serialize(new List<WebRule>
                {
                    new WebRule { Pattern = "youtube.com", Allow = false } // missing reddit.com
                })
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("cannot remove", resp.Error);
    }

    [Fact]
    public void SaveProfile_AdditiveOnly_RejectsAddingExceptions_WhenLocked()
    {
        var store = new FakeBlockStore();
        store.SaveProfile(new Profile
        {
            Name = "LockedAdditiveException",
            Enabled = true,
            LockMode = LockMode.Locked,
            BlockedProcesses = new List<string> { "game.exe" },
            WebRules = new List<WebRule>
            {
                new WebRule { Pattern = "youtube.com", Allow = false }
            },
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        // Attempt to sneak in an exception (Allow == true)
        var req = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "LockedAdditiveException",
                ["additive_only"] = "true",
                ["processes"] = "game.exe",
                ["webrules"] = JsonSerializer.Serialize(new List<WebRule>
                {
                    new WebRule { Pattern = "youtube.com", Allow = false },
                    new WebRule { Pattern = "youtube.com/watch*", Allow = true }
                })
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("exceptions cannot be added", resp.Error);
    }

    [Fact]
    public void SaveProfile_ActiveProfileTypingChallenge_RefusesWithoutValidChallenge_SucceedsWithValid()
    {
        var store = new FakeBlockStore();
        var p = store.SaveProfile(new Profile
        {
            Name = "ActiveTyping",
            Enabled = true,
            LockMode = LockMode.TypingChallenge,
            Schedule = new()
        });

        // 1. Without challenge
        var req1 = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new() { ["name"] = "ActiveTyping", ["block"] = "discord.exe" }
        };
        var resp1 = Handle(req1, store);
        Assert.False(resp1.Ok);
        Assert.Contains("typing challenge", resp1.Error);

        // 2. Request challenge
        var chReq = new Request
        {
            Command = CommandNames.GetUnlockChallenge,
            Args = new() { ["name"] = "ActiveTyping" }
        };
        var chResp = Handle(chReq, store);
        Assert.True(chResp.Ok);
        string challenge = chResp.Data!;
        Assert.Equal(80, challenge.Length);

        // 3. With wrong challenge
        var reqWrong = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new() { ["name"] = "ActiveTyping", ["unlock_text"] = "wrong", ["block"] = "discord.exe" }
        };
        var respWrong = Handle(reqWrong, store);
        Assert.False(respWrong.Ok);

        // Re-request challenge because failed validate consumed previous entry
        var chResp2 = Handle(chReq, store);
        challenge = chResp2.Data!;

        // 4. With correct challenge
        var reqOk = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new() { ["name"] = "ActiveTyping", ["unlock_text"] = challenge, ["block"] = "discord.exe" }
        };
        var respOk = Handle(reqOk, store);
        Assert.True(respOk.Ok);
    }

    [Fact]
    public void SaveProfile_ActiveProfilePassword_RefusesWithoutOrWrongPassword_SucceedsWithCorrect()
    {
        var store = new FakeBlockStore();
        PasswordSecurity.HashPassword("secret123", out string salt, out string hash);
        var p = store.SaveProfile(new Profile
        {
            Name = "ActivePwd",
            Enabled = true,
            LockMode = LockMode.Password,
            PasswordSalt = salt,
            PasswordHash = hash,
            Schedule = new()
        });

        // 1. Without password
        var req1 = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new() { ["name"] = "ActivePwd", ["block"] = "discord.exe" }
        };
        var resp1 = Handle(req1, store);
        Assert.False(resp1.Ok);
        Assert.Contains("requires an unlock password", resp1.Error);

        // 2. With wrong password
        var reqWrong = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new() { ["name"] = "ActivePwd", ["unlock_password"] = "wrong", ["block"] = "discord.exe" }
        };
        var respWrong = Handle(reqWrong, store);
        Assert.False(respWrong.Ok);
        Assert.Contains("Incorrect unlock password", respWrong.Error);

        // 3. With correct password
        var reqOk = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new() { ["name"] = "ActivePwd", ["unlock_password"] = "secret123", ["block"] = "discord.exe" }
        };
        var respOk = Handle(reqOk, store);
        Assert.True(respOk.Ok);
    }

    [Fact]
    public void SaveProfile_SwitchAwayFromPassword_RequiresCurrentPassword()
    {
        var store = new FakeBlockStore();
        PasswordSecurity.HashPassword("secret123", out string salt, out string hash);
        var p = store.SaveProfile(new Profile
        {
            Name = "InactivePwd",
            Enabled = false,
            LockMode = LockMode.Password,
            PasswordSalt = salt,
            PasswordHash = hash
        });

        // Attempt to switch to None without password
        var req1 = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new() { ["name"] = "InactivePwd", ["lock_mode"] = "None" }
        };
        var resp1 = Handle(req1, store);
        Assert.False(resp1.Ok);
        Assert.Contains("Current password required to change lock mode from Password", resp1.Error);

        // With correct password
        var reqOk = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new() { ["name"] = "InactivePwd", ["lock_mode"] = "None", ["current_password"] = "secret123" }
        };
        var respOk = Handle(reqOk, store);
        Assert.True(respOk.Ok);
        var updated = store.GetProfileByName("InactivePwd");
        Assert.Equal(LockMode.None, updated!.LockMode);
        Assert.Null(updated.PasswordHash);
    }

    [Fact]
    public void StopSession_EmergencyUnlock_DisablesAllProfilesAndStops()
    {
        var store = new FakeBlockStore
        {
            EmergencyUnlockExists = true
        };
        var p1 = store.SaveProfile(new Profile { Name = "P1", Enabled = true });
        var p2 = store.SaveProfile(new Profile { Name = "P2", Enabled = true });
        store.StartSession(p1.Id, 3600, locked: true);

        var req = new Request { Command = CommandNames.StopSession };
        var resp = Handle(req, store);
        Assert.True(resp.Ok);
        Assert.Null(store.GetActiveSession());
        Assert.False(p1.Enabled);
        Assert.False(p2.Enabled);
        Assert.True(store.EmergencyUnlockConsumed);
    }

    [Fact]
    public void GetStatus_ReturnsActiveProfileNamesAndStatuses()
    {
        var store = new FakeBlockStore();
        var p = store.SaveProfile(new Profile
        {
            Name = "WorkSchedule",
            Enabled = true,
            BlockedProcesses = new() { "slack.exe" },
            BlockedDomains = new() { "twitter.com" },
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        var req = new Request { Command = CommandNames.GetStatus };
        var resp = Handle(req, store);
        Assert.True(resp.Ok);

        var status = JsonSerializer.Deserialize<StatusData>(resp.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(status);
        Assert.True(status.Active);
        Assert.Contains("WorkSchedule", status.ActiveProfileNames);
        Assert.Contains("slack.exe", status.BlockedProcesses);
        Assert.Contains("twitter.com", status.BlockedDomains);
        Assert.Single(status.ProfileStatuses);
        Assert.True(status.ProfileStatuses[0].IsActive);
        Assert.Equal("Schedule", status.ProfileStatuses[0].ActiveReason);
    }

    [Fact]
    public void SaveProfile_ValidatesAndNormalizesWebRules()
    {
        var store = new FakeBlockStore();
        var rules = new List<WebRule>
        {
            new WebRule { Pattern = "HTTPS://EXAMPLE.COM/PATH/", Allow = false },
            new WebRule { Pattern = "example.org?query=1", Allow = true }
        };

        var req = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "WebTest",
                ["webrules"] = JsonSerializer.Serialize(rules),
                ["block_all"] = "false"
            }
        };

        var resp = Handle(req, store);
        Assert.True(resp.Ok, resp.Error);

        var saved = store.GetProfileByName("WebTest");
        Assert.NotNull(saved);
        Assert.Equal(2, saved.WebRules.Count);
        Assert.Equal("example.com/path", saved.WebRules[0].Pattern);
        Assert.False(saved.WebRules[0].Allow);
        Assert.Equal("example.org", saved.WebRules[1].Pattern);
        Assert.True(saved.WebRules[1].Allow);
    }

    [Fact]
    public void SaveProfile_RejectsBadPattern_ReturnsClearError()
    {
        var store = new FakeBlockStore();
        var rules = new List<WebRule>
        {
            new WebRule { Pattern = "example<bad>.com", Allow = false }
        };

        var req = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "WebTest",
                ["webrules"] = JsonSerializer.Serialize(rules)
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("Invalid website pattern", resp.Error);
        Assert.Contains("example<bad>.com", resp.Error);
    }

    [Fact]
    public void SaveProfile_RejectsMoreThan500WebRules()
    {
        var store = new FakeBlockStore();
        var rules = Enumerable.Range(1, 501)
            .Select(i => new WebRule { Pattern = $"example{i}.com", Allow = false })
            .ToList();

        var req = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "WebTest",
                ["webrules"] = JsonSerializer.Serialize(rules)
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("more than 500", resp.Error);
    }

    [Fact]
    public void SaveProfile_ObeysLockRules_WhenLocked()
    {
        var store = new FakeBlockStore();
        var p = store.SaveProfile(new Profile
        {
            Name = "LockedWeb",
            Enabled = true,
            LockMode = LockMode.TypingChallenge,
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        // Attempt to edit web rules without unlock text
        var req = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "LockedWeb",
                ["webrules"] = JsonSerializer.Serialize(new List<WebRule> { new WebRule { Pattern = "example.com", Allow = false } })
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("Refused", resp.Error);
    }

    [Fact]
    public void GetActiveWebRules_ReturnsActiveProfilesAndStableVersionHash()
    {
        var store = new FakeBlockStore();
        store.SaveProfile(new Profile
        {
            Name = "Active1",
            Enabled = true,
            BlockAllWebsites = true,
            WebRules = new() { new WebRule { Pattern = "example.org", Allow = true } }
        });
        store.SaveProfile(new Profile
        {
            Name = "Inactive2",
            Enabled = false,
            WebRules = new() { new WebRule { Pattern = "example.net", Allow = false } }
        });

        var req = new Request { Command = CommandNames.GetActiveWebRules };
        var resp1 = Handle(req, store);
        Assert.True(resp1.Ok);

        var data1 = JsonSerializer.Deserialize<ActiveWebRulesData>(resp1.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(data1);
        Assert.Single(data1.Profiles);
        Assert.Equal("Active1", data1.Profiles[0].ProfileName);
        Assert.True(data1.Profiles[0].BlockAllWebsites);
        Assert.Single(data1.Profiles[0].WebRules);
        Assert.Equal("example.org", data1.Profiles[0].WebRules[0].Pattern);
        Assert.True(data1.Profiles[0].WebRules[0].Allow);
        Assert.Null(data1.Profiles[0].ActiveUntilLocal); // Always on -> null

        // Hash stability check
        var resp2 = Handle(req, store);
        var data2 = JsonSerializer.Deserialize<ActiveWebRulesData>(resp2.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(data2);
        Assert.Equal(data1.RulesVersion, data2.RulesVersion);
        Assert.NotEmpty(data1.RulesVersion);
    }

    [Fact]
    public void GetActiveWebRules_ScheduledActiveProfile_WithLockModeNone_ReturnsLockedTrue()
    {
        var store = new FakeBlockStore();
        var now = DateTime.Now;
        int currentMinute = now.Hour * 60 + now.Minute;
        var scheduleRule = new ScheduleRule
        {
            Days = DaysOfWeek.All,
            StartMinute = currentMinute == 1439 ? 1400 : 0,
            EndMinute = currentMinute == 1439 ? 100 : 1439
        };

        var profile = new Profile
        {
            Name = "ScheduledStudy",
            Enabled = true,
            LockMode = LockMode.None,
            Schedule = new List<ScheduleRule> { scheduleRule },
            WebRules = new List<WebRule>
            {
                new WebRule { Pattern = "distraction.com", Allow = false }
            }
        };
        store.SaveProfile(profile);

        var req = new Request { Command = CommandNames.GetActiveWebRules };
        var resp = Handle(req, store);
        Assert.True(resp.Ok);

        var data = JsonSerializer.Deserialize<ActiveWebRulesResponse>(resp.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(data);

        // ActiveProfiles check
        var activeProfile = Assert.Single(data.ActiveProfiles);
        Assert.Equal("ScheduledStudy", activeProfile.Name);
        Assert.True(activeProfile.Locked, "ActiveProfileDto.Locked should be true during an active schedule even when LockMode is None.");
        Assert.Equal("None", activeProfile.LockMode);

        // AllProfiles summary check
        var summaryProfile = Assert.Single(data.AllProfiles);
        Assert.Equal("ScheduledStudy", summaryProfile.Name);
        Assert.True(summaryProfile.Active, "ProfileSummaryDto.Active should be true.");
        Assert.True(summaryProfile.Locked, "ProfileSummaryDto.Locked should be true during an active schedule even when LockMode is None.");
        Assert.Equal("None", summaryProfile.LockMode);
    }

    [Fact]
    public void GetActiveWebRules_ScheduledProfile_OutsideScheduleWindow_ReturnsLockedFalse()
    {
        var store = new FakeBlockStore();
        var now = DateTime.Now;
        // Schedule rule for a day that is NOT today
        DayOfWeek today = now.DayOfWeek;
        DayOfWeek otherDay = today == DayOfWeek.Sunday ? DayOfWeek.Monday : DayOfWeek.Sunday;
        var scheduleRule = new ScheduleRule
        {
            Days = ScheduleRule.FromDayOfWeek(otherDay),
            StartMinute = 0,
            EndMinute = 1439
        };

        var profile = new Profile
        {
            Name = "OffScheduleBlock",
            Enabled = true,
            LockMode = LockMode.None,
            Schedule = new List<ScheduleRule> { scheduleRule },
            WebRules = new List<WebRule>
            {
                new WebRule { Pattern = "gaming.com", Allow = false }
            }
        };
        store.SaveProfile(profile);

        var req = new Request { Command = CommandNames.GetActiveWebRules };
        var resp = Handle(req, store);
        Assert.True(resp.Ok);

        var data = JsonSerializer.Deserialize<ActiveWebRulesResponse>(resp.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(data);

        // Not currently active, so activeProfiles should be empty
        Assert.Empty(data.ActiveProfiles);

        // In allProfiles, it should be inactive and not locked
        var summaryProfile = Assert.Single(data.AllProfiles);
        Assert.Equal("OffScheduleBlock", summaryProfile.Name);
        Assert.False(summaryProfile.Active);
        Assert.False(summaryProfile.Locked);
    }

    [Fact]
    public void ExtHeartbeat_RecordsHeartbeatAndReturnsRulesDirectly()
    {
        var store = new FakeBlockStore();
        store.SaveProfile(new Profile
        {
            Name = "ActiveWork",
            Enabled = true,
            WebRules = new() { new WebRule { Pattern = "example.com", Allow = false } }
        });

        var req = new Request
        {
            Command = CommandNames.ExtHeartbeat,
            Args = new Dictionary<string, string>
            {
                ["browserProcess"] = "chrome.exe",
                ["incognitoAllowed"] = "true",
                ["extensionVersion"] = "1.0.0"
            }
        };

        var resp = Handle(req, store);
        Assert.True(resp.Ok);
        Assert.NotNull(resp.Data);

        var data = JsonSerializer.Deserialize<ActiveWebRulesResponse>(resp.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(data);
        Assert.True(data.RulesVersion > 0);
        Assert.Single(data.ActiveProfiles);
        Assert.Equal("ActiveWork", data.ActiveProfiles[0].Name);
        Assert.Single(data.ActiveProfiles[0].WebRules);
        Assert.Equal("example.com", data.ActiveProfiles[0].WebRules[0].Pattern);

        // Verify status reflects extension connection
        var statusResp = Handle(new Request { Command = CommandNames.GetStatus }, store);
        Assert.True(statusResp.Ok);
        var status = JsonSerializer.Deserialize<StatusData>(statusResp.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(status);
        Assert.NotNull(status.ExtensionStatus);
        Assert.True(status.ExtensionStatus.Connected);
        Assert.Contains(status.ExtensionStatus.Browsers, b => b.ProcessName == "chrome.exe" && b.Connected);
    }

    [Fact]
    public void DnsCommands_SaveLockChallengeAndUnlock_WorksCorrectly()
    {
        var store = new FakeBlockStore();

        // 1. Get default DNS config
        var getReq = new Request { Command = CommandNames.GetDnsConfig };
        var getResp = Handle(getReq, store);
        Assert.True(getResp.Ok);
        var initial = JsonSerializer.Deserialize<DnsConfig>(getResp.Data!);
        Assert.NotNull(initial);
        Assert.False(initial.Enabled);
        Assert.False(initial.Locked);

        // 2. Save DNS config (CleanBrowsing)
        var saveReq = new Request
        {
            Command = CommandNames.SaveDnsConfig,
            Args = new()
            {
                ["preset"] = "CleanBrowsing",
                ["primary"] = "185.228.168.168",
                ["secondary"] = "185.228.169.168",
                ["enabled"] = "true"
            }
        };
        var saveResp = Handle(saveReq, store);
        Assert.True(saveResp.Ok);
        var updated = JsonSerializer.Deserialize<DnsConfig>(saveResp.Data!);
        Assert.NotNull(updated);
        Assert.True(updated.Enabled);
        Assert.Equal("CleanBrowsing", updated.PresetName);
        Assert.Equal("185.228.168.168", updated.PrimaryDns);

        // 3. Lock DNS with 64-character typing challenge
        var lockReq = new Request
        {
            Command = CommandNames.LockDns,
            Args = new()
            {
                ["lock_type"] = "Challenge"
            }
        };
        var lockResp = Handle(lockReq, store);
        Assert.True(lockResp.Ok);
        var lockedCfg = JsonSerializer.Deserialize<DnsConfig>(lockResp.Data!);
        Assert.NotNull(lockedCfg);
        Assert.True(lockedCfg.Locked);

        // 4. Modifying DNS while locked should fail
        var tryModifyReq = new Request
        {
            Command = CommandNames.SaveDnsConfig,
            Args = new() { ["enabled"] = "false" }
        };
        var tryModifyResp = Handle(tryModifyReq, store);
        Assert.False(tryModifyResp.Ok);
        Assert.Contains("locked", tryModifyResp.Error, StringComparison.OrdinalIgnoreCase);

        // 5. Request 64-character DNS challenge
        var chReq = new Request { Command = CommandNames.GetDnsChallenge };
        var chResp = Handle(chReq, store);
        Assert.True(chResp.Ok);
        string challenge = chResp.Data!;
        Assert.Equal(64, challenge.Length);

        // 6. Attempt unlock with wrong text should fail
        var wrongUnlockReq = new Request
        {
            Command = CommandNames.UnlockDns,
            Args = new() { ["challenge_text"] = "wrong_challenge_text" }
        };
        var wrongResp = Handle(wrongUnlockReq, store);
        Assert.False(wrongResp.Ok);

        // 7. Request fresh challenge and unlock with exact match
        var freshChResp = Handle(new Request { Command = CommandNames.GetDnsChallenge }, store);
        string freshChallenge = freshChResp.Data!;
        Assert.Equal(64, freshChallenge.Length);

        var unlockReq = new Request
        {
            Command = CommandNames.UnlockDns,
            Args = new() { ["challenge_text"] = freshChallenge }
        };
        var unlockResp = Handle(unlockReq, store);
        Assert.True(unlockResp.Ok);
        var unlockedCfg = JsonSerializer.Deserialize<DnsConfig>(unlockResp.Data!);
        Assert.NotNull(unlockedCfg);
        Assert.False(unlockedCfg.Locked);
    }

    [Fact]
    public void DnsTimerLock_CannotBeUnlockedEarly_UnlocksWhenExpired()
    {
        var store = new FakeBlockStore();

        // 1. Lock DNS with 1 day (86400 seconds)
        var lockReq = new Request
        {
            Command = CommandNames.LockDns,
            Args = new()
            {
                ["lock_type"] = "Timer",
                ["duration"] = "86400"
            }
        };
        var lockResp = Handle(lockReq, store);
        Assert.True(lockResp.Ok);
        var lockedCfg = JsonSerializer.Deserialize<DnsConfig>(lockResp.Data!);
        Assert.NotNull(lockedCfg);
        Assert.True(lockedCfg.Locked);
        Assert.Equal("Timer", lockedCfg.LockType);
        Assert.NotNull(lockedCfg.LockedUntilUtc);
        Assert.True(lockedCfg.LockedUntilUtc > DateTime.UtcNow.AddHours(23));

        // 2. Early unlock attempt should fail
        var unlockReq = new Request { Command = CommandNames.UnlockDns };
        var earlyUnlockResp = Handle(unlockReq, store);
        Assert.False(earlyUnlockResp.Ok);
        Assert.Contains("DNS is locked for", earlyUnlockResp.Error);

        // 3. Fast-forward expiry in store to past
        var currentCfg = store.GetDnsConfig();
        currentCfg.LockedUntilUtc = DateTime.UtcNow.AddSeconds(-5);
        store.SaveDnsConfig(currentCfg);

        // 4. Now unlock succeeds
        var expiredUnlockResp = Handle(unlockReq, store);
        Assert.True(expiredUnlockResp.Ok);
        var unlockedCfg = JsonSerializer.Deserialize<DnsConfig>(expiredUnlockResp.Data!);
        Assert.NotNull(unlockedCfg);
        Assert.False(unlockedCfg.Locked);
    }

    #region Section 1: Rename Under Lock Tests

    [Fact]
    public void RenameProfile_UnderActiveLock_Succeeds_AndSessionTimerUnchanged()
    {
        var store = new FakeBlockStore();
        var profile = store.SaveProfile(new Profile
        {
            Name = "InitialBlock",
            BlockedProcesses = new List<string> { "notepad.exe" },
            BlockedDomains = new List<string> { "example.com" }
        });

        var session = store.StartSession(profile.Id, 3600, locked: true);
        DateTime originalStart = session.StartUtc;

        var renameReq = new Request
        {
            Command = CommandNames.RenameProfile,
            Args = new()
            {
                ["id"] = profile.Id.ToString(),
                ["name"] = "DeepFocusBlock"
            }
        };

        var resp = Handle(renameReq, store);
        Assert.True(resp.Ok, resp.Error);

        var updated = store.GetProfileById(profile.Id);
        Assert.NotNull(updated);
        Assert.Equal("DeepFocusBlock", updated.Name);

        // Session and timer are unchanged
        var activeSession = store.GetActiveSession();
        Assert.NotNull(activeSession);
        Assert.Equal(profile.Id, activeSession.ProfileId);
        Assert.Equal(3600, activeSession.DurationSeconds);
        Assert.True(activeSession.Locked);
        Assert.Equal(originalStart, activeSession.StartUtc);

        // Rules are unchanged
        Assert.Contains("notepad.exe", updated.BlockedProcesses);
        Assert.Contains("example.com", updated.BlockedDomains);
    }

    [Fact]
    public void RenameProfile_CarryingOtherEdits_IsRefused()
    {
        var store = new FakeBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "InitialBlock" });
        store.StartSession(profile.Id, 3600, locked: true);

        var req = new Request
        {
            Command = CommandNames.RenameProfile,
            Args = new()
            {
                ["id"] = profile.Id.ToString(),
                ["name"] = "NewName",
                ["processes"] = "calc.exe" // Illicit modification parameter
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("Rename operation cannot carry other modifications", resp.Error);
    }

    [Fact]
    public void SaveProfile_UnderCompleteLock_WithoutAdditiveMode_IsRefused()
    {
        var store = new FakeBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "LockedBlock", BlockedProcesses = new() { "notepad.exe" } });
        store.StartSession(profile.Id, 3600, locked: true);

        var req = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "LockedBlock",
                ["processes"] = "notepad.exe,calc.exe"
                // Missing additive_only: true
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("Cannot edit profile 'LockedBlock' while an active LOCKED session is using it", resp.Error);
    }

    [Fact]
    public void RenameProfile_InvalidNames_Rejected()
    {
        var store = new FakeBlockStore();
        var p1 = store.SaveProfile(new Profile { Name = "First" });
        store.SaveProfile(new Profile { Name = "Second" });

        // Empty name
        var emptyResp = Handle(new Request
        {
            Command = CommandNames.RenameProfile,
            Args = new() { ["id"] = p1.Id.ToString(), ["name"] = "" }
        }, store);
        Assert.False(emptyResp.Ok);
        Assert.Contains("cannot be empty", emptyResp.Error);

        // Whitespace name
        var wsResp = Handle(new Request
        {
            Command = CommandNames.RenameProfile,
            Args = new() { ["id"] = p1.Id.ToString(), ["name"] = "   " }
        }, store);
        Assert.False(wsResp.Ok);
        Assert.Contains("cannot be empty", wsResp.Error);

        // Name exceeding 100 characters
        var longResp = Handle(new Request
        {
            Command = CommandNames.RenameProfile,
            Args = new() { ["id"] = p1.Id.ToString(), ["name"] = new string('A', 101) }
        }, store);
        Assert.False(longResp.Ok);
        Assert.Contains("cannot exceed 100 characters", longResp.Error);

        // Duplicate name
        var dupResp = Handle(new Request
        {
            Command = CommandNames.RenameProfile,
            Args = new() { ["id"] = p1.Id.ToString(), ["name"] = "Second" }
        }, store);
        Assert.False(dupResp.Ok);
        Assert.Contains("already exists", dupResp.Error);
    }

    #endregion

    #region Section 4: Extension Theme Tests

    [Fact]
    public void ExtensionTheme_RoundTripsThroughSyncMessage()
    {
        var store = new FakeBlockStore();
        store.SaveProfile(new Profile { Name = "ActiveBlock", Enabled = true, WebRules = new() { new WebRule { Pattern = "reddit.com", Allow = false } } });

        // Save black theme
        var settings = UiSettingsStore.Load();
        settings.Theme = "black";
        UiSettingsStore.Save(settings);

        var resp1 = Handle(new Request { Command = CommandNames.GetActiveWebRules }, store);
        Assert.True(resp1.Ok);
        var dto1 = JsonSerializer.Deserialize<ActiveWebRulesResponse>(resp1.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(dto1);
        Assert.Equal("black", dto1.ExtensionTheme);

        // Save white theme
        settings.Theme = "white";
        UiSettingsStore.Save(settings);

        var resp2 = Handle(new Request { Command = CommandNames.GetActiveWebRules }, store);
        Assert.True(resp2.Ok);
        var dto2 = JsonSerializer.Deserialize<ActiveWebRulesResponse>(resp2.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(dto2);
        Assert.Equal("white", dto2.ExtensionTheme);

        // Save grey theme
        settings.Theme = "grey";
        UiSettingsStore.Save(settings);

        var resp3 = Handle(new Request { Command = CommandNames.GetActiveWebRules }, store);
        Assert.True(resp3.Ok);
        var dto3 = JsonSerializer.Deserialize<ActiveWebRulesResponse>(resp3.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(dto3);
        Assert.Equal("grey", dto3.ExtensionTheme);

        // Clean up
        settings.Theme = "black";
        UiSettingsStore.Save(settings);
    }

    [Fact]
    public void ExtensionTheme_InvalidTheme_FallsBackToDefault()
    {
        Assert.Equal("black", UiSettingsStore.NormalizeTheme("NeonPink"));
        Assert.Equal("black", UiSettingsStore.NormalizeTheme(null));
        Assert.Equal("black", UiSettingsStore.NormalizeTheme(""));
        Assert.Equal("black", UiSettingsStore.NormalizeTheme("   "));
        Assert.Equal("black", UiSettingsStore.NormalizeTheme("black"));
        Assert.Equal("black", UiSettingsStore.NormalizeTheme("DeepBlack"));
        Assert.Equal("white", UiSettingsStore.NormalizeTheme("white"));
        Assert.Equal("white", UiSettingsStore.NormalizeTheme("MinimalWhite"));
        Assert.Equal("white", UiSettingsStore.NormalizeTheme("minimal-white"));
        Assert.Equal("grey", UiSettingsStore.NormalizeTheme("grey"));
        Assert.Equal("grey", UiSettingsStore.NormalizeTheme("gray"));
    }

    #endregion

    #region Section 5: Extension Add Website & Domain Normalization Tests

    [Theory]
    [InlineData("https://example.com/path?query#hash", "example.com")]
    [InlineData("http://www.sub.example.com:8080/test", "sub.example.com")]
    [InlineData("m.facebook.com", "facebook.com")]
    [InlineData("WWW.EXAMPLE.COM", "example.com")]
    [InlineData("example.com/", "example.com")]
    [InlineData("https://news.ycombinator.com/item?id=123", "news.ycombinator.com")]
    public void DomainNormalization_Table_NormalizesCorrectly(string raw, string expected)
    {
        bool ok = HostsFileBuilder.TryNormalizeDomain(raw, out string norm);
        Assert.True(ok, $"Failed to normalize '{raw}'");
        Assert.Equal(expected, norm);
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("localhost")]
    [InlineData("http://*.wildcard.com")]
    [InlineData("bad domain with spaces")]
    [InlineData("http://#anchor")]
    [InlineData("")]
    [InlineData("   ")]
    public void DomainNormalization_Table_RejectsInvalid(string invalid)
    {
        bool ok = HostsFileBuilder.TryNormalizeDomain(invalid, out string _);
        Assert.False(ok, $"Should reject '{invalid}'");
    }

    [Fact]
    public void AddWebsite_ToLockedBlock_SucceedsInAdditiveMode()
    {
        var store = new FakeBlockStore();
        var profile = store.SaveProfile(new Profile
        {
            Name = "LockedTarget",
            Enabled = true,
            WebRules = new() { new WebRule { Pattern = "blocked1.com", Allow = false } }
        });

        store.StartSession(profile.Id, 3600, locked: true);

        var req = new Request
        {
            Command = CommandNames.AddWebsite,
            Args = new()
            {
                ["profile_id"] = profile.Id.ToString(),
                ["domain"] = "https://www.wikipedia.org/wiki/Special:Search"
            }
        };

        var resp = Handle(req, store);
        Assert.True(resp.Ok, resp.Error);

        var updated = store.GetProfileById(profile.Id);
        Assert.NotNull(updated);
        Assert.Contains(updated.WebRules, r => r.Pattern == "wikipedia.org" && !r.Allow);
        Assert.Contains(updated.WebRules, r => r.Pattern == "blocked1.com" && !r.Allow);
    }

    [Fact]
    public void AddWebsite_RemovalRequestFromExtension_IsRefused()
    {
        var store = new FakeBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "TestBlock" });

        var req = new Request
        {
            Command = CommandNames.AddWebsite,
            Args = new()
            {
                ["profile_id"] = profile.Id.ToString(),
                ["domain"] = "example.com",
                ["remove"] = "true"
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("Refused: Extension cannot perform removal, stop, or exception actions", resp.Error);
    }

    [Fact]
    public void AddWebsite_DuplicateDomain_Rejected()
    {
        var store = new FakeBlockStore();
        var profile = store.SaveProfile(new Profile
        {
            Name = "DupTarget",
            WebRules = new() { new WebRule { Pattern = "reddit.com", Allow = false } }
        });

        var req = new Request
        {
            Command = CommandNames.AddWebsite,
            Args = new()
            {
                ["profile_id"] = profile.Id.ToString(),
                ["domain"] = "https://www.reddit.com/r/all"
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("Duplicate", resp.Error);
    }

    [Fact]
    public void AddWebsite_InvalidInput_Rejected()
    {
        var store = new FakeBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "InvalidTarget" });

        var req = new Request
        {
            Command = CommandNames.AddWebsite,
            Args = new()
            {
                ["profile_id"] = profile.Id.ToString(),
                ["domain"] = "localhost"
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("Invalid domain", resp.Error);
    }

    
    [Fact]
    public void MultiSession_CanStartSessionsOnMultipleProfilesConcurrently()
    {
        var store = new FakeBlockStore();
        var p1 = store.SaveProfile(new Profile { Name = "Profile1", BlockedProcesses = new() { "app1.exe" } });
        var p2 = store.SaveProfile(new Profile { Name = "Profile2", BlockedProcesses = new() { "app2.exe" } });

        // Start locked session on Profile 1
        var start1 = Handle(new Request
        {
            Command = CommandNames.StartSession,
            Args = new() { ["profile"] = "Profile1", ["minutes"] = "30", ["locked"] = "true" }
        }, store);
        Assert.True(start1.Ok);

        // Start locked session on Profile 2 concurrently
        var start2 = Handle(new Request
        {
            Command = CommandNames.StartSession,
            Args = new() { ["profile"] = "Profile2", ["minutes"] = "45", ["locked"] = "true" }
        }, store);
        Assert.True(start2.Ok);

        // Status reflects both sessions
        var statusResp = Handle(new Request { Command = "GETSTATUS" }, store);
        Assert.True(statusResp.Ok);
        var status = JsonSerializer.Deserialize<StatusData>(statusResp.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(status);
        Assert.True(status.Active);
        Assert.True(status.Locked);
        Assert.Equal(2, status.ProfileStatuses.Count(p => p.IsSessionRunning));
    }

    [Fact]
    public void MultiSession_CanAddAndEditOtherProfileWhileLockedSessionIsActive()
    {
        var store = new FakeBlockStore();
        var p1 = store.SaveProfile(new Profile { Name = "LockedProfile", BlockedProcesses = new() { "distraction.exe" } });
        store.StartSession(p1.Id, 3600, locked: true);

        // Add a brand new profile while p1 is locked
        var addResp = Handle(new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "ProductiveProfile",
                ["processes"] = "workapp.exe",
                ["site"] = "github.com"
            }
        }, store);
        Assert.True(addResp.Ok);

        // Edit the new profile freely
        var editResp = Handle(new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "ProductiveProfile",
                ["processes"] = "workapp.exe,slack.exe"
            }
        }, store);
        Assert.True(editResp.Ok);

        // Editing locked profile with non-additive modification is refused
        var refuseEdit = Handle(new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "LockedProfile",
                ["processes"] = "different.exe"
            }
        }, store);
        Assert.False(refuseEdit.Ok);
        Assert.Contains("Cannot edit profile 'LockedProfile' while an active LOCKED session is using it", refuseEdit.Error);

        // Editing locked profile with additive_only is allowed
        var additiveEdit = Handle(new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "LockedProfile",
                ["processes"] = "distraction.exe,more_distraction.exe",
                ["additive_only"] = "true"
            }
        }, store);
        Assert.True(additiveEdit.Ok);
    }

    [Fact]
    public void MultiSession_StopSession_TargetsSpecificProfile()
    {
        var store = new FakeBlockStore();
        var pLocked = store.SaveProfile(new Profile { Name = "CompleteLockProfile" });
        var pUnlocked = store.SaveProfile(new Profile { Name = "FlexibleProfile" });

        store.StartSession(pLocked.Id, 3600, locked: true);
        store.StartSession(pUnlocked.Id, 1800, locked: false);

        // Stopping without specifying profile fails due to ambiguity
        var ambiguousStop = Handle(new Request { Command = CommandNames.StopSession }, store);
        Assert.False(ambiguousStop.Ok);
        Assert.Contains("Multiple sessions are active", ambiguousStop.Error);

        // Stopping the locked session is refused
        var stopLocked = Handle(new Request
        {
            Command = CommandNames.StopSession,
            Args = new() { ["profile"] = "CompleteLockProfile" }
        }, store);
        Assert.False(stopLocked.Ok);
        Assert.Contains("Stop refused", stopLocked.Error);

        // Stopping the unlocked session succeeds
        var stopUnlocked = Handle(new Request
        {
            Command = CommandNames.StopSession,
            Args = new() { ["profile"] = "FlexibleProfile" }
        }, store);
        Assert.True(stopUnlocked.Ok);

        // Flexible session is ended, complete lock session remains active
        var activeSessions = store.GetActiveSessions();
        Assert.Single(activeSessions);
        Assert.Equal(pLocked.Id, activeSessions[0].ProfileId);
    }

    [Fact]
    public void BulkAdding_500LimitEnforced()
    {
        var store = new FakeBlockStore();
        
        // 500 rules succeeds
        var rules500 = Enumerable.Range(1, 500)
            .Select(i => new WebRule { Pattern = $"valid{i}.com", Allow = false })
            .ToList();

        var req500 = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "Bulk500",
                ["webrules"] = JsonSerializer.Serialize(rules500)
            }
        };
        var resp500 = Handle(req500, store);
        Assert.True(resp500.Ok);

        // 501 rules is rejected
        var rules501 = Enumerable.Range(1, 501)
            .Select(i => new WebRule { Pattern = $"exceed{i}.com", Allow = false })
            .ToList();

        var req501 = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "Bulk501",
                ["webrules"] = JsonSerializer.Serialize(rules501)
            }
        };
        var resp501 = Handle(req501, store);
        Assert.False(resp501.Ok);
        Assert.Contains("more than 500 website rules", resp501.Error);
    }

    [Fact]
    public void DisableProfile_CannotDisableProfileWithActiveLockedSession()
    {
        var store = new FakeBlockStore();
        var p = store.SaveProfile(new Profile
        {
            Name = "LockedSessionProfile",
            Enabled = true,
            LockMode = LockMode.None
        });
        store.StartSession(p.Id, 1800, locked: true);

        var resp = Handle(new Request
        {
            Command = CommandNames.DisableProfile,
            Args = new() { ["name"] = "LockedSessionProfile" }
        }, store);

        Assert.False(resp.Ok);
        Assert.Contains("active LOCKED session", resp.Error);
    }

    #region Schedule and Challenge Protection Tests (Bug 3)

    [Fact]
    public void DisableProfile_DuringActiveSchedule_RefusedEvenWithPassword()
    {
        var store = new FakeBlockStore();
        PasswordSecurity.HashPassword("secret123", out string salt, out string hash);
        store.SaveProfile(new Profile
        {
            Name = "SchedPwd",
            Enabled = true,
            LockMode = LockMode.Password,
            PasswordSalt = salt,
            PasswordHash = hash,
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        var req = new Request
        {
            Command = CommandNames.DisableProfile,
            Args = new()
            {
                ["name"] = "SchedPwd",
                ["unlock_password"] = "secret123"
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("is locked until", resp.Error);
        Assert.True(store.GetProfileByName("SchedPwd")!.Enabled);
    }

    [Fact]
    public void DisableProfile_DuringActiveSchedule_RefusedEvenWithTypingChallenge()
    {
        var store = new FakeBlockStore();
        store.SaveProfile(new Profile
        {
            Name = "SchedTyping",
            Enabled = true,
            LockMode = LockMode.TypingChallenge,
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        var chResp = Handle(new Request
        {
            Command = CommandNames.GetUnlockChallenge,
            Args = new() { ["name"] = "SchedTyping" }
        }, store);
        Assert.True(chResp.Ok);
        string challenge = chResp.Data!;

        var req = new Request
        {
            Command = CommandNames.DisableProfile,
            Args = new()
            {
                ["name"] = "SchedTyping",
                ["unlock_text"] = challenge
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("is locked until", resp.Error);
        Assert.True(store.GetProfileByName("SchedTyping")!.Enabled);
    }

    [Fact]
    public void DisableProfile_DuringActiveSchedule_RefusedEvenWithDelayedUnlockToken()
    {
        var store = new FakeBlockStore();
        store.SaveProfile(new Profile
        {
            Name = "SchedDelay",
            Enabled = true,
            LockMode = LockMode.DelayedUnlock,
            DelayedUnlockMinutes = 15,
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        string token = DelayedUnlockManager.StartDelayedUnlock("SchedDelay", 0);

        var req = new Request
        {
            Command = CommandNames.DisableProfile,
            Args = new()
            {
                ["name"] = "SchedDelay",
                ["delayed_unlock_token"] = token
            }
        };

        var resp = Handle(req, store);
        Assert.False(resp.Ok);
        Assert.Contains("is locked until", resp.Error);
        Assert.True(store.GetProfileByName("SchedDelay")!.Enabled);
    }

    [Fact]
    public void SaveProfile_DuringActiveSchedule_NonAdditiveChanges_RefusedEvenWithCredentials()
    {
        var store = new FakeBlockStore();
        PasswordSecurity.HashPassword("secret123", out string salt, out string hash);
        store.SaveProfile(new Profile
        {
            Name = "SchedNonAdditive",
            Enabled = true,
            LockMode = LockMode.Password,
            PasswordSalt = salt,
            PasswordHash = hash,
            BlockedProcesses = new() { "notepad.exe", "slack.exe" },
            BlockedDomains = new() { "twitter.com" },
            Schedule = new() { new ScheduleRule { Days = (DaysOfWeek)127, StartMinute = 0, EndMinute = 1439 } }
        });

        // 1. Non-additive edit without additive_only, but with correct password -> refused
        var req1 = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "SchedNonAdditive",
                ["processes"] = "notepad.exe", // slacking removed
                ["unlock_password"] = "secret123"
            }
        };
        var resp1 = Handle(req1, store);
        Assert.False(resp1.Ok);
        Assert.Contains("is locked until", resp1.Error);

        // 2. Non-additive edit with additive_only=true and correct password -> refused
        var req2 = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "SchedNonAdditive",
                ["processes"] = "notepad.exe", // slacking removed
                ["additive_only"] = "true",
                ["unlock_password"] = "secret123"
            }
        };
        var resp2 = Handle(req2, store);
        Assert.False(resp2.Ok);
        Assert.Contains("cannot remove existing blocked application", resp2.Error);

        // 3. Trying to disable profile during active schedule -> refused
        var req3 = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new()
            {
                ["name"] = "SchedNonAdditive",
                ["enabled"] = "false",
                ["additive_only"] = "true",
                ["unlock_password"] = "secret123"
            }
        };
        var resp3 = Handle(req3, store);
        Assert.False(resp3.Ok);
        Assert.Contains("cannot disable profile", resp3.Error);
    }

    [Fact]
    public void DisableProfile_InactiveSchedule_WithPassword_EnforcesCredentials()
    {
        var store = new FakeBlockStore();
        PasswordSecurity.HashPassword("secret123", out string salt, out string hash);
        var notToday = DateTime.Now.DayOfWeek == DayOfWeek.Sunday ? DaysOfWeek.Mon : DaysOfWeek.Sun;
        store.SaveProfile(new Profile
        {
            Name = "InactiveSchedPwd",
            Enabled = true,
            LockMode = LockMode.Password,
            PasswordSalt = salt,
            PasswordHash = hash,
            Schedule = new() { new ScheduleRule { Days = notToday, StartMinute = 0, EndMinute = 1439 } }
        });

        // 1. Missing password -> Refused
        var req1 = new Request
        {
            Command = CommandNames.DisableProfile,
            Args = new() { ["name"] = "InactiveSchedPwd" }
        };
        var resp1 = Handle(req1, store);
        Assert.False(resp1.Ok);
        Assert.Contains("requires an unlock password", resp1.Error);
        Assert.True(store.GetProfileByName("InactiveSchedPwd")!.Enabled);

        // 2. Wrong password -> Refused
        var req2 = new Request
        {
            Command = CommandNames.DisableProfile,
            Args = new() { ["name"] = "InactiveSchedPwd", ["unlock_password"] = "wrong" }
        };
        var resp2 = Handle(req2, store);
        Assert.False(resp2.Ok);
        Assert.Contains("Incorrect unlock password", resp2.Error);
        Assert.True(store.GetProfileByName("InactiveSchedPwd")!.Enabled);

        // 3. Correct password -> Succeeds
        var req3 = new Request
        {
            Command = CommandNames.DisableProfile,
            Args = new() { ["name"] = "InactiveSchedPwd", ["unlock_password"] = "secret123" }
        };
        var resp3 = Handle(req3, store);
        Assert.True(resp3.Ok);
        Assert.False(store.GetProfileByName("InactiveSchedPwd")!.Enabled);
    }

    [Fact]
    public void DisableProfile_InactiveSchedule_WithTypingChallenge_EnforcesChallenge()
    {
        var store = new FakeBlockStore();
        var notToday = DateTime.Now.DayOfWeek == DayOfWeek.Sunday ? DaysOfWeek.Mon : DaysOfWeek.Sun;
        store.SaveProfile(new Profile
        {
            Name = "InactiveSchedTyping",
            Enabled = true,
            LockMode = LockMode.TypingChallenge,
            Schedule = new() { new ScheduleRule { Days = notToday, StartMinute = 0, EndMinute = 1439 } }
        });

        // 1. Missing challenge -> Refused
        var req1 = new Request
        {
            Command = CommandNames.DisableProfile,
            Args = new() { ["name"] = "InactiveSchedTyping" }
        };
        var resp1 = Handle(req1, store);
        Assert.False(resp1.Ok);
        Assert.Contains("typing challenge", resp1.Error);
        Assert.True(store.GetProfileByName("InactiveSchedTyping")!.Enabled);

        // 2. Wrong challenge -> Refused
        var req2 = new Request
        {
            Command = CommandNames.DisableProfile,
            Args = new() { ["name"] = "InactiveSchedTyping", ["unlock_text"] = "wrong" }
        };
        var resp2 = Handle(req2, store);
        Assert.False(resp2.Ok);

        // 3. Valid challenge -> Succeeds
        var chResp = Handle(new Request
        {
            Command = CommandNames.GetUnlockChallenge,
            Args = new() { ["name"] = "InactiveSchedTyping" }
        }, store);
        Assert.True(chResp.Ok);
        string challenge = chResp.Data!;

        var req3 = new Request
        {
            Command = CommandNames.DisableProfile,
            Args = new() { ["name"] = "InactiveSchedTyping", ["unlock_text"] = challenge }
        };
        var resp3 = Handle(req3, store);
        Assert.True(resp3.Ok);
        Assert.False(store.GetProfileByName("InactiveSchedTyping")!.Enabled);
    }

    [Fact]
    public void CancelDelayedUnlock_WithoutToken_ReturnsMissingTokenArgumentFail()
    {
        var reqMissing = new Request
        {
            Command = CommandNames.CancelDelayedUnlock,
            Args = new() { ["name"] = "TestProfile" }
        };
        var respMissing = Handle(reqMissing, new FakeBlockStore());
        Assert.False(respMissing.Ok);
        Assert.Equal("Missing required argument: 'token'.", respMissing.Error);

        var reqWhitespace = new Request
        {
            Command = CommandNames.CancelDelayedUnlock,
            Args = new() { ["name"] = "TestProfile", ["token"] = "   " }
        };
        var respWhitespace = Handle(reqWhitespace, new FakeBlockStore());
        Assert.False(respWhitespace.Ok);
        Assert.Equal("Missing required argument: 'token'.", respWhitespace.Error);
    }

    [Fact]
    public void DeleteProfile_WithTypingChallengeOrDelayedUnlock_EnforcesChallenge()
    {
        var store = new FakeBlockStore();
        store.SaveProfile(new Profile
        {
            Name = "TypingDel",
            Enabled = false,
            LockMode = LockMode.TypingChallenge
        });

        // 1. Delete without challenge -> Refused
        var delReq1 = new Request
        {
            Command = CommandNames.DeleteProfile,
            Args = new() { ["name"] = "TypingDel" }
        };
        var delResp1 = Handle(delReq1, store);
        Assert.False(delResp1.Ok);
        Assert.Contains("typing challenge", delResp1.Error);

        // Request challenge and delete with correct challenge
        var ch = Handle(new Request { Command = CommandNames.GetUnlockChallenge, Args = new() { ["name"] = "TypingDel" } }, store);
        var delReq2 = new Request
        {
            Command = CommandNames.DeleteProfile,
            Args = new() { ["name"] = "TypingDel", ["unlock_text"] = ch.Data! }
        };
        var delResp2 = Handle(delReq2, store);
        Assert.True(delResp2.Ok);
        Assert.Null(store.GetProfileByName("TypingDel"));

        // 2. Delayed unlock profile delete without token
        store.SaveProfile(new Profile
        {
            Name = "DelayDel",
            Enabled = false,
            LockMode = LockMode.DelayedUnlock,
            DelayedUnlockMinutes = 15
        });

        var delReq3 = new Request
        {
            Command = CommandNames.DeleteProfile,
            Args = new() { ["name"] = "DelayDel" }
        };
        var delResp3 = Handle(delReq3, store);
        Assert.False(delResp3.Ok);
        Assert.Contains("delayed unlock timer", delResp3.Error);
    }

    #endregion

    #endregion
}
