using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blocker.Cli;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Sankalpa.Tests;

public class TestPipeClient : IPipeClient
{
    private readonly IBlockStore _store;
    private readonly IClientIdentity _identity;

    public TestPipeClient(IBlockStore store, IClientIdentity? identity = null)
    {
        _store = store;
        _identity = identity ?? ClientIdentity.Administrator();
    }

    public Task<PipeResult> SendRequestAsync(Request request, CancellationToken cancellationToken = default)
    {
        var response = CommandHandler.Handle(request, _store, _identity);
        return Task.FromResult(PipeResult.Success(response));
    }
}

public class CliTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly BlockerDatabase _db;
    private readonly TestPipeClient _pipeClient;

    public CliTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"cli_test_{Guid.NewGuid():N}.db");
        _db = new BlockerDatabase(_tempDbPath);
        _pipeClient = new TestPipeClient(_db);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempDbPath))
            {
                File.Delete(_tempDbPath);
            }
        }
        catch { }
    }

    [Fact]
    public async Task Cli_NoArgs_PrintsUsage()
    {
        using var sw = new StringWriter();
        int exitCode = await Program.RunAsync(Array.Empty<string>(), _pipeClient, sw, sw);
        Assert.Equal(1, exitCode);
        string output = sw.ToString();
        Assert.Contains("Sankalpa CLI", output);
        Assert.Contains("Meditation & Lockdown Mode", output);
    }

    [Fact]
    public async Task Cli_HelpFlag_PrintsUsage()
    {
        using var sw = new StringWriter();
        int exitCode = await Program.RunAsync(new[] { "--help" }, _pipeClient, sw, sw);
        Assert.Equal(0, exitCode);
        string output = sw.ToString();
        Assert.Contains("Sankalpa CLI", output);
        Assert.Contains("meditation start", output);
    }

    [Fact]
    public async Task Cli_Ping_ReturnsPong()
    {
        using var sw = new StringWriter();
        int exitCode = await Program.RunAsync(new[] { "ping" }, _pipeClient, sw, sw);
        Assert.Equal(0, exitCode);
        Assert.Contains("pong", sw.ToString());
    }

    [Fact]
    public async Task Cli_MeditationStatus_WhenInactive_ReportsNoActiveSession()
    {
        using var sw = new StringWriter();
        int exitCode = await Program.RunAsync(new[] { "meditation", "status" }, _pipeClient, sw, sw);
        Assert.Equal(0, exitCode);
        Assert.Contains("No active meditation session", sw.ToString());
    }

    [Fact]
    public async Task Cli_MeditationStart_And_Status_WorksCorrectly()
    {
        // Start a 20m meditation session with allowed apps and soundscape
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[]
            {
                "meditation", "start",
                "--duration", "20m",
                "--apps", "spotify.exe,notion.exe",
                "--mode", "none",
                "--sound", "rain"
            }, _pipeClient, sw, sw);

            Assert.Equal(0, exitCode);
            string outStr = sw.ToString();
            Assert.Contains("Meditation session started", outStr);
            Assert.Contains("spotify.exe, notion.exe", outStr);
            Assert.Contains("Rain", outStr, StringComparison.OrdinalIgnoreCase);
        }

        // Query status
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[] { "meditation", "status" }, _pipeClient, sw, sw);
            Assert.Equal(0, exitCode);
            string outStr = sw.ToString();
            Assert.Contains("Active Meditation Session", outStr);
            Assert.Contains("spotify.exe, notion.exe", outStr);
            Assert.Contains("Rain", outStr, StringComparison.OrdinalIgnoreCase);
        }

        // Stop session
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[] { "meditation", "stop" }, _pipeClient, sw, sw);
            Assert.Equal(0, exitCode);
            Assert.Contains("Meditation session stopped", sw.ToString());
        }

        // Verify status is now inactive
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[] { "meditation", "status" }, _pipeClient, sw, sw);
            Assert.Equal(0, exitCode);
            Assert.Contains("No active meditation session", sw.ToString());
        }
    }

    [Fact]
    public async Task Cli_MeditationLocked_CannotBeStoppedEarly()
    {
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[]
            {
                "meditation", "start",
                "--minutes", "30",
                "--mode", "locked"
            }, _pipeClient, sw, sw);
            Assert.Equal(0, exitCode);
        }

        // Attempt stop
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[] { "meditation", "stop" }, _pipeClient, sw, sw);
            Assert.Equal(1, exitCode);
            Assert.Contains("cannot be stopped early", sw.ToString());
        }

        // Clean up session in DB for next tests
        var active = _db.GetActiveMeditationSession();
        if (active != null) _db.EndMeditationSession(active.Id, completed: true);
    }

    [Fact]
    public async Task Cli_MeditationNote_And_History_WorksCorrectly()
    {
        // Start a session
        using (var sw = new StringWriter())
        {
            await Program.RunAsync(new[] { "meditation", "start", "--duration", "10m", "--sound", "forest" }, _pipeClient, sw, sw);
        }

        // Save note
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[] { "meditation", "note", "--text", "Felt very calm and focused." }, _pipeClient, sw, sw);
            Assert.Equal(0, exitCode);
            Assert.Contains("Meditation note saved", sw.ToString());
        }

        // End session
        using (var sw = new StringWriter())
        {
            await Program.RunAsync(new[] { "meditation", "stop" }, _pipeClient, sw, sw);
        }

        // Query history
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[] { "meditation", "history", "--limit", "5" }, _pipeClient, sw, sw);
            Assert.Equal(0, exitCode);
            string outStr = sw.ToString();
            Assert.Contains("Meditation History", outStr);
            Assert.Contains("Felt very calm and focused", outStr);
        }
    }

    [Fact]
    public async Task Cli_MeditationSchedule_CrudWorks()
    {
        // 1. Add schedule
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[]
            {
                "meditation", "schedule", "add",
                "--time", "14:30",
                "--days", "Mon,Wed,Fri",
                "--duration", "25m",
                "--sound", "bowls",
                "--apps", "notion.exe"
            }, _pipeClient, sw, sw);

            Assert.Equal(0, exitCode);
            string outStr = sw.ToString();
            Assert.Contains("Meditation schedule saved", outStr);
            Assert.Contains("14:30", outStr);
        }

        // 2. List schedules
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[] { "meditation", "schedule", "list" }, _pipeClient, sw, sw);
            Assert.Equal(0, exitCode);
            string outStr = sw.ToString();
            Assert.Contains("Meditation Schedules", outStr);
            Assert.Contains("14:30", outStr);
        }

        // 3. Snooze schedule
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[] { "meditation", "schedule", "snooze", "--id", "1" }, _pipeClient, sw, sw);
            Assert.Equal(0, exitCode);
            Assert.Contains("snoozed for 5 minutes", sw.ToString());
        }

        // 4. Delete schedule
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[] { "meditation", "schedule", "delete", "--id", "1" }, _pipeClient, sw, sw);
            Assert.Equal(0, exitCode);
            Assert.Contains("Meditation schedule deleted", sw.ToString());
        }
    }

    [Fact]
    public async Task Cli_DelayedUnlock_RequestAndCancel()
    {
        // Seed a profile
        _db.SaveProfile(new Profile
        {
            Name = "WorkProfile",
            Enabled = true,
            LockMode = LockMode.DelayedUnlock,
            DelayedUnlockMinutes = 10
        });

        string token = "";
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[] { "delayed-unlock", "request", "--name", "WorkProfile" }, _pipeClient, sw, sw);
            Assert.Equal(0, exitCode);
            string outStr = sw.ToString();
            Assert.Contains("Delayed unlock initiated", outStr);
            Assert.Contains("Unlock Token:", outStr);

            // Extract token
            int idx = outStr.IndexOf("Unlock Token: ");
            if (idx >= 0)
            {
                int end = outStr.IndexOf('\n', idx);
                token = outStr.Substring(idx + "Unlock Token: ".Length, end - (idx + "Unlock Token: ".Length)).Trim();
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(token));

        // Cancel
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[] { "delayed-unlock", "cancel", "--name", "WorkProfile", "--token", token }, _pipeClient, sw, sw);
            Assert.Equal(0, exitCode);
            Assert.Contains("Delayed unlock cancelled", sw.ToString());
        }
    }

    [Fact]
    public async Task Cli_ProfileRename_WorksCorrectly()
    {
        _db.SaveProfile(new Profile { Name = "OldName", Enabled = true });

        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[] { "profile", "rename", "--name", "OldName", "--new-name", "NewName" }, _pipeClient, sw, sw);
            Assert.Equal(0, exitCode);
            Assert.Contains("successfully renamed to 'NewName'", sw.ToString());
        }

        Assert.Null(_db.GetProfileByName("OldName"));
        Assert.NotNull(_db.GetProfileByName("NewName"));
    }

    [Fact]
    public async Task Cli_DnsChallenge_ReturnsChallengeString()
    {
        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[] { "dns", "challenge" }, _pipeClient, sw, sw);
            Assert.Equal(0, exitCode);
            Assert.Contains("DNS Protection Challenge Text", sw.ToString());
        }

        using (var sw = new StringWriter())
        {
            int exitCode = await Program.RunAsync(new[] { "unlock-challenge", "--dns" }, _pipeClient, sw, sw);
            Assert.Equal(0, exitCode);
            Assert.Contains("DNS Protection Challenge Text", sw.ToString());
        }
    }

    [Fact]
    public async Task Cli_Events_DisplaysRecordedEvents()
    {
        _db.RecordBlockEvent(new BlockEvent
        {
            TimeUtc = DateTime.UtcNow,
            Kind = "App",
            Name = "distraction.exe",
            ProfileName = "Default"
        });

        using var sw = new StringWriter();
        int exitCode = await Program.RunAsync(new[] { "events" }, _pipeClient, sw, sw);
        Assert.Equal(0, exitCode);
        string outStr = sw.ToString();
        Assert.Contains("Security & Block Events", outStr);
        Assert.Contains("distraction.exe", outStr);
    }

    [Fact]
    public async Task Cli_WebRules_DisplaysRules()
    {
        _db.SaveProfile(new Profile
        {
            Name = "WebShield",
            Enabled = true,
            BlockedDomains = new List<string> { "youtube.com", "reddit.com" }
        });

        using var sw = new StringWriter();
        int exitCode = await Program.RunAsync(new[] { "webrules" }, _pipeClient, sw, sw);
        Assert.Equal(0, exitCode);
        string outStr = sw.ToString();
        Assert.Contains("Active Web Protection Rules", outStr);
    }

    [Theory]
    [InlineData("15m", 900)]
    [InlineData("2h", 7200)]
    [InlineData("45s", 45)]
    [InlineData("300", 300)]
    public void TryParseDuration_ParsesCorrectly(string input, long expected)
    {
        bool ok = Program.TryParseDuration(input, out long sec);
        Assert.True(ok);
        Assert.Equal(expected, sec);
    }
}
