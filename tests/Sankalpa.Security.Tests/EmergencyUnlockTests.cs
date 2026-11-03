using System;
using System.Collections.Generic;
using System.Linq;
using Blocker.Core;
using Xunit;

namespace Sankalpa.Security.Tests;

public class EmergencyUnlockTests
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

    [Fact]
    public void EmergencyUnlock_Authorized_WhenAdministrator()
    {
        var store = new TestBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "DeepWork", Enabled = true });
        store.StartSession(profile.Id, 3600, locked: true);

        var adminIdentity = ClientIdentity.Administrator("CORP\\AdminUser", "S-1-5-32-544");
        var request = new Request { Command = "EMERGENCYUNLOCK" };

        var response = CommandHandler.Handle(request, store, adminIdentity);

        Assert.True(response.Ok);
        Assert.Contains("emergency unlock executed successfully", response.Data, StringComparison.OrdinalIgnoreCase);
        Assert.Null(store.GetActiveSession());
        Assert.False(profile.Enabled);
        Assert.True(store.CleanHostsCalled);
    }

    [Fact]
    public void EmergencyUnlock_Authorized_WhenLocalSystem()
    {
        var store = new TestBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "ExamLock", Enabled = true });
        store.StartSession(profile.Id, 7200, locked: true);

        var systemIdentity = ClientIdentity.LocalSystem;
        var request = new Request { Command = "EMERGENCYUNLOCK" };

        var response = CommandHandler.Handle(request, store, systemIdentity);

        Assert.True(response.Ok);
        Assert.Contains("emergency unlock executed successfully", response.Data, StringComparison.OrdinalIgnoreCase);
        Assert.Null(store.GetActiveSession());
        Assert.False(profile.Enabled);
        Assert.True(store.CleanHostsCalled);
    }

    [Fact]
    public void EmergencyUnlock_Authorized_ForStandardUser_WhenEmergencyRecoveryFilePresent()
    {
        var store = new TestBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "FocusSession", Enabled = true });
        store.StartSession(profile.Id, 3600, locked: true);
        store.EmergencyUnlockExists = true;

        var standardUser = ClientIdentity.StandardUser("DESKTOP\\Student", "S-1-5-21-12345");
        var request = new Request { Command = "EMERGENCYUNLOCK" };

        var response = CommandHandler.Handle(request, store, standardUser);

        Assert.True(response.Ok);
        Assert.True(store.EmergencyUnlockConsumed);
        Assert.False(store.EmergencyUnlockExists);
        Assert.Null(store.GetActiveSession());
        Assert.False(profile.Enabled);
        Assert.True(store.CleanHostsCalled);
    }

    [Fact]
    public void EmergencyUnlock_Unauthorized_ForStandardUser_WhenNoRecoveryFilePresent_StatePreserved()
    {
        var store = new TestBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "StrictFocus", Enabled = true });
        var session = store.StartSession(profile.Id, 3600, locked: true);
        store.EmergencyUnlockExists = false;

        var standardUser = ClientIdentity.StandardUser("DESKTOP\\NormalUser", "S-1-5-21-98765");
        var request = new Request { Command = "EMERGENCYUNLOCK" };

        var response = CommandHandler.Handle(request, store, standardUser);

        Assert.False(response.Ok);
        Assert.Contains("Unauthorized: EMERGENCYUNLOCK requires elevated administrator privileges or an emergency recovery file.", response.Error);
        Assert.NotNull(store.GetActiveSession());
        Assert.Equal(session.Id, store.GetActiveSession()!.Id);
        Assert.True(profile.Enabled);
        Assert.False(store.CleanHostsCalled);
        Assert.False(store.EmergencyUnlockConsumed);
    }

    [Fact]
    public void EmergencyUnlock_Rejected_WhenClientIdentityNull()
    {
        var store = new TestBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "StrictFocus", Enabled = true });
        store.StartSession(profile.Id, 3600, locked: true);

        var request = new Request { Command = "EMERGENCYUNLOCK" };
        var response = CommandHandler.Handle(request, store, clientIdentity: null);

        Assert.False(response.Ok);
        Assert.Equal("Unauthorized: Authentication required.", response.Error);
        Assert.NotNull(store.GetActiveSession());
        Assert.True(profile.Enabled);
    }

    [Fact]
    public void EmergencyUnlock_Rejected_WhenClientIdentityAnonymous()
    {
        var store = new TestBlockStore();
        var profile = store.SaveProfile(new Profile { Name = "StrictFocus", Enabled = true });
        store.StartSession(profile.Id, 3600, locked: true);

        var request = new Request { Command = "EMERGENCYUNLOCK" };
        var response = CommandHandler.Handle(request, store, ClientIdentity.Anonymous);

        Assert.False(response.Ok);
        Assert.Equal("Unauthorized: Authentication required.", response.Error);
        Assert.NotNull(store.GetActiveSession());
        Assert.True(profile.Enabled);
    }

    [Fact]
    public void EmergencyUnlock_Rejected_WhenUserIdentityUnauthenticated()
    {
        var store = new TestBlockStore();
        var unauthenticatedIdentity = new ClientIdentity
        {
            Name = "Attacker",
            Sid = "S-1-5-fake",
            IsAuthenticated = false,
            IsAdministrator = true // Even if claiming admin flag, unauthenticated must fail
        };

        var request = new Request { Command = "EMERGENCYUNLOCK" };
        var response = CommandHandler.Handle(request, store, unauthenticatedIdentity);

        Assert.False(response.Ok);
        Assert.Equal("Unauthorized: Authentication required.", response.Error);
    }
}
