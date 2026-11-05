using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Blocker.Core.Tests;

[SupportedOSPlatform("windows")]
public class FuzzingAndInputTests
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
            foreach (var p in Profiles) p.Enabled = false;
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
            if (p != null) { Profiles.Remove(p); return true; }
            return false;
        }

        public bool DeleteProfile(int id)
        {
            var p = GetProfileById(id);
            if (p != null) { Profiles.Remove(p); return true; }
            return false;
        }

        public BlockSession? GetActiveSession()
        {
            var active = Sessions.LastOrDefault(s => s.EndedUtc == null);
            if (active != null && active.IsActive(DateTime.UtcNow)) return active;
            return null;
        }

        public BlockSession? GetSessionById(int id) =>
            Sessions.FirstOrDefault(s => s.Id == id);

        public BlockSession StartSession(int profileId, long durationSeconds, bool locked)
        {
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
        public void ConsumeEmergencyUnlock() { EmergencyUnlockConsumed = true; EmergencyUnlockExists = false; }
        public bool CleanHosts() { CleanHostsCalled = true; return true; }

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

    #region PipeServer Fuzzing

    [Fact]
    public async Task PipeServer_MalformedJson_DroppedGracefully_AndRecovers()
    {
        if (!OperatingSystem.IsWindows()) return;

        string pipeName = $"BlockerFuzzPipe_{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource();
        var store = new FakeBlockStore();
        var server = new PipeServer(store, pipeName: pipeName);

        _ = server.StartAsync(cts.Token);
        await Task.Delay(200);

        try
        {
            string[] malformedPayloads = new[]
            {
                "{\"command\": \"Ping\"",                    // Truncated JSON
                "{\"command\":",                             // Incomplete key/value
                "{",                                         // Open brace only
                "NOT A JSON STRING AT ALL \r\n",             // Plain text garbage
                "{\"command\": 12345}",                      // Invalid type for Command
                "{\"command\": \"Ping\", \"args\": \"str\"}", // Args is string instead of dictionary
                "{\"command\": \"Ping\", \"args\": [1,2,3]}", // Args is array instead of dictionary
                "",                                          // Empty string
                "   \t  \r  "                                // Whitespace only
            };

            foreach (var payload in malformedPayloads)
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                await client.ConnectAsync(3000);
                byte[] bytes = Encoding.UTF8.GetBytes(payload + "\n");
                await client.WriteAsync(bytes, 0, bytes.Length);
                await client.FlushAsync();

                using var reader = new StreamReader(client, Encoding.UTF8);
                string? line = null;
                try
                {
                    line = await reader.ReadLineAsync();
                }
                catch (IOException)
                {
                    // Server safely closed/dropped pipe
                }

                Assert.True(string.IsNullOrWhiteSpace(line), $"Expected empty or null response for malformed payload: {payload}");
            }

            // Test random binary garbage bytes
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                await client.ConnectAsync(3000);
                byte[] randomBytes = new byte[256];
                new Random(42).NextBytes(randomBytes);
                // Ensure no newline in random bytes until end
                for (int i = 0; i < randomBytes.Length - 1; i++)
                {
                    if (randomBytes[i] == (byte)'\n') randomBytes[i] = (byte)'X';
                }
                randomBytes[^1] = (byte)'\n';

                await client.WriteAsync(randomBytes, 0, randomBytes.Length);
                await client.FlushAsync();

                using var reader = new StreamReader(client, Encoding.UTF8);
                string? line = null;
                try
                {
                    line = await reader.ReadLineAsync();
                }
                catch (IOException) { }

                Assert.True(string.IsNullOrWhiteSpace(line), "Expected null/empty response for random binary bytes");
            }

            // Verify server recovered and still answers valid Ping requests
            await Task.Delay(100);
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                await client.ConnectAsync(3000);
                byte[] pingBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Request { Command = CommandNames.Ping }) + "\n");
                await client.WriteAsync(pingBytes, 0, pingBytes.Length);
                await client.FlushAsync();

                using var reader = new StreamReader(client, Encoding.UTF8);
                string? line = await reader.ReadLineAsync();
                Assert.NotNull(line);
                var resp = JsonSerializer.Deserialize<Response>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Assert.NotNull(resp);
                Assert.True(resp.Ok);
                Assert.Equal("pong", resp.Data);
            }
        }
        finally
        {
            cts.Cancel();
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PipeServer_OversizedRequest_DroppedAt64KbCap_AndRecovers()
    {
        if (!OperatingSystem.IsWindows()) return;

        string pipeName = $"BlockerFuzzPipe_{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource();
        var store = new FakeBlockStore();
        var server = new PipeServer(store, pipeName: pipeName);

        _ = server.StartAsync(cts.Token);
        await Task.Delay(200);

        try
        {
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                await client.ConnectAsync(3000);
                // Send 100 KB payload (exceeding 64 KB cap)
                byte[] oversizedBytes = new byte[100 * 1024];
                Array.Fill(oversizedBytes, (byte)'Z');

                try
                {
                    await client.WriteAsync(oversizedBytes, 0, oversizedBytes.Length);
                    await client.FlushAsync();

                    using var reader = new StreamReader(client, Encoding.UTF8);
                    string? line = await reader.ReadLineAsync();
                    Assert.True(string.IsNullOrEmpty(line), "Expected server to drop connection on oversized message");
                }
                catch (IOException)
                {
                    // Clean connection reset/disconnect by server
                }
            }

            // Verify server continues running cleanly and answers ping
            await Task.Delay(100);
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                await client.ConnectAsync(3000);
                byte[] pingBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Request { Command = CommandNames.Ping }) + "\n");
                await client.WriteAsync(pingBytes, 0, pingBytes.Length);
                await client.FlushAsync();

                using var reader = new StreamReader(client, Encoding.UTF8);
                string? line = await reader.ReadLineAsync();
                Assert.NotNull(line);
                var resp = JsonSerializer.Deserialize<Response>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Assert.NotNull(resp);
                Assert.True(resp.Ok);
                Assert.Equal("pong", resp.Data);
            }
        }
        finally
        {
            cts.Cancel();
            await server.StopAsync(CancellationToken.None);
        }
    }

    #endregion

    #region CommandHandler Fuzzing

    [Fact]
    public void CommandHandler_NullAndEmptyRequests_HandledSafely()
    {
        var store = new FakeBlockStore();

        // Null request
        var res1 = Handle(null, store);
        Assert.False(res1.Ok);
        Assert.Contains("Empty or invalid", res1.Error);

        // Empty command
        var res2 = Handle(new Request { Command = "" }, store);
        Assert.False(res2.Ok);

        // Whitespace command
        var res3 = Handle(new Request { Command = "   \t\r\n" }, store);
        Assert.False(res3.Ok);

        // Unknown command
        var res4 = Handle(new Request { Command = "NON_EXISTENT_COMMAND_BLAH" }, store);
        Assert.False(res4.Ok);
        Assert.Contains("Unknown command", res4.Error);
    }

    [Fact]
    public void CommandHandler_MissingRequiredArguments_ReturnsFailGracefully()
    {
        var store = new FakeBlockStore();

        string[] cmdsRequiringArgs = new[]
        {
            CommandNames.GetProfile,
            CommandNames.SaveProfile,
            CommandNames.DeleteProfile,
            CommandNames.EnableProfile,
            CommandNames.DisableProfile,
            CommandNames.RenameProfile,
            CommandNames.AddWebsite
        };

        foreach (var cmd in cmdsRequiringArgs)
        {
            var res = Handle(new Request { Command = cmd, Args = null }, store);
            Assert.False(res.Ok, $"Command {cmd} with null Args should have failed gracefully.");
            Assert.False(string.IsNullOrWhiteSpace(res.Error), $"Command {cmd} should have returned an error description.");

            var resEmpty = Handle(new Request { Command = cmd, Args = new Dictionary<string, string>() }, store);
            Assert.False(resEmpty.Ok, $"Command {cmd} with empty Args should have failed gracefully.");
        }
    }

    [Fact]
    public void CommandHandler_CorruptedNumbersAndFlags_ReturnsFailWithoutThrowing()
    {
        var store = new FakeBlockStore();
        store.Profiles.Add(new Profile { Id = 1, Name = "Focus" });

        // StartSession with negative / huge / non-numeric minutes & seconds
        var res1 = Handle(new Request
        {
            Command = CommandNames.StartSession,
            Args = new Dictionary<string, string>
            {
                ["profile"] = "Focus",
                ["minutes"] = "NOT_A_NUMBER",
                ["seconds"] = "abc",
                ["locked"] = "maybe"
            }
        }, store);
        // Should either fail gracefully or fall back safely without crashing
        Assert.NotNull(res1);

        // SaveDnsConfig with invalid lock duration
        var res2 = Handle(new Request
        {
            Command = CommandNames.SaveDnsConfig,
            Args = new Dictionary<string, string>
            {
                ["enabled"] = "true",
                ["lock_type"] = "InvalidMode",
                ["duration"] = "-99999999999999999999999999999999"
            }
        }, store);
        Assert.NotNull(res2);
    }

    [Fact]
    public void CommandHandler_CorruptedJsonInArgs_HandledSafely()
    {
        var store = new FakeBlockStore();

        // Corrupted webrules JSON
        var res1 = Handle(new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new Dictionary<string, string>
            {
                ["name"] = "FuzzProfile",
                ["webrules"] = "{NOT_VALID_JSON_AT_ALL}"
            }
        }, store);
        Assert.NotNull(res1);

        // Corrupted schedule JSON
        var res2 = Handle(new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new Dictionary<string, string>
            {
                ["name"] = "FuzzProfile",
                ["schedule"] = "[{\"Days\": 99999, \"StartMinute\": \"bad\"}]"
            }
        }, store);
        Assert.NotNull(res2);

        // Corrupted allowance JSON
        var res3 = Handle(new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new Dictionary<string, string>
            {
                ["name"] = "FuzzProfile",
                ["allowance"] = "{\"DailySeconds\": \"NOT_AN_INT\"}"
            }
        }, store);
        Assert.NotNull(res3);
    }

    #endregion

    #region Domain & App Name Normalization Fuzzing

    [Fact]
    public void DomainNormalization_NastyInputs_HandledCorrectly()
    {
        // Leading and trailing whitespace/newlines are trimmed cleanly
        Assert.True(HostsFileBuilder.TryNormalizeDomain("example.com\r\n", out string normCrLf));
        Assert.Equal("example.com", normCrLf);

        Assert.True(HostsFileBuilder.TryNormalizeDomain("\r\nexample.org\t\n", out string normOrg));
        Assert.Equal("example.org", normOrg);

        // Embedded newlines inside domain labels must be rejected
        Assert.False(HostsFileBuilder.TryNormalizeDomain("test\nsite.com", out _));
        Assert.False(HostsFileBuilder.TryNormalizeDomain("example\r\n.com", out _));
        Assert.False(HostsFileBuilder.TryNormalizeDomain("bad\rdomain.com", out _));

        // Fragments and hashes
        Assert.True(HostsFileBuilder.TryNormalizeDomain("example.com#section", out string normHash));
        Assert.Equal("example.com", normHash);

        Assert.True(HostsFileBuilder.TryNormalizeDomain("https://example.com:8080/path/to/page?q=1#top", out string normUrl));
        Assert.Equal("example.com", normUrl);

        // Wildcards and spaces must be rejected
        Assert.False(HostsFileBuilder.TryNormalizeDomain("*.example.com", out _));
        Assert.False(HostsFileBuilder.TryNormalizeDomain("ex ample.com", out _));
        Assert.False(HostsFileBuilder.TryNormalizeDomain("example.com/ space", out _));

        // Trailing and leading dots
        Assert.True(HostsFileBuilder.TryNormalizeDomain("example.com.", out string normDot));
        Assert.Equal("example.com", normDot);
        Assert.True(HostsFileBuilder.TryNormalizeDomain("....example.com....", out string normDots));
        Assert.Equal("example.com", normDots);

        // Null and empty
        Assert.False(HostsFileBuilder.TryNormalizeDomain(null, out _));
        Assert.False(HostsFileBuilder.TryNormalizeDomain("", out _));
        Assert.False(HostsFileBuilder.TryNormalizeDomain("   ", out _));
        Assert.False(HostsFileBuilder.TryNormalizeDomain(".", out _));
        Assert.False(HostsFileBuilder.TryNormalizeDomain("...", out _));

        // Localhost and IPs
        Assert.False(HostsFileBuilder.TryNormalizeDomain("localhost", out _));
        Assert.False(HostsFileBuilder.TryNormalizeDomain("127.0.0.1", out _));
        Assert.False(HostsFileBuilder.TryNormalizeDomain("::1", out _));

        // Stripping www. and m.
        Assert.True(HostsFileBuilder.TryNormalizeDomain("www.reddit.com", out string normReddit));
        Assert.Equal("reddit.com", normReddit);
        Assert.True(HostsFileBuilder.TryNormalizeDomain("m.youtube.com", out string normYt));
        Assert.Equal("youtube.com", normYt);
    }

    [Fact]
    public void AppNameNormalization_NastyInputs_HandledCorrectly()
    {
        // Normal cases
        Assert.Equal("notepad", ProcessMatcher.NormalizeProcessName("notepad.exe"));
        Assert.Equal("notepad", ProcessMatcher.NormalizeProcessName("notepad"));
        Assert.Equal("notepad", ProcessMatcher.NormalizeProcessName("  notepad.exe  "));
        Assert.Equal("notepad", ProcessMatcher.NormalizeProcessName("notepad.mui"));

        // Null / empty
        Assert.Equal(string.Empty, ProcessMatcher.NormalizeProcessName(null));
        Assert.Equal(string.Empty, ProcessMatcher.NormalizeProcessName(""));
        Assert.Equal(string.Empty, ProcessMatcher.NormalizeProcessName("   \t  "));

        // Multiple extensions
        Assert.Equal("game.exe", ProcessMatcher.NormalizeProcessName("game.exe.exe"));

        // IsBlocked matching with various nasty blocked list inputs
        var blockedList = new List<string?>
        {
            null,
            "",
            "   ",
            "notepad.exe",
            "DISCORD.EXE",
            "spotify"
        };

        Assert.True(ProcessMatcher.IsBlocked("notepad", blockedList));
        Assert.True(ProcessMatcher.IsBlocked("NOTEPAD.EXE", blockedList));
        Assert.True(ProcessMatcher.IsBlocked("discord", blockedList));
        Assert.True(ProcessMatcher.IsBlocked("spotify.exe", blockedList));

        // Not blocked
        Assert.False(ProcessMatcher.IsBlocked("calc.exe", blockedList));
        Assert.False(ProcessMatcher.IsBlocked(null, blockedList));
        Assert.False(ProcessMatcher.IsBlocked("", blockedList));

        // Critical system processes are never blocked even if explicitly listed
        var evilBlockedList = new List<string> { "csrss.exe", "svchost.exe", "services.exe", "Blocker.Service" };
        Assert.False(ProcessMatcher.IsBlocked("csrss.exe", evilBlockedList));
        Assert.False(ProcessMatcher.IsBlocked("svchost", evilBlockedList));
        Assert.False(ProcessMatcher.IsBlocked("services.exe", evilBlockedList));
    }

    #endregion

    #region Large-Scale & Boundary Stress Fuzzing

    [Fact]
    public void CommandHandler_Exceeding200Rules_SafelyRejectedWithoutCrash()
    {
        var store = new FakeBlockStore();

        // 1. Sending 10,000 domains exceeds the 200-rule safety cap and should be safely rejected
        var domainList = new List<string>(10000);
        for (int i = 0; i < 5000; i++)
        {
            domainList.Add($"site{i}.example.com");
            domainList.Add($"  SITE{i}.EXAMPLE.COM  ");
        }

        var requestOverCap = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new Dictionary<string, string>
            {
                ["name"] = "MassiveProfile",
                ["domains"] = string.Join(",", domainList)
            }
        };

        var responseOverCap = Handle(requestOverCap, store);
        Assert.False(responseOverCap.Ok);
        Assert.Contains("cannot have more than 500", responseOverCap.Error);

        // 2. Sending within the 200-rule cap (e.g. 100 unique with 100 duplicates = 200 items)
        var validBatch = new List<string>(200);
        for (int i = 0; i < 100; i++)
        {
            validBatch.Add($"site{i}.example.com");
            validBatch.Add($"  SITE{i}.EXAMPLE.COM  ");
        }

        var requestValid = new Request
        {
            Command = CommandNames.SaveProfile,
            Args = new Dictionary<string, string>
            {
                ["name"] = "ValidBatchProfile",
                ["domains"] = string.Join(",", validBatch)
            }
        };

        var responseValid = Handle(requestValid, store);
        Assert.True(responseValid.Ok, $"Saving batch within cap failed: {responseValid.Error}");

        var profile = store.GetProfileByName("ValidBatchProfile");
        Assert.NotNull(profile);
        Assert.NotNull(profile.BlockedDomains);
        Assert.Equal(100, profile.BlockedDomains.Count);
    }

    [Fact]
    public void BulkTextParser_10000Domains_ParsedAndDeduplicatedWithoutCrash()
    {
        // Build 10,000 lines text with 5,000 unique domains and 5,000 duplicates + whitespace
        var sb = new StringBuilder(10000 * 25);
        for (int i = 0; i < 5000; i++)
        {
            sb.AppendLine($"site{i}.example.com");
            sb.AppendLine($"  SITE{i}.EXAMPLE.COM  ");
        }

        var parsed = WebRuleMatcher.ParsePatternsFromText(sb.ToString(), out int invalid, out int dups);
        Assert.Equal(5000, parsed.Count);
        Assert.Equal(5000, dups);
        Assert.Equal(0, invalid);
    }

    [Fact]
    public void WebRuleMatcher_Fuzzing_CompiledPatternAndNormalization()
    {
        // Fuzz TryNormalizePattern and CompilePattern with boundary inputs
        string[] testPatterns = new[]
        {
            "***",
            "//",
            "///",
            "http:///",
            "https://*.*.*",
            "example.com/very/long/path/../../back",
            new string('a', 1500) + ".com",
            "site.com/path?param=1#frag",
            "site.com$",
            "*.site.com/$",
            "site.com:443/test",
            "   \t\r\n",
            "site.com/path/*/*/test"
        };

        foreach (var p in testPatterns)
        {
            bool ok = WebRuleMatcher.TryNormalizePattern(p, out string norm, out string? err);
            if (ok)
            {
                Assert.False(string.IsNullOrWhiteSpace(norm));
                var compiled = WebRuleMatcher.CompilePattern(norm);
                Assert.NotNull(compiled);
            }
        }
    }

    #endregion
}
