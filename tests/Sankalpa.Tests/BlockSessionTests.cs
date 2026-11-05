using System.Runtime.Versioning;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

[SupportedOSPlatform("windows")]
public class BlockSessionTests
{
    [Fact]
    public void IsActive_ShouldReturnTrue_WhenCurrentTimeIsWithinSessionWindow()
    {
        var start = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);
        var session = new BlockSession
        {
            Id = 1,
            ProfileId = 1,
            StartUtc = start,
            DurationSeconds = 120, // 2 minutes
            Locked = true
        };

        // At exact start
        Assert.True(BlockSessionLogic.IsActive(session, start));

        // Halfway through
        Assert.True(BlockSessionLogic.IsActive(session, start.AddSeconds(60)));

        // 1 second before end
        Assert.True(BlockSessionLogic.IsActive(session, start.AddSeconds(119)));
    }

    [Fact]
    public void IsActive_ShouldReturnFalse_WhenTimeIsBeforeStartOrAfterEnd()
    {
        var start = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);
        var session = new BlockSession
        {
            Id = 1,
            ProfileId = 1,
            StartUtc = start,
            DurationSeconds = 120,
            Locked = true
        };

        // Before start
        Assert.False(BlockSessionLogic.IsActive(session, start.AddSeconds(-1)));

        // At exact expiration
        Assert.False(BlockSessionLogic.IsActive(session, start.AddSeconds(120)));

        // After expiration
        Assert.False(BlockSessionLogic.IsActive(session, start.AddSeconds(121)));
    }

    [Fact]
    public void IsActive_ShouldReturnFalse_WhenSessionIsMarkedEnded()
    {
        var start = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);
        var session = new BlockSession
        {
            Id = 1,
            ProfileId = 1,
            StartUtc = start,
            DurationSeconds = 120,
            Locked = true,
            EndedUtc = start.AddSeconds(30)
        };

        Assert.False(BlockSessionLogic.IsActive(session, start.AddSeconds(40)));
    }

    [Fact]
    public void IsActive_ShouldReturnFalse_WhenSessionIsNullOrDefault()
    {
        Assert.False(BlockSessionLogic.IsActive(null, DateTime.UtcNow));

        var zeroDurationSession = new BlockSession
        {
            StartUtc = DateTime.UtcNow,
            DurationSeconds = 0
        };
        Assert.False(BlockSessionLogic.IsActive(zeroDurationSession, DateTime.UtcNow));
    }

    [Fact]
    public void RestoreAfterRestart_ShouldCalculateCorrectRemainingTime()
    {
        var start = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);
        var session = new BlockSession
        {
            Id = 1,
            ProfileId = 1,
            StartUtc = start,
            DurationSeconds = 120, // 2 minutes
            Locked = true
        };

        // Service restarted 45 seconds after session started
        var restartTime = start.AddSeconds(45);

        Assert.True(BlockSessionLogic.IsActive(session, restartTime));
        TimeSpan remaining = BlockSessionLogic.GetRemainingTime(session, restartTime);

        Assert.Equal(TimeSpan.FromSeconds(75), remaining);
    }

    [Fact]
    public void RestoreAfterRestart_ShouldBeExpired_WhenDowntimeExceedsDuration()
    {
        var start = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);
        var session = new BlockSession
        {
            Id = 1,
            ProfileId = 1,
            StartUtc = start,
            DurationSeconds = 120,
            Locked = true
        };

        // Service restarted 200 seconds after session started (downtime exceeded duration)
        var restartTime = start.AddSeconds(200);

        Assert.False(BlockSessionLogic.IsActive(session, restartTime));
        TimeSpan remaining = BlockSessionLogic.GetRemainingTime(session, restartTime);

        Assert.Equal(TimeSpan.Zero, remaining);
    }

    [Fact]
    public void LockedStopRefusal_ShouldRefuseStop_WhenSessionIsLockedAndActive()
    {
        var start = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);
        var session = new BlockSession
        {
            Id = 1,
            ProfileId = 1,
            StartUtc = start,
            DurationSeconds = 120,
            Locked = true
        };

        var now = start.AddSeconds(30);

        var (canStop, remaining, reason) = BlockSessionLogic.EvaluateStopRequest(session, now, emergencyUnlockPresent: false);

        Assert.False(canStop);
        Assert.Equal(TimeSpan.FromSeconds(90), remaining);
        Assert.Contains("locked", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("01:30", reason);
    }

    [Fact]
    public void LockedStopRefusal_ShouldAllowStop_WhenSessionIsUnlocked()
    {
        var start = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);
        var session = new BlockSession
        {
            Id = 1,
            ProfileId = 1,
            StartUtc = start,
            DurationSeconds = 120,
            Locked = false // Unlocked
        };

        var now = start.AddSeconds(30);

        var (canStop, remaining, reason) = BlockSessionLogic.EvaluateStopRequest(session, now, emergencyUnlockPresent: false);

        Assert.True(canStop);
        Assert.Equal(TimeSpan.FromSeconds(90), remaining);
    }

    [Fact]
    public void LockedStopRefusal_ShouldAllowStop_WhenEmergencyUnlockIsPresent()
    {
        var start = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);
        var session = new BlockSession
        {
            Id = 1,
            ProfileId = 1,
            StartUtc = start,
            DurationSeconds = 120,
            Locked = true // Locked session
        };

        var now = start.AddSeconds(30);

        // DEV-ONLY: Emergency unlock present
        var (canStop, remaining, reason) = BlockSessionLogic.EvaluateStopRequest(session, now, emergencyUnlockPresent: true);

        Assert.True(canStop);
        Assert.Contains("Emergency", reason, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void EvaluateStopRequest_ShouldRefuseStop_WhenClockManipulatedBackwardsAndSessionLocked()
    {
        var start = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var session = new BlockSession
        {
            Id = 1,
            ProfileId = 1,
            StartUtc = start,
            DurationSeconds = 259200, // 3 days
            Locked = true
        };

        // System clock manipulated 1 hour backwards into the past
        var tamperedNow = start.AddHours(-1);

        var (canStop, remaining, reason) = BlockSessionLogic.EvaluateStopRequest(session, tamperedNow, emergencyUnlockPresent: false);

        Assert.False(canStop);
        Assert.Equal(TimeSpan.FromSeconds(259200), remaining);
        Assert.Contains("locked", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3d", reason);
    }

    [Fact]
    public void GetRemainingTime_ShouldPreserveFullDuration_WhenClockMovedBackwards()
    {
        var start = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var session = new BlockSession
        {
            Id = 1,
            ProfileId = 1,
            StartUtc = start,
            DurationSeconds = 7200, // 2 hours
            Locked = true
        };

        var tamperedNow = start.AddMinutes(-30);
        var remaining = BlockSessionLogic.GetRemainingTime(session, tamperedNow);

        Assert.Equal(TimeSpan.FromSeconds(7200), remaining);
    }

    [Fact]
    public void ThreeDaySession_CountdownFormattingAndRemainingTime_Accurate()
    {
        var start = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var session = new BlockSession
        {
            Id = 1,
            ProfileId = 1,
            StartUtc = start,
            DurationSeconds = 259200, // 3 days (3 * 86400)
            Locked = true
        };

        // Exact start
        Assert.Equal("3d", UiLogic.FormatCountdown(BlockSessionLogic.GetRemainingTime(session, start)));

        // 1 hour in (2 days 23 hours remaining)
        var oneHourIn = start.AddHours(1);
        Assert.Equal("2d 23h", UiLogic.FormatCountdown(BlockSessionLogic.GetRemainingTime(session, oneHourIn)));

        // 2 days in (24 hours remaining = 1 day)
        var twoDaysIn = start.AddDays(2);
        Assert.Equal("1d", UiLogic.FormatCountdown(BlockSessionLogic.GetRemainingTime(session, twoDaysIn)));

        // 2 days 23 hours in (1 hour remaining)
        var almostDone = start.AddDays(2).AddHours(23);
        Assert.Equal("01:00:00", UiLogic.FormatCountdown(BlockSessionLogic.GetRemainingTime(session, almostDone)));
    }

    [Fact]
    public void BlockerDatabase_CacheStaleness_RefreshesAcrossInstances()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), $"blocker_cache_test_{Guid.NewGuid():N}.db");
        try
        {
            var db1 = new Blocker.Service.BlockerDatabase(tempDb);
            var db2 = new Blocker.Service.BlockerDatabase(tempDb);

            // Initially no active sessions in db1 cache
            Assert.Empty(db1.GetActiveSessions());

            // db2 starts a 3-day session in SQLite
            var session = db2.StartSession(1, 259200, locked: true);

            // Wait for cache TTL (500ms)
            Thread.Sleep(600);

            // db1 must see the new session from db2 without manual InvalidateCache call
            var activeFromDb1 = db1.GetActiveSessions();
            Assert.Single(activeFromDb1);
            Assert.Equal(session.Id, activeFromDb1[0].Id);
            Assert.Equal(259200, activeFromDb1[0].DurationSeconds);

            // db2 updates StartUtc
            var updatedStart = DateTime.UtcNow.AddMinutes(-5);
            db2.UpdateSessionStartUtc(session.Id, updatedStart);

            Thread.Sleep(600);
            var refreshedDb1 = db1.GetActiveSessions();
            Assert.Single(refreshedDb1);
            Assert.Equal(updatedStart.ToString("yyyy-MM-dd HH:mm:ss"), refreshedDb1[0].StartUtc.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    [Fact]
    public void BlockerDatabase_GetActiveSessions_ReturnsSession_WhenClockMovedBackwards()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), $"blocker_clock_test_{Guid.NewGuid():N}.db");
        try
        {
            var db = new Blocker.Service.BlockerDatabase(tempDb);
            var session = db.StartSession(1, 259200, locked: true);

            // Simulate clock moved backwards by setting StartUtc 10 minutes into future
            var futureStart = DateTime.UtcNow.AddMinutes(10);
            db.UpdateSessionStartUtc(session.Id, futureStart);

            // GetActiveSessions must still return the session and not drop it
            var active = db.GetActiveSessions(includeUnendedExpired: false);
            Assert.Single(active);
            Assert.Equal(session.Id, active[0].Id);
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }
}
