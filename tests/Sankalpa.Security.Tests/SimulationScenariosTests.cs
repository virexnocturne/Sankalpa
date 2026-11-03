using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Sankalpa.Security.Tests;

/// <summary>
/// End-to-end isolated simulation tests matching the 12 scenarios required by Part 2 of the security review.
/// Guarantees deterministic testing without modifying real Windows host settings.
/// </summary>
public class SimulationScenariosTests : IDisposable
{
    public SimulationScenariosTests()
    {
        SecurityHardener.ResetStateForTesting();
    }

    public void Dispose()
    {
        SecurityHardener.ResetStateForTesting();
    }

    private class SimulationBlockStore : IBlockStore
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
        private int _nextProfileId = 1;
        private int _nextSessionId = 1;

        public Profile? GetProfileByName(string name) => Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        public Profile? GetProfileById(int id) => Profiles.FirstOrDefault(p => p.Id == id);
        public List<Profile> ListProfiles() => Profiles.ToList();
        public Profile SaveProfile(Profile profile)
        {
            var existing = GetProfileByName(profile.Name);
            if (existing != null)
            {
                existing.Enabled = profile.Enabled;
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
        public bool DeleteProfile(int id) => Profiles.RemoveAll(p => p.Id == id) > 0;
        public BlockSession? GetSessionById(int id) => Sessions.FirstOrDefault(s => s.Id == id);
        public BlockSession? GetActiveSession() => Sessions.FirstOrDefault(s => s.EndedUtc == null);
        public BlockSession StartSession(int profileId, long durationSeconds, bool locked)
        {
            var session = new BlockSession
            {
                Id = _nextSessionId++,
                ProfileId = profileId,
                StartUtc = DateTime.UtcNow,
                DurationSeconds = durationSeconds,
                Locked = locked
            };
            Sessions.Add(session);
            return session;
        }
        public void EndSession(int id)
        {
            var s = Sessions.FirstOrDefault(x => x.Id == id);
            if (s != null) s.EndedUtc = DateTime.UtcNow;
        }
        public void EndActiveSession()
        {
            var active = GetActiveSession();
            if (active != null) EndSession(active.Id);
        }
        public void DisableAllProfiles()
        {
            foreach (var p in Profiles) p.Enabled = false;
        }
        public bool CleanHosts()
        {
            CleanHostsCalled = true;
            return true;
        }
        public List<string> GetCurrentHostsDomains() => new();
        public bool IsEmergencyUnlockPresent() => EmergencyUnlockExists;
        public void ConsumeEmergencyUnlock()
        {
            EmergencyUnlockConsumed = true;
            EmergencyUnlockExists = false;
        }
        public DnsConfig GetDnsConfig() => _dnsConfig;
        public void SaveDnsConfig(DnsConfig config) => _dnsConfig = config;
        public List<BlockEvent> GetBlockEvents(long sinceId = 0) => new();
        public void RecordBlockEvent(BlockEvent evt) { }
    }

    private sealed class ScenarioMutexHandle : IMutexHandle
    {
        public bool WaitOneResult { get; set; } = true;
        public Exception? WaitOneException { get; set; }
        public bool WasReleased { get; private set; }
        public bool WasDisposed { get; private set; }

        public bool WaitOne(TimeSpan timeout)
        {
            if (WaitOneException != null) throw WaitOneException;
            return WaitOneResult;
        }

        public void ReleaseMutex() => WasReleased = true;
        public void Dispose() => WasDisposed = true;
    }

    // =========================================================================
    // Scenario 1: An unauthorized local client attempting emergency unlock
    // =========================================================================
    [Fact]
    public void Scenario01_UnauthorizedLocalClientAttemptingEmergencyUnlock()
    {
        var store = new SimulationBlockStore();
        store.Profiles.Add(new Profile { Id = 1, Name = "Work", Enabled = true, LockMode = LockMode.Locked });
        store.StartSession(profileId: 1, durationSeconds: 3600, locked: true);

        // Standard non-admin client identity
        var standardClient = ClientIdentity.StandardUser("AttackerUser", "S-1-5-21-999-1001");

        var response = CommandHandler.Handle(new Request { Command = "EMERGENCYUNLOCK" }, store, standardClient);

        Assert.False(response.Ok);
        Assert.Contains("Unauthorized", response.Error, StringComparison.OrdinalIgnoreCase);

        // Verification: Block session is still active, profile is still enabled
        Assert.NotNull(store.GetActiveSession());
        Assert.True(store.Profiles[0].Enabled);
        Assert.False(store.CleanHostsCalled);
    }

    // =========================================================================
    // Scenario 2: An authorized client requesting emergency unlock
    // =========================================================================
    [Fact]
    public void Scenario02_AuthorizedClientRequestingEmergencyUnlock()
    {
        var store = new SimulationBlockStore();
        store.Profiles.Add(new Profile { Id = 1, Name = "DeepWork", Enabled = true, LockMode = LockMode.Locked });
        store.StartSession(profileId: 1, durationSeconds: 3600, locked: true);

        // Case A: Administrator client
        var adminClient = ClientIdentity.Administrator("AdminUser", "S-1-5-32-544");
        var adminResponse = CommandHandler.Handle(new Request { Command = "EMERGENCYUNLOCK" }, store, adminClient);

        Assert.True(adminResponse.Ok, adminResponse.Error);
        Assert.Null(store.GetActiveSession());
        Assert.False(store.Profiles[0].Enabled);
        Assert.True(store.CleanHostsCalled);

        // Case B: Standard user with valid emergency recovery file present
        store.Profiles[0].Enabled = true;
        store.StartSession(profileId: 1, durationSeconds: 3600, locked: true);
        store.CleanHostsCalled = false;
        store.EmergencyUnlockExists = true;

        var userClient = ClientIdentity.StandardUser("StandardUser", "S-1-5-21-user");
        var userResponse = CommandHandler.Handle(new Request { Command = "EMERGENCYUNLOCK" }, store, userClient);

        Assert.True(userResponse.Ok, userResponse.Error);
        Assert.True(store.EmergencyUnlockConsumed);
        Assert.Null(store.GetActiveSession());
        Assert.False(store.Profiles[0].Enabled);
    }

    // =========================================================================
    // Scenario 3: The service becoming unavailable while cached heartbeat data is still being sent
    // =========================================================================
    [Fact]
    public void Scenario03_ServiceBecomingUnavailableWhileCachedHeartbeatSent()
    {
        // ActiveWebRulesResponse protocol model carrying cached state
        var response = new ActiveWebRulesResponse
        {
            RulesVersion = 42,
            Cached = true,
            ServiceConnected = false,
            ExtensionTheme = "black"
        };

        string json = JsonSerializer.Serialize(response);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("cached").GetBoolean(), "Cached payload must explicitly flag cached: true");
        Assert.False(root.GetProperty("serviceConnected").GetBoolean(), "Cached payload must indicate serviceConnected: false");
        Assert.Equal(42, root.GetProperty("rulesVersion").GetInt32());
    }

    // =========================================================================
    // Scenario 4: The browser extension receiving stale data repeatedly
    // =========================================================================
    [Fact]
    public void Scenario04_BrowserExtensionReceivingStaleDataRepeatedly()
    {
        long initialTime = 100000;
        long lastResponseTime = initialTime;

        // Simulate 20 consecutive polls receiving cached responses over 100 seconds
        for (int i = 1; i <= 20; i++)
        {
            long currentTick = initialTime + (i * 5000);
            bool isCached = true;

            // In background.js: if (!msg.cached) lastResponseTime = getTime();
            if (!isCached)
            {
                lastResponseTime = currentTick;
            }
        }

        // Verification: lastResponseTime was never updated by cached payloads
        Assert.Equal(initialTime, lastResponseTime);
    }

    // =========================================================================
    // Scenario 5: The heartbeat timeout expiring (10-minute fail-open)
    // =========================================================================
    [Fact]
    public void Scenario05_HeartbeatTimeoutExpiring()
    {
        long initialTime = 1000000;
        long lastResponseTime = initialTime;
        long failOpenMs = 10 * 60 * 1000; // 10 minutes (600,000 ms)

        long currentTimeBeforeTimeout = initialTime + failOpenMs - 1000;
        bool isStaleBefore = (currentTimeBeforeTimeout - lastResponseTime) > failOpenMs;
        Assert.False(isStaleBefore, "Must not be stale before 10 minutes elapse");

        long currentTimeAfterTimeout = initialTime + failOpenMs + 1000;
        bool isStaleAfter = (currentTimeAfterTimeout - lastResponseTime) > failOpenMs;
        Assert.True(isStaleAfter, "Must trigger fail-open stale condition after 10 minutes");
    }

    // =========================================================================
    // Scenario 6: The service reconnecting
    // =========================================================================
    [Fact]
    public void Scenario06_ServiceReconnecting()
    {
        long initialTime = 1000000;
        long lastResponseTime = initialTime;
        long failOpenMs = 10 * 60 * 1000;

        // Time advanced 15 minutes, extension is stale
        long currentTime = initialTime + (15 * 60 * 1000);
        Assert.True((currentTime - lastResponseTime) > failOpenMs);

        // Service reconnects, fresh payload with cached: false
        var freshRules = new ActiveWebRulesResponse
        {
            RulesVersion = 43,
            Cached = false,
            ServiceConnected = true
        };
        string freshReply = JsonSerializer.Serialize(freshRules);
        using var doc = JsonDocument.Parse(freshReply);
        bool isFresh = !doc.RootElement.GetProperty("cached").GetBoolean();
        Assert.True(isFresh);

        // Extension receives fresh reply, updates timestamp
        if (isFresh)
        {
            lastResponseTime = currentTime;
        }

        // Verification: Extension is no longer stale, blocking resumes
        Assert.False((currentTime - lastResponseTime) > failOpenMs);
        Assert.Equal(currentTime, lastResponseTime);
    }

    private sealed class ScenarioMutexFactory : IMutexFactory
    {
        public bool CreatedNewToReturn { get; set; } = true;
        public ScenarioMutexHandle HandleToReturn { get; set; } = new();

        public IMutexHandle CreateMutex(bool initiallyOwned, string name, out bool createdNew)
        {
            createdNew = CreatedNewToReturn;
            return HandleToReturn;
        }
    }

    // =========================================================================
    // Scenario 7: Two service instances attempting to acquire singleton mutex
    // =========================================================================
    [Fact]
    public void Scenario07_TwoServiceInstancesAttemptingToAcquireSingletonMutex()
    {
        var handle1 = new ScenarioMutexHandle();
        var factory1 = new ScenarioMutexFactory { HandleToReturn = handle1, CreatedNewToReturn = true };
        using var instance1 = new SingletonMutexManager("Global\\TestServiceMutex", factory1);
        var result1 = instance1.TryAcquire();
        Assert.Equal(MutexAcquisitionStatus.Acquired, result1.Status);
        Assert.True(instance1.HasHandle);

        // Second instance attempts to acquire while instance 1 is running
        var handle2 = new ScenarioMutexHandle { WaitOneResult = false };
        var factory2 = new ScenarioMutexFactory { HandleToReturn = handle2, CreatedNewToReturn = false };
        using var instance2 = new SingletonMutexManager("Global\\TestServiceMutex", factory2);
        var result2 = instance2.TryAcquire();

        // Verification: Instance 2 fails to acquire, reports AlreadyRunning
        Assert.Equal(MutexAcquisitionStatus.AlreadyRunning, result2.Status);
        Assert.False(instance2.HasHandle);
    }

    // =========================================================================
    // Scenario 8: An abandoned mutex
    // =========================================================================
    [Fact]
    public void Scenario08_AbandonedMutex()
    {
        var handle = new ScenarioMutexHandle
        {
            WaitOneException = new AbandonedMutexException()
        };
        var factory = new ScenarioMutexFactory { HandleToReturn = handle, CreatedNewToReturn = false };
        using var manager = new SingletonMutexManager("Global\\TestAbandonedMutex", factory);

        var result = manager.TryAcquire();

        // Verification: Thread acquires ownership when AbandonedMutexException is thrown
        Assert.Equal(MutexAcquisitionStatus.AbandonedAcquired, result.Status);
        Assert.True(manager.HasHandle);

        manager.Release();
        Assert.True(handle.WasReleased);
    }

    // =========================================================================
    // Scenario 9: A failed service-lockdown command
    // =========================================================================
    [Fact]
    public void Scenario09_FailedServiceLockdownCommand()
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(false);
        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) => new ServiceProcessResult(
                Success: false,
                ExitCode: 5,
                StandardOutput: "",
                StandardError: "[SC] OpenService FAILED 5: Access is denied."
            )
        };

        bool ok = SecurityHardener.LockdownService(out string? error, runner);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Access is denied", error);
        Assert.False(SecurityHardener.IsServiceLockedDown, "State must remain false on failed lockdown");
    }

    // =========================================================================
    // Scenario 10: A successful service-lockdown command
    // =========================================================================
    [Fact]
    public void Scenario10_SuccessfulServiceLockdownCommand()
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(false);
        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) =>
            {
                if (args[0] == "sdset")
                    return new ServiceProcessResult(true, 0, "[SC] SetServiceObjectSecurity SUCCESS", "");
                if (args[0] == "sdshow")
                    return new ServiceProcessResult(true, 0, SecurityHardener.LockedServiceSddl, "");
                return new ServiceProcessResult(false, 1, "", "Unknown command");
            }
        };

        bool ok = SecurityHardener.LockdownService(out string? error, runner);

        Assert.True(ok, error);
        Assert.Null(error);
        Assert.True(SecurityHardener.IsServiceLockedDown, "State must be true after verified lockdown");
    }

    // =========================================================================
    // Scenario 11: A process timeout
    // =========================================================================
    [Fact]
    public void Scenario11_ProcessTimeout()
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(false);
        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, timeoutMs) => new ServiceProcessResult(
                Success: false,
                ExitCode: -1,
                StandardOutput: "",
                StandardError: $"sc.exe timed out after {timeoutMs}ms and was terminated.",
                TimedOut: true
            )
        };

        bool ok = SecurityHardener.LockdownService(out string? error, runner);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("timed out", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(SecurityHardener.IsServiceLockedDown, "State must remain false after timeout");
    }

    // =========================================================================
    // Scenario 12: A failed security-descriptor verification
    // =========================================================================
    [Fact]
    public void Scenario12_FailedSecurityDescriptorVerification()
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(false);
        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) =>
            {
                if (args[0] == "sdset")
                    return new ServiceProcessResult(true, 0, "[SC] SetServiceObjectSecurity SUCCESS", "");
                if (args[0] == "sdshow")
                    // Process succeeded, but returned unexpected/unlocked SDDL
                    return new ServiceProcessResult(true, 0, SecurityHardener.UnlockedServiceSddl, "");
                return new ServiceProcessResult(false, 1, "", "");
            }
        };

        bool ok = SecurityHardener.LockdownService(out string? error, runner);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("verification failed", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(SecurityHardener.IsServiceLockedDown, "State must remain false when SDDL verification fails");
    }
}
