using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

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
}
