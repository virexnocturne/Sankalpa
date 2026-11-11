using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Sankalpa.Security.Tests;

[SupportedOSPlatform("windows")]
public class PipeAuthorizationTests
{
    private class TestBlockStore : IBlockStore
    {
        public List<Profile> Profiles { get; } = new();
        public List<BlockSession> Sessions { get; } = new();
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
        private DnsConfig _dnsConfig = new();
        private readonly List<BlockEvent> _events = new();
        private int _nextProfileId = 1;
        private int _nextSessionId = 1;

        public Profile? GetProfileByName(string name) =>
            Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public Profile? GetProfileById(int id) =>
            Profiles.FirstOrDefault(p => p.Id == id);

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
                existing.SystemWide = profile.SystemWide;
                existing.Enabled = profile.Enabled;
                existing.Schedule = profile.Schedule;
                existing.Allowance = profile.Allowance;
                existing.LockMode = profile.LockMode;
                existing.PasswordSalt = profile.PasswordSalt;
                existing.PasswordHash = profile.PasswordHash;
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

        public BlockSession? GetActiveSession()
        {
            var now = DateTime.UtcNow;
            return Sessions.FirstOrDefault(s => s.StartUtc.AddSeconds(s.DurationSeconds) > now);
        }

        public BlockSession? GetSessionById(int id) =>
            Sessions.FirstOrDefault(s => s.Id == id);

        public BlockSession StartSession(int profileId, long durationSeconds, bool locked)
        {
            var session = new BlockSession
            {
                Id = _nextSessionId++,
                ProfileId = profileId,
                DurationSeconds = durationSeconds,
                Locked = locked,
                StartUtc = DateTime.UtcNow
            };
            Sessions.Add(session);
            return session;
        }

        public void EndActiveSession()
        {
            var s = GetActiveSession();
            if (s != null)
            {
                Sessions.Remove(s);
            }
        }

        public void EndSession(int sessionId)
        {
            var s = GetSessionById(sessionId);
            if (s != null)
            {
                Sessions.Remove(s);
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
            HostsState = HostsState.Idle;
            HostsDomainCount = 0;
            return true;
        }

        public List<string> GetCurrentHostsDomains() => new();
        public void RecordBlockEvent(BlockEvent evt) => _events.Add(evt);
        public List<BlockEvent> GetBlockEvents(long sinceId) => _events.Where(e => e.Id > sinceId).ToList();
        public DnsConfig GetDnsConfig() => _dnsConfig;
        public void SaveDnsConfig(DnsConfig config) => _dnsConfig = config;
    }

    [Theory]
    [InlineData("GETSTATUS")]
    [InlineData("LISTPROFILES")]
    [InlineData("GETPROFILE")]
    [InlineData("SAVEPROFILE")]
    [InlineData("DELETEPROFILE")]
    [InlineData("ENABLEPROFILE")]
    [InlineData("DISABLEPROFILE")]
    [InlineData("STARTSESSION")]
    [InlineData("STOPSESSION")]
    [InlineData("CLEANHOSTS")]
    [InlineData("HOSTSSTATUS")]
    [InlineData("GETEVENTS")]
    [InlineData("GETACTIVEWEBRULES")]
    [InlineData("EXTHEARTBEAT")]
    [InlineData("GETDNSCONFIG")]
    [InlineData("SAVEDNSCONFIG")]
    [InlineData("LOCKDNS")]
    [InlineData("UNLOCKDNS")]
    [InlineData("RENAMEPROFILE")]
    [InlineData("ADDWEBSITE")]
    [InlineData("GETDNSCHALLENGE")]
    [InlineData("GETUNLOCKCHALLENGE")]
    public void MissingOrNullIdentity_RejectsAllNonPingCommands(string command)
    {
        var store = new TestBlockStore();
        var request = new Request { Command = command };

        // Test with null clientIdentity
        var responseNull = CommandHandler.Handle(request, store, clientIdentity: null);
        Assert.False(responseNull.Ok);
        Assert.Equal("Unauthorized: Authentication required.", responseNull.Error);

        // Test with Anonymous clientIdentity
        var responseAnon = CommandHandler.Handle(request, store, ClientIdentity.Anonymous);
        Assert.False(responseAnon.Ok);
        Assert.Equal("Unauthorized: Authentication required.", responseAnon.Error);
    }

    [Fact]
    public void PingCommand_AlwaysPermitted_EvenWithoutAuthentication()
    {
        var store = new TestBlockStore();
        var pingRequest = new Request { Command = "PING" };

        var respNull = CommandHandler.Handle(pingRequest, store, clientIdentity: null);
        Assert.True(respNull.Ok);
        Assert.Equal("pong", respNull.Data);

        var respAnon = CommandHandler.Handle(pingRequest, store, ClientIdentity.Anonymous);
        Assert.True(respAnon.Ok);
        Assert.Equal("pong", respAnon.Data);

        var respStandard = CommandHandler.Handle(pingRequest, store, ClientIdentity.StandardUser("User", "S-1-5-21"));
        Assert.True(respStandard.Ok);
        Assert.Equal("pong", respStandard.Data);

        var respAdmin = CommandHandler.Handle(pingRequest, store, ClientIdentity.Administrator("Admin", "S-1-5-32-544"));
        Assert.True(respAdmin.Ok);
        Assert.Equal("pong", respAdmin.Data);
    }

    [Fact]
    public void NormalApplicationFunctionality_Succeeds_ForAuthenticatedStandardUser()
    {
        var store = new TestBlockStore();
        store.SaveProfile(new Profile { Name = "DeepWork", BlockedProcesses = new List<string> { "game.exe" } });

        var user = ClientIdentity.StandardUser("DESKTOP\\Worker", "S-1-5-21-1234");

        // 1. GETSTATUS
        var statusResp = CommandHandler.Handle(new Request { Command = "GETSTATUS" }, store, user);
        Assert.True(statusResp.Ok);
        Assert.NotNull(statusResp.Data);

        // 2. LISTPROFILES
        var listResp = CommandHandler.Handle(new Request { Command = "LISTPROFILES" }, store, user);
        Assert.True(listResp.Ok);
        Assert.Contains("DeepWork", listResp.Data);

        // 3. HOSTSSTATUS
        var hostsResp = CommandHandler.Handle(new Request { Command = "HOSTSSTATUS" }, store, user);
        Assert.True(hostsResp.Ok);
        Assert.NotNull(hostsResp.Data);

        // 4. GETDNSCONFIG
        var dnsResp = CommandHandler.Handle(new Request { Command = "GETDNSCONFIG" }, store, user);
        Assert.True(dnsResp.Ok);
        Assert.NotNull(dnsResp.Data);
    }

    [Fact]
    public void CleanHosts_StandardUser_RejectedWhenSessionLocked_WithoutEmergencyFile()
    {
        var store = new TestBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "Work", Enabled = true });
        store.StartSession(profile.Id, 3600, locked: true);
        store.EmergencyUnlockExists = false;

        var user = ClientIdentity.StandardUser("StandardUser", "S-1-5-21-user");
        var resp = CommandHandler.Handle(new Request { Command = "CLEANHOSTS" }, store, user);

        Assert.False(resp.Ok);
        Assert.Contains("Cannot clean hosts while a LOCKED session is active", resp.Error);
        Assert.False(store.CleanHostsCalled);
    }

    [Fact]
    public void CleanHosts_StandardUser_RejectedWhenProfileLocked_WithoutEmergencyFile()
    {
        var store = new TestBlockStore();
        var profile = store.SaveProfile(new Profile
        {
            Name = "StrictStudy",
            Enabled = true,
            LockMode = LockMode.Locked,
            Schedule = new List<ScheduleRule>
            {
                new ScheduleRule
                {
                    Days = DaysOfWeek.All,
                    StartMinute = 0,
                    EndMinute = 1439
                }
            }
        });
        store.EmergencyUnlockExists = false;

        var user = ClientIdentity.StandardUser("StandardUser", "S-1-5-21-user");
        var resp = CommandHandler.Handle(new Request { Command = "CLEANHOSTS" }, store, user);

        Assert.False(resp.Ok);
        Assert.Contains("Cannot clean hosts while active profile 'StrictStudy' is locked", resp.Error);
        Assert.False(store.CleanHostsCalled);
    }

    [Fact]
    public void CleanHosts_Administrator_PermittedEvenWhenSessionLocked()
    {
        var store = new TestBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "Work", Enabled = true });
        store.StartSession(profile.Id, 3600, locked: true);

        var admin = ClientIdentity.Administrator("Admin", "S-1-5-32-544");
        var resp = CommandHandler.Handle(new Request { Command = "CLEANHOSTS" }, store, admin);

        Assert.True(resp.Ok);
        Assert.True(store.CleanHostsCalled);
    }

    [Fact]
    public void CleanHosts_StandardUser_PermittedWhenEmergencyRecoveryFilePresent()
    {
        var store = new TestBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "Work", Enabled = true });
        store.StartSession(profile.Id, 3600, locked: true);
        store.EmergencyUnlockExists = true;

        var user = ClientIdentity.StandardUser("StandardUser", "S-1-5-21-user");
        var resp = CommandHandler.Handle(new Request { Command = "CLEANHOSTS" }, store, user);

        Assert.True(resp.Ok);
        Assert.True(store.CleanHostsCalled);
        Assert.True(store.EmergencyUnlockConsumed);
        Assert.Null(store.GetActiveSession());
        Assert.False(profile.Enabled);
    }

    [Fact]
    public void StopSession_StandardUser_RejectedWhenLocked()
    {
        var store = new TestBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "LockedFocus", Enabled = true });
        store.StartSession(profile.Id, 3600, locked: true);

        var user = ClientIdentity.StandardUser("StandardUser", "S-1-5-21-user");
        var resp = CommandHandler.Handle(new Request { Command = "STOPSESSION" }, store, user);

        Assert.False(resp.Ok);
        Assert.Contains("Stop refused", resp.Error);
        Assert.NotNull(store.GetActiveSession());
    }

    [Fact]
    public void StopSession_Administrator_PermittedWhenLocked()
    {
        var store = new TestBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "LockedFocus", Enabled = true });
        store.StartSession(profile.Id, 3600, locked: true);

        var admin = ClientIdentity.Administrator("Admin", "S-1-5-32-544");
        var resp = CommandHandler.Handle(new Request { Command = "STOPSESSION" }, store, admin);

        Assert.True(resp.Ok);
        Assert.Null(store.GetActiveSession());
        Assert.True(store.CleanHostsCalled);
    }

    [Fact]
    public void StopSession_StandardUser_PermittedWhenEmergencyRecoveryFilePresent()
    {
        var store = new TestBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "LockedFocus", Enabled = true });
        store.StartSession(profile.Id, 3600, locked: true);
        store.EmergencyUnlockExists = true;

        var user = ClientIdentity.StandardUser("StandardUser", "S-1-5-21-user");
        var resp = CommandHandler.Handle(new Request { Command = "STOPSESSION" }, store, user);

        Assert.True(resp.Ok);
        Assert.True(store.EmergencyUnlockConsumed);
        Assert.Null(store.GetActiveSession());
        Assert.False(profile.Enabled);
    }

    [Fact]
    public void UnexpectedAuthorizationFailures_AndMalformedRequests_HandledSafely()
    {
        var store = new TestBlockStore();
        var user = ClientIdentity.StandardUser("ValidUser", "S-1-5-21");

        // 1. Null request
        var nullResp = CommandHandler.Handle(null, store, user);
        Assert.False(nullResp.Ok);
        Assert.Equal("Empty or invalid request.", nullResp.Error);

        // 2. Empty command
        var emptyResp = CommandHandler.Handle(new Request { Command = "" }, store, user);
        Assert.False(emptyResp.Ok);
        Assert.Equal("Empty or invalid request.", emptyResp.Error);

        // 3. Unknown command
        var unknownResp = CommandHandler.Handle(new Request { Command = "UNKNOWN_OPCODE_TEST" }, store, user);
        Assert.False(unknownResp.Ok);
        Assert.Equal("Unknown command: 'UNKNOWN_OPCODE_TEST'.", unknownResp.Error);
    }

    [Fact]
    public void PipeServer_ExtractClientIdentity_SafeFallback_OnInvalidStream()
    {
        // ExtractClientIdentity on uninitialized or disconnected stream returns Anonymous and does not crash
        string testPipe = $"TestPipe_Auth_{Guid.NewGuid():N}";
        using var server = PipeServer.CreateServerStream(testPipe);

        var identity = PipeServer.ExtractClientIdentity(server);
        Assert.NotNull(identity);
        Assert.False(identity.IsAuthenticated);
        Assert.False(identity.IsAdministrator);
        Assert.False(identity.IsLocalSystem);
        Assert.Equal("Anonymous", identity.Name);
    }
}
