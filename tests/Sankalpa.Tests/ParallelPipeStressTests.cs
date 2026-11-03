using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Blocker.Core.Tests;

[SupportedOSPlatform("windows")]
public class ParallelPipeStressTests
{
    private class InProcessMockStore : IBlockStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<int, Profile> _profiles = new();
        private int _nextProfileId = 1;
        private BlockSession? _activeSession;
        private int _nextSessionId = 1;
        private DnsConfig _dnsConfig = new();
        private readonly List<BlockEvent> _events = new();

        public Profile? GetProfileByName(string name)
        {
            lock (_lock)
            {
                var found = _profiles.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                return found != null ? CloneProfile(found) : null;
            }
        }

        public Profile? GetProfileById(int id)
        {
            lock (_lock)
            {
                return _profiles.TryGetValue(id, out var p) ? CloneProfile(p) : null;
            }
        }

        public List<Profile> ListProfiles()
        {
            lock (_lock)
            {
                return _profiles.Values.Select(CloneProfile).ToList();
            }
        }

        public Profile SaveProfile(Profile profile)
        {
            lock (_lock)
            {
                if (profile.Id == 0)
                {
                    profile.Id = _nextProfileId++;
                }
                else if (profile.Id >= _nextProfileId)
                {
                    _nextProfileId = profile.Id + 1;
                }

                var cloned = CloneProfile(profile);
                _profiles[profile.Id] = cloned;
                return CloneProfile(cloned);
            }
        }

        public bool RenameProfile(int id, string newName)
        {
            lock (_lock)
            {
                if (_profiles.TryGetValue(id, out var p))
                {
                    p.Name = newName;
                    return true;
                }
                return false;
            }
        }

        public bool DeleteProfileByName(string name)
        {
            lock (_lock)
            {
                var p = _profiles.Values.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (p != null)
                {
                    _profiles.Remove(p.Id);
                    return true;
                }
                return false;
            }
        }

        public bool DeleteProfile(int id)
        {
            lock (_lock)
            {
                return _profiles.Remove(id);
            }
        }

        public void DisableAllProfiles()
        {
            lock (_lock)
            {
                foreach (var p in _profiles.Values)
                {
                    p.Enabled = false;
                }
            }
        }

        public BlockSession? GetActiveSession()
        {
            lock (_lock)
            {
                return _activeSession != null ? CloneSession(_activeSession) : null;
            }
        }

        public BlockSession? GetSessionById(int id)
        {
            lock (_lock)
            {
                return _activeSession?.Id == id ? CloneSession(_activeSession) : null;
            }
        }

        public BlockSession StartSession(int profileId, long durationSeconds, bool locked)
        {
            lock (_lock)
            {
                var session = new BlockSession
                {
                    Id = _nextSessionId++,
                    ProfileId = profileId,
                    DurationSeconds = durationSeconds,
                    Locked = locked,
                    StartUtc = DateTime.UtcNow
                };
                _activeSession = session;
                return CloneSession(session);
            }
        }

        public void EndActiveSession()
        {
            lock (_lock)
            {
                _activeSession = null;
            }
        }

        public void EndSession(int sessionId)
        {
            lock (_lock)
            {
                if (_activeSession?.Id == sessionId)
                {
                    _activeSession = null;
                }
            }
        }

        public bool IsEmergencyUnlockPresent() => false;
        public void ConsumeEmergencyUnlock() { }
        public bool CleanHosts() => true;

        public bool ServiceElevated { get; set; } = true;
        public bool HostsBlockingOk { get; set; } = true;
        public HostsState HostsState { get; set; } = HostsState.Idle;
        public int HostsDomainCount { get; set; } = 0;
        public string? HostsLastError { get; set; }
        public List<string> GetCurrentHostsDomains() => new();

        public void RecordBlockEvent(BlockEvent evt)
        {
            lock (_lock)
            {
                _events.Add(evt);
            }
        }

        public List<BlockEvent> GetBlockEvents(long sinceId)
        {
            lock (_lock)
            {
                return _events.Where(e => e.Id > sinceId).ToList();
            }
        }

        public FallbackPlanner FallbackPlanner { get; } = new();
        public FallbackPlanResult? LastFallbackPlan { get; set; }

        public DnsConfig GetDnsConfig()
        {
            lock (_lock)
            {
                return new DnsConfig
                {
                    Enabled = _dnsConfig.Enabled,
                    Locked = _dnsConfig.Locked,
                    PresetName = _dnsConfig.PresetName,
                    PrimaryDns = _dnsConfig.PrimaryDns,
                    SecondaryDns = _dnsConfig.SecondaryDns,
                    PrimaryIpv6 = _dnsConfig.PrimaryIpv6,
                    SecondaryIpv6 = _dnsConfig.SecondaryIpv6,
                    LockType = _dnsConfig.LockType,
                    LockDurationSeconds = _dnsConfig.LockDurationSeconds,
                    LockedUntilUtc = _dnsConfig.LockedUntilUtc
                };
            }
        }

        public void SaveDnsConfig(DnsConfig config)
        {
            lock (_lock)
            {
                _dnsConfig = config;
            }
        }

        private static Profile CloneProfile(Profile p) => new()
        {
            Id = p.Id,
            Name = p.Name,
            BlockedProcesses = new List<string>(p.BlockedProcesses ?? new List<string>()),
            BlockedDomains = new List<string>(p.BlockedDomains ?? new List<string>()),
            WebRules = (p.WebRules ?? new List<WebRule>()).Select(r => new WebRule { Pattern = r.Pattern, Allow = r.Allow }).ToList(),
            BlockAllWebsites = p.BlockAllWebsites,
            SystemWide = p.SystemWide,
            Enabled = p.Enabled,
            Schedule = p.Schedule != null ? new List<ScheduleRule>(p.Schedule) : new(),
            LockMode = p.LockMode,
            PasswordSalt = p.PasswordSalt,
            PasswordHash = p.PasswordHash
        };

        private static BlockSession CloneSession(BlockSession s) => new()
        {
            Id = s.Id,
            ProfileId = s.ProfileId,
            DurationSeconds = s.DurationSeconds,
            Locked = s.Locked,
            StartUtc = s.StartUtc
        };
    }

    [Fact]
    public async Task FiftyParallelClients_ExecuteConcurrently_WithoutDeadlockOrCorruption()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string pipeName = $"StressTest_Parallel50_{Guid.NewGuid():N}";
        using var serverCts = new CancellationTokenSource();
        var store = new InProcessMockStore();

        // 1. Pre-seed profiles for AddWebsite (15 profiles: Profile_20..Profile_34)
        for (int i = 20; i < 35; i++)
        {
            store.SaveProfile(new Profile
            {
                Name = $"Profile_{i}",
                Enabled = true,
                WebRules = new List<WebRule>(),
                BlockedDomains = new List<string>()
            });
        }

        // 2. Pre-seed profiles for RenameProfile (13 profiles: Profile_35..Profile_47)
        for (int i = 35; i < 48; i++)
        {
            store.SaveProfile(new Profile
            {
                Name = $"Profile_{i}",
                Enabled = true,
                WebRules = new List<WebRule>(),
                BlockedDomains = new List<string>()
            });
        }

        // 3. Pre-seed profile for StartSession
        store.SaveProfile(new Profile
        {
            Name = "SessionProfile",
            Enabled = true,
            WebRules = new List<WebRule>(),
            BlockedDomains = new List<string>()
        });

        var server = new PipeServer(store, pipeName: pipeName);
        var serverTask = server.StartAsync(serverCts.Token);

        // Allow pipe server to start listening
        await Task.Delay(250);

        // 4. Setup background poller reading status & profiles concurrently
        using var pollerCts = new CancellationTokenSource();
        int pollerCycles = 0;
        var pollerTask = Task.Run(async () =>
        {
            var pollerClient = new PipeClient(pipeName, timeoutMs: 15000);
            while (!pollerCts.Token.IsCancellationRequested)
            {
                try
                {
                    var sRes = await pollerClient.SendRequestAsync(new Request { Command = CommandNames.GetStatus }, pollerCts.Token);
                    var pRes = await pollerClient.SendRequestAsync(new Request { Command = CommandNames.ListProfiles }, pollerCts.Token);
                    if (sRes.Connected && pRes.Connected)
                    {
                        Interlocked.Increment(ref pollerCycles);
                    }
                    await Task.Delay(20, pollerCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore transient interruptions during shutdown
                }
            }
        });

        try
        {
            // 5. Define 50 parallel client tasks firing distinct commands
            var clientTasks = new Task<PipeResult>[50];

            // 10 x Ping (0..9)
            for (int i = 0; i < 10; i++)
            {
                clientTasks[i] = Task.Run(async () =>
                {
                    var client = new PipeClient(pipeName, timeoutMs: 15000);
                    return await client.SendRequestAsync(new Request { Command = CommandNames.Ping });
                });
            }

            // 10 x GetStatus (10..19)
            for (int i = 10; i < 20; i++)
            {
                clientTasks[i] = Task.Run(async () =>
                {
                    var client = new PipeClient(pipeName, timeoutMs: 15000);
                    return await client.SendRequestAsync(new Request { Command = CommandNames.GetStatus });
                });
            }

            // 15 x AddWebsite (20..34)
            for (int i = 20; i < 35; i++)
            {
                int idx = i;
                clientTasks[i] = Task.Run(async () =>
                {
                    var client = new PipeClient(pipeName, timeoutMs: 15000);
                    return await client.SendRequestAsync(new Request
                    {
                        Command = CommandNames.AddWebsite,
                        Args = new Dictionary<string, string>
                        {
                            ["profile_name"] = $"Profile_{idx}",
                            ["domain"] = $"stress-test-{idx}.example.org"
                        }
                    });
                });
            }

            // 13 x RenameProfile (35..47)
            for (int i = 35; i < 48; i++)
            {
                int idx = i;
                clientTasks[i] = Task.Run(async () =>
                {
                    var client = new PipeClient(pipeName, timeoutMs: 15000);
                    return await client.SendRequestAsync(new Request
                    {
                        Command = CommandNames.RenameProfile,
                        Args = new Dictionary<string, string>
                        {
                            ["old_name"] = $"Profile_{idx}",
                            ["new_name"] = $"Profile_{idx}_Renamed"
                        }
                    });
                });
            }

            // 1 x StartSession (48)
            clientTasks[48] = Task.Run(async () =>
            {
                var client = new PipeClient(pipeName, timeoutMs: 15000);
                return await client.SendRequestAsync(new Request
                {
                    Command = CommandNames.StartSession,
                    Args = new Dictionary<string, string>
                    {
                        ["profile"] = "SessionProfile",
                        ["seconds"] = "120",
                        ["locked"] = "false"
                    }
                });
            });

            // 1 x StopSession (49)
            clientTasks[49] = Task.Run(async () =>
            {
                var client = new PipeClient(pipeName, timeoutMs: 15000);
                return await client.SendRequestAsync(new Request
                {
                    Command = CommandNames.StopSession
                });
            });

            // 6. Await all 50 tasks with 30s timeout guard against deadlocks
            var allTasks = Task.WhenAll(clientTasks);
            var completed = await Task.WhenAny(allTasks, Task.Delay(30000));
            Assert.True(completed == allTasks, "Deadlock detected! Parallel client tasks did not finish within 30 seconds.");

            var results = await allTasks;

            // Stop background poller
            pollerCts.Cancel();
            await pollerTask;

            // 7. Assert all 50 commands completed successfully with valid JSON responses
            Assert.Equal(50, results.Length);
            for (int i = 0; i < results.Length; i++)
            {
                var res = results[i];
                Assert.True(res.Connected, $"Client task {i} failed to connect to pipe: {res.Error}");
                Assert.NotNull(res.Response);
                Assert.True(res.Response.Ok, $"Client task {i} returned error: {res.Response.Error}");

                // Verify valid JSON response
                string json = JsonSerializer.Serialize(res.Response);
                using var doc = JsonDocument.Parse(json);
                bool okVal = doc.RootElement.TryGetProperty("ok", out var okProp) 
                    ? okProp.GetBoolean() 
                    : doc.RootElement.GetProperty("Ok").GetBoolean();
                Assert.True(okVal);
            }

            // 8. Assert background poller ran concurrently
            Assert.True(pollerCycles > 0, $"Expected background poller to complete cycles, got {pollerCycles}.");

            // 9. Assert zero corrupted state
            for (int i = 20; i < 35; i++)
            {
                var p = store.GetProfileByName($"Profile_{i}");
                Assert.NotNull(p);
                Assert.Contains(p.BlockedDomains, d => d.Equals($"stress-test-{i}.example.org", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(p.WebRules, r => r.Pattern.Equals($"stress-test-{i}.example.org", StringComparison.OrdinalIgnoreCase));
            }

            for (int i = 35; i < 48; i++)
            {
                var p = store.GetProfileByName($"Profile_{i}_Renamed");
                Assert.NotNull(p);
                Assert.Equal($"Profile_{i}_Renamed", p.Name);
                Assert.Null(store.GetProfileByName($"Profile_{i}"));
            }
        }
        finally
        {
            pollerCts.Cancel();
            serverCts.Cancel();
            await server.StopAsync(CancellationToken.None);
        }
    }
}
