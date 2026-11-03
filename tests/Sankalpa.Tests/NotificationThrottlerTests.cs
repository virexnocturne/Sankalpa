using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class NotificationThrottlerTests
{
    [Fact]
    public void ShouldNotify_FirstTime_ReturnsTrue()
    {
        var throttler = new NotificationThrottler(TimeSpan.FromSeconds(5));
        var now = DateTime.UtcNow;

        bool result = throttler.ShouldNotify("notepad.exe", now);
        Assert.True(result);
    }

    [Fact]
    public void ShouldNotify_Within5Seconds_ReturnsFalse()
    {
        var throttler = new NotificationThrottler(TimeSpan.FromSeconds(5));
        var now = DateTime.UtcNow;

        Assert.True(throttler.ShouldNotify("notepad.exe", now));
        Assert.False(throttler.ShouldNotify("notepad.exe", now.AddSeconds(2)));
        Assert.False(throttler.ShouldNotify("notepad.exe", now.AddSeconds(4.9)));
    }

    [Fact]
    public void ShouldNotify_DifferentApps_NotThrottledByEachOther()
    {
        var throttler = new NotificationThrottler(TimeSpan.FromSeconds(5));
        var now = DateTime.UtcNow;

        Assert.True(throttler.ShouldNotify("notepad.exe", now));
        Assert.True(throttler.ShouldNotify("discord.exe", now.AddSeconds(1)));
        Assert.False(throttler.ShouldNotify("notepad.exe", now.AddSeconds(2)));
        Assert.False(throttler.ShouldNotify("discord.exe", now.AddSeconds(3)));
    }

    [Fact]
    public void ShouldNotify_After5Seconds_ReturnsTrue()
    {
        var throttler = new NotificationThrottler(TimeSpan.FromSeconds(5));
        var now = DateTime.UtcNow;

        Assert.True(throttler.ShouldNotify("notepad.exe", now));
        Assert.False(throttler.ShouldNotify("notepad.exe", now.AddSeconds(3)));
        Assert.True(throttler.ShouldNotify("notepad.exe", now.AddSeconds(5.1)));
    }

    [Fact]
    public void ShouldNotify_CaseInsensitive()
    {
        var throttler = new NotificationThrottler(TimeSpan.FromSeconds(5));
        var now = DateTime.UtcNow;

        Assert.True(throttler.ShouldNotify("Notepad.EXE", now));
        Assert.False(throttler.ShouldNotify("notepad.exe", now.AddSeconds(1)));
    }

    [Fact]
    public void ShouldNotify_DifferentKinds_NotThrottledByEachOther()
    {
        var throttler = new NotificationThrottler(TimeSpan.FromSeconds(5));
        var now = DateTime.UtcNow;

        Assert.True(throttler.ShouldNotify("msedge.exe", now, "BrowserGrace"));
        Assert.True(throttler.ShouldNotify("msedge.exe", now.AddSeconds(1), "BrowserKill"));
        Assert.False(throttler.ShouldNotify("msedge.exe", now.AddSeconds(2), "BrowserGrace"));
        Assert.False(throttler.ShouldNotify("msedge.exe", now.AddSeconds(2), "BrowserKill"));
    }
}
