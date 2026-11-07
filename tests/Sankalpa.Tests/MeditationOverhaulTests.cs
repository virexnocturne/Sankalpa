using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Blocker.Core;
using Blocker.Service;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Sankalpa.Tests;

public class MeditationOverhaulTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly BlockerDatabase _db;

    public MeditationOverhaulTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"meditation_overhaul_{Guid.NewGuid():N}.db");
        _db = new BlockerDatabase(_tempDbPath);
    }

    public void Dispose()
    {
        ProcessSuspender.Default.ResumeAll();
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
    public void BlockerDatabase_SaveMeditationSchedule_EnforcesSingletonUpsert()
    {
        // 1. Save schedule initially (Id = 1)
        var s1 = new MeditationSchedule
        {
            Id = 1,
            Enabled = true,
            TimeOfDay = "09:00",
            DurationSeconds = 1200,
            LockMode = LockMode.None,
            Soundscape = "Rain"
        };
        _db.SaveMeditationSchedule(s1);

        // 2. Save again with Id = 0, verifying it still targets singleton Id = 1
        var s2 = new MeditationSchedule
        {
            Id = 0,
            Enabled = true,
            TimeOfDay = "10:30",
            DurationSeconds = 1800,
            LockMode = LockMode.Password,
            Soundscape = "Forest",
            PasswordHash = "hash123"
        };
        _db.SaveMeditationSchedule(s2);

        // 3. Save again with Id = 999, verifying it forces Id = 1
        var s3 = new MeditationSchedule
        {
            Id = 999,
            Enabled = false,
            TimeOfDay = "11:00",
            DurationSeconds = 2400,
            LockMode = LockMode.TypingChallenge,
            Soundscape = "Library"
        };
        _db.SaveMeditationSchedule(s3);

        // 4. Verify directly in SQLite that exactly 1 row exists with Id = 1
        using var conn = new SqliteConnection($"Data Source={_tempDbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), Id, Enabled, TimeOfDay, DurationSeconds, LockMode, Soundscape FROM MeditationSchedules;";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        long count = reader.GetInt64(0);
        int id = reader.GetInt32(1);
        long enabled = reader.GetInt64(2);
        string time = reader.GetString(3);
        long duration = reader.GetInt64(4);
        int lockMode = reader.GetInt32(5);
        string soundscape = reader.GetString(6);

        Assert.Equal(1, count);
        Assert.Equal(1, id);
        Assert.Equal(0, enabled);
        Assert.Equal("11:00", time);
        Assert.Equal(2400, duration);
        Assert.Equal((int)LockMode.TypingChallenge, lockMode);
        Assert.Equal("Library", soundscape);
    }

    [Fact]
    public void BlockerDatabase_Migration_PreservesNewestRowAsId1()
    {
        string migrationDbPath = Path.Combine(Path.GetTempPath(), $"meditation_mig_{Guid.NewGuid():N}.db");
        try
        {
            // 1. Initialize full schema cleanly with BlockerDatabase
            var initialDb = new BlockerDatabase(migrationDbPath);

            // 2. Simulate legacy bug state by injecting duplicate rows (Id 2 and Id 3) with row 3 being the newest configuration
            using (var conn = new SqliteConnection($"Data Source={migrationDbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                UPDATE MeditationSchedules
                SET Enabled = 0, DaysOfWeekJson = '["Monday"]', TimeOfDay = '07:00', DurationSeconds = 600, LockMode = 0, Soundscape = 'Rain'
                WHERE Id = 1;

                INSERT INTO MeditationSchedules (Id, Enabled, DaysOfWeekJson, TimeOfDay, DurationSeconds, LockMode, Soundscape)
                VALUES (2, 1, '["Tuesday"]', '08:00', 900, 1, 'Forest');

                INSERT INTO MeditationSchedules (Id, Enabled, DaysOfWeekJson, TimeOfDay, DurationSeconds, LockMode, Soundscape)
                VALUES (3, 1, '["Wednesday","Friday"]', '14:30', 1500, 2, 'Singing Bowls');
                """;
                cmd.ExecuteNonQuery();
            }

            // 3. Re-initialize BlockerDatabase on this existing file to trigger EnsureSingleMeditationSchedule migration
            var migratedDb = new BlockerDatabase(migrationDbPath);

            // 4. Verify directly in SQLite that Id = 1 contains the newest row (Row 3's values) and rows > 1 are deleted
            using (var conn = new SqliteConnection($"Data Source={migrationDbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*), Id, Enabled, TimeOfDay, DurationSeconds, LockMode, Soundscape FROM MeditationSchedules;";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                long count = reader.GetInt64(0);
                int id = reader.GetInt32(1);
                long enabled = reader.GetInt64(2);
                string time = reader.GetString(3);
                long duration = reader.GetInt64(4);
                int lockMode = reader.GetInt32(5);
                string soundscape = reader.GetString(6);

                Assert.Equal(1, count);
                Assert.Equal(1, id);
                Assert.Equal(1, enabled);
                Assert.Equal("14:30", time);
                Assert.Equal(1500, duration);
                Assert.Equal(2, lockMode);
                Assert.Equal("Singing Bowls", soundscape);
            }
        }
        finally
        {
            if (File.Exists(migrationDbPath))
            {
                try { File.Delete(migrationDbPath); } catch { }
            }
        }
    }

    [Fact]
    public void ProcessSuspender_RealChildProcess_SuspendAndResume()
    {
        // Spawn a real child process
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"Start-Sleep -Seconds 30\"",
            CreateNoWindow = true,
            UseShellExecute = false
        };

        using var child = Process.Start(psi);
        Assert.NotNull(child);

        try
        {
            int pid = child.Id;
            var suspender = new ProcessSuspender();

            // Suspend child process
            bool suspended = suspender.SuspendProcess(pid, "powershell.exe");
            Assert.True(suspended);
            Assert.True(suspender.IsSuspended(pid));
            Assert.Contains(pid, suspender.SuspendedPids);

            // Resume child process
            bool resumed = suspender.ResumeProcess(pid);
            Assert.True(resumed);
            Assert.False(suspender.IsSuspended(pid));
            Assert.DoesNotContain(pid, suspender.SuspendedPids);
        }
        finally
        {
            try
            {
                if (!child.HasExited)
                {
                    child.Kill();
                }
            }
            catch { }
        }
    }

    [Fact]
    public void ProcessSuspender_ProtectedProcesses_ImmunityGuaranteed()
    {
        var suspender = new ProcessSuspender();
        int currentPid = Environment.ProcessId;

        string[] protectedNames =
        {
            "audiodg",
            "taskmgr",
            "explorer",
            "dwm",
            "csrss",
            "smss",
            "wininit",
            "services",
            "lsass",
            "svchost",
            "snippingtool",
            "screenclippinghost",
            "Blocker.Service",
            "Sankalpa"
        };

        foreach (var name in protectedNames)
        {
            bool result = suspender.SuspendProcess(currentPid, name);
            Assert.False(result);
            Assert.False(suspender.IsSuspended(currentPid));
        }

        Assert.Empty(suspender.SuspendedPids);
    }

    [Fact]
    public void CommandHandler_ConfirmTimerReady_PromotesAndSuspends()
    {
        // 1. Start a Pending meditation session in DB
        var session = _db.StartMeditationSession(
            targetDurationSeconds: 1200,
            allowedApps: new List<string> { "notepad.exe" },
            lockMode: LockMode.Locked,
            soundscape: "Rain",
            passwordHash: null,
            status: "Pending");

        Assert.Equal("Pending", session.Status);

        // 2. Start a real child process that is NOT in allowed apps
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"Start-Sleep -Seconds 30\"",
            CreateNoWindow = true,
            UseShellExecute = false
        };

        using var child = Process.Start(psi);
        Assert.NotNull(child);

        try
        {
            // 3. Dispatch ConfirmTimerReady IPC command
            var reqObj = new ConfirmTimerReadyRequest
            {
                SessionId = session.Id,
                VisibleWindowPids = new List<int> { child.Id }
            };

            var request = new Request
            {
                Command = CommandNames.ConfirmTimerReady,
                Args = new Dictionary<string, string>
                {
                    ["data"] = JsonSerializer.Serialize(reqObj)
                }
            };

            var response = CommandHandler.Handle(request, _db, ClientIdentity.StandardUser());
            Assert.True(response.Ok);

            // 4. Verify session in DB was promoted to Active
            var activeSession = _db.GetActiveMeditationSession();
            Assert.NotNull(activeSession);
            Assert.Equal(session.Id, activeSession.Id);
            Assert.Equal("Active", activeSession.Status);

            // 5. Verify child process was suspended by ProcessSuspender.Default
            Assert.True(ProcessSuspender.Default.IsSuspended(child.Id));
        }
        finally
        {
            ProcessSuspender.Default.ResumeAll();
            try
            {
                if (!child.HasExited) child.Kill();
            }
            catch { }
        }
    }

    [Fact]
    public void FullStack_ScheduleDisable_PersistsViaIpc()
    {
        // 1. Save an enabled schedule
        var initialSchedule = new MeditationSchedule
        {
            Id = 1,
            Enabled = true,
            TimeOfDay = "16:00",
            DurationSeconds = 1800,
            LockMode = LockMode.None,
            Soundscape = "Library"
        };
        _db.SaveMeditationSchedule(initialSchedule);

        // 2. Client toggles switch to disabled and sends SAVEMEDITATIONSCHEDULE
        var disableDto = new MeditationScheduleDto
        {
            Id = 1,
            Enabled = false,
            DaysOfWeek = new List<string> { "Monday" },
            TimeOfDay = "16:00",
            DurationSeconds = 1800,
            AllowedApps = new List<string>(),
            LockMode = "None",
            Soundscape = "Library"
        };

        var request = new Request
        {
            Command = CommandNames.SaveMeditationSchedule,
            Args = new Dictionary<string, string>
            {
                ["data"] = JsonSerializer.Serialize(disableDto)
            }
        };

        var response = CommandHandler.Handle(request, _db, ClientIdentity.StandardUser());
        Assert.True(response.Ok);

        // 3. Query directly from database
        var saved = _db.GetMeditationSchedule();
        Assert.NotNull(saved);
        Assert.Equal(1, saved.Id);
        Assert.False(saved.Enabled);
    }

    [Fact]
    public void Startup_StateHydration_ReflectsDatabase()
    {
        // 1. Seed database with scheduled settings
        var sched = new MeditationSchedule
        {
            Id = 1,
            Enabled = true,
            DaysOfWeekJson = "[\"Monday\",\"Thursday\"]",
            TimeOfDay = "06:45",
            DurationSeconds = 2700,
            AllowedAppsJson = "[\"code.exe\",\"slack.exe\"]",
            LockMode = LockMode.Locked,
            Soundscape = "Forest",
            PasswordHash = "adminpass"
        };
        _db.SaveMeditationSchedule(sched);

        // 2. Query schedules via IPC (simulating frontend startup hydration)
        var request = new Request
        {
            Command = CommandNames.GetMeditationSchedules
        };

        var response = CommandHandler.Handle(request, _db, ClientIdentity.StandardUser());
        Assert.True(response.Ok);
        Assert.NotNull(response.Data);

        var list = JsonSerializer.Deserialize<List<MeditationScheduleDto>>(
            response.Data,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(list);
        Assert.Single(list);

        var dto = list[0];
        Assert.Equal(1, dto.Id);
        Assert.True(dto.Enabled);
        Assert.Equal("06:45", dto.TimeOfDay);
        Assert.Equal(2700, dto.DurationSeconds);
        Assert.Equal("Forest", dto.Soundscape);
        Assert.Equal("Locked", dto.LockMode);
        Assert.Contains("Monday", dto.DaysOfWeek);
        Assert.Contains("Thursday", dto.DaysOfWeek);
        Assert.Contains("code.exe", dto.AllowedApps);
        Assert.Contains("slack.exe", dto.AllowedApps);
    }

    [Fact]
    public void Handshake_PendingSession_FilteredFromActiveUntilPromoted()
    {
        // 1. Start a Pending session
        var pending = _db.StartMeditationSession(
            targetDurationSeconds: 600,
            allowedApps: new List<string>(),
            lockMode: LockMode.Locked,
            soundscape: "Mute",
            passwordHash: null,
            status: "Pending");

        Assert.Equal("Pending", pending.Status);

        // 2. Query active session - must be null because Pending is not yet Active
        var activeBefore = _db.GetActiveMeditationSession();
        Assert.Null(activeBefore);

        // 3. Promote session to Active
        bool promoted = _db.PromoteMeditationSessionToActive(pending.Id);
        Assert.True(promoted);

        // 4. Query active session - now returns the session
        var activeAfter = _db.GetActiveMeditationSession();
        Assert.NotNull(activeAfter);
        Assert.Equal(pending.Id, activeAfter.Id);
        Assert.Equal("Active", activeAfter.Status);

        // 5. End session cleanly
        _db.EndMeditationSession(pending.Id, completed: true);
        var activeEnded = _db.GetActiveMeditationSession();
        Assert.Null(activeEnded);
    }

    [Fact]
    public void RealReleaseDatabase_IfPresent_IsCleanedAndMigrated()
    {
        var cur = new DirectoryInfo(AppContext.BaseDirectory);
        string? repoRoot = null;
        while (cur != null)
        {
            if (File.Exists(Path.Combine(cur.FullName, "Blocker.sln")))
            {
                repoRoot = cur.FullName;
                break;
            }
            cur = cur.Parent;
        }

        if (repoRoot != null)
        {
            string releaseDb = Path.Combine(repoRoot, "dist", "Sankalpa-Release", "data", "blocker.db");
            if (File.Exists(releaseDb))
            {
                var db = new BlockerDatabase(releaseDb);
                var schedules = db.GetMeditationSchedules();
                Assert.True(schedules.Count <= 1);
                if (schedules.Count == 1)
                {
                    Assert.Equal(1, schedules[0].Id);
                }
            }
        }
    }
}
