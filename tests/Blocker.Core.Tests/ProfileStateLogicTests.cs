using System;
using System.Collections.Generic;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class ProfileStateLogicTests
{
    [Fact]
    public void IsProfileActive_WhenManualSessionRunning_ReturnsTrueManual()
    {
        var profile = new Profile { Id = 1, Name = "Study", Enabled = false };
        var session = new BlockSession { ProfileId = 1 };

        bool active = ProfileStateLogic.IsProfileActive(profile, session, DateTime.Now, out string reason);
        Assert.True(active);
        Assert.Equal("Manual", reason);
    }

    [Fact]
    public void IsProfileActive_WhenDisabledAndNoSession_ReturnsFalse()
    {
        var profile = new Profile { Id = 1, Name = "Study", Enabled = false };
        bool active = ProfileStateLogic.IsProfileActive(profile, null, DateTime.Now, out string reason);
        Assert.False(active);
        Assert.Equal("None", reason);
    }

    [Fact]
    public void IsProfileActive_WhenEnabledWithNoSchedule_ReturnsTrueAlwaysOn()
    {
        var profile = new Profile { Id = 1, Name = "Study", Enabled = true, Schedule = new() };
        bool active = ProfileStateLogic.IsProfileActive(profile, null, DateTime.Now, out string reason);
        Assert.True(active);
        Assert.Equal("AlwaysOn", reason);
    }

    [Fact]
    public void IsProfileActive_WhenEnabledWithSchedule_EvaluatesSchedule()
    {
        var profile = new Profile
        {
            Id = 1,
            Name = "Work",
            Enabled = true,
            Schedule = new()
            {
                new() { Days = DaysOfWeek.Mon, StartMinute = 540, EndMinute = 1020 } // Mon 09:00 - 17:00
            }
        };

        DateTime monDuring = new DateTime(2026, 11, 2, 11, 0, 0); // Monday
        bool active1 = ProfileStateLogic.IsProfileActive(profile, null, monDuring, out string reason1);
        Assert.True(active1);
        Assert.Equal("Schedule", reason1);

        DateTime tueDuring = new DateTime(2026, 11, 3, 11, 0, 0); // Tuesday
        bool active2 = ProfileStateLogic.IsProfileActive(profile, null, tueDuring, out string reason2);
        Assert.False(active2);
        Assert.Equal("None", reason2);
    }

    [Fact]
    public void ComputeActiveUnion_CombinesAllActiveProfiles()
    {
        var p1 = new Profile
        {
            Id = 1,
            Name = "Work",
            Enabled = true,
            BlockedProcesses = new() { "discord.exe", "slack.exe" },
            BlockedDomains = new() { "reddit.com" }
        };

        var p2 = new Profile
        {
            Id = 2,
            Name = "Gaming",
            Enabled = true,
            BlockedProcesses = new() { "steam.exe", "discord.exe" },
            BlockedDomains = new() { "twitch.tv", "reddit.com" }
        };

        var p3 = new Profile
        {
            Id = 3,
            Name = "Off",
            Enabled = false,
            BlockedProcesses = new() { "notepad.exe" },
            BlockedDomains = new() { "example.com" }
        };

        var (apps, domains) = ProfileStateLogic.ComputeActiveUnion(new[] { p1, p2, p3 }, null, DateTime.Now);

        Assert.Equal(3, apps.Count);
        Assert.Contains("discord.exe", apps, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("slack.exe", apps, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("steam.exe", apps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("notepad.exe", apps, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, domains.Count);
        Assert.Contains("reddit.com", domains, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("twitch.tv", domains, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", domains, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsProfileLockedNow_OnlyTrueWhenActiveAndLockModeNotNone()
    {
        var p1 = new Profile { Id = 1, Enabled = true, LockMode = LockMode.None };
        Assert.False(ProfileStateLogic.IsProfileLockedNow(p1, null, DateTime.Now));

        var p2 = new Profile { Id = 2, Enabled = true, LockMode = LockMode.Locked };
        Assert.True(ProfileStateLogic.IsProfileLockedNow(p2, null, DateTime.Now));

        var p3 = new Profile { Id = 3, Enabled = false, LockMode = LockMode.Locked };
        Assert.False(ProfileStateLogic.IsProfileLockedNow(p3, null, DateTime.Now));
    }
}
