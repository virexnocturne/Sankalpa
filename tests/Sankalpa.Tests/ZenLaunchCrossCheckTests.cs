using System;
using System.Collections.Generic;
using System.IO;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Sankalpa.Tests;

public class ZenLaunchCrossCheckTests
{
    [Fact]
    public void Constants_AdhereToSingleSourceContract()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), ZenLaunchConstants.MaxDiscoveryTimeout);
        Assert.Equal(8000, ZenLaunchConstants.MaxDiscoveryTimeoutMs);

        Assert.Equal(TimeSpan.FromSeconds(2), ZenLaunchConstants.WarmSwitchTarget);
        Assert.Equal(2000, ZenLaunchConstants.WarmSwitchTargetMs);

        Assert.Equal(TimeSpan.FromSeconds(5), ZenLaunchConstants.ColdLaunchTarget);
        Assert.Equal(5000, ZenLaunchConstants.ColdLaunchTargetMs);

        Assert.Equal(TimeSpan.FromSeconds(4), ZenLaunchConstants.LaunchGracePeriod);
        Assert.Equal(4000, ZenLaunchConstants.LaunchGracePeriodMs);

        Assert.Equal(100, ZenLaunchConstants.MinWindowDimension);
        Assert.Equal(100, ZenLaunchConstants.PollIntervalMs);
        Assert.Equal(300, ZenLaunchConstants.SettleDelayMs);
        Assert.Equal(3, ZenLaunchConstants.MaxUnauthorizedBreaches);
    }

    [Theory]
    [InlineData("msedge", "msedge.exe", "msedge")]
    [InlineData("msedge", "msedge.exe", "msedge.exe")]
    [InlineData("MSEDGE", "MSEDGE.EXE", "msedge")]
    [InlineData("notepad", "notepad.exe", "notepad")]
    [InlineData("code", "code.exe", "code")]
    [InlineData("slack", "slack.exe", "Slack")]
    public void SingleAllowedAppRule_ExactParity_AllowedApps(string procName, string exeName, string allowed)
    {
        var allowedApps = new List<string> { allowed };

        bool matcherResult = ZenLaunchMatcher.IsAppAllowed(procName, exeName, allowedApps, null, null);
        bool workerResult = Worker.IsAppAllowed(procName, exeName, allowedApps, null, null);

        Assert.True(matcherResult);
        Assert.Equal(workerResult, matcherResult);
    }

    [Theory]
    [InlineData("cmd", "cmd.exe")]
    [InlineData("powershell", "powershell.exe")]
    [InlineData("chrome", "chrome.exe")]
    [InlineData("discord", "discord.exe")]
    public void SingleAllowedAppRule_ExactParity_DisallowedApps(string procName, string exeName)
    {
        var allowedApps = new List<string> { "notepad", "msedge" };

        bool matcherResult = ZenLaunchMatcher.IsAppAllowed(procName, exeName, allowedApps, null, null);
        bool workerResult = Worker.IsAppAllowed(procName, exeName, allowedApps, null, null);

        Assert.False(matcherResult);
        Assert.Equal(workerResult, matcherResult);
    }

    [Fact]
    public void SingleAllowedAppRule_ExactParity_EmptyOrNullList()
    {
        var emptyList = new List<string>();

        Assert.Equal(
            Worker.IsAppAllowed("msedge", "msedge.exe", emptyList, null, null),
            ZenLaunchMatcher.IsAppAllowed("msedge", "msedge.exe", emptyList, null, null));

        Assert.Equal(
            Worker.IsAppAllowed("msedge", "msedge.exe", null!, null, null),
            ZenLaunchMatcher.IsAppAllowed("msedge", "msedge.exe", null, null, null));
    }

    [Fact]
    public void SingleAllowedAppRule_ExactParity_SnippingHostToolsAlwaysAllowed()
    {
        var allowedApps = new List<string> { "notepad" };

        foreach (var snip in new[] { "snippingtool", "screenclippinghost" })
        {
            bool matcherResult = ZenLaunchMatcher.IsAppAllowed(snip, snip + ".exe", allowedApps, null, null);
            bool workerResult = Worker.IsAppAllowed(snip, snip + ".exe", allowedApps, null, null);

            Assert.True(matcherResult);
            Assert.Equal(workerResult, matcherResult);
        }
    }

    [Fact]
    public void SingleAllowedAppRule_ExactParity_PinnedRoots()
    {
        string rootDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string validPath = Path.Combine(rootDir, "msedge.exe");
        string roguePath = @"C:\Malicious\msedge.exe";

        var allowedApps = new List<string> { "msedge" };
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["msedge"] = rootDir
        };

        // 1. Valid image path inside pinned root
        bool matcherValid = ZenLaunchMatcher.IsAppAllowed("msedge", "msedge.exe", allowedApps, validPath, roots);
        bool workerValid = Worker.IsAppAllowed("msedge", "msedge.exe", allowedApps, validPath, roots);
        Assert.True(matcherValid);
        Assert.Equal(workerValid, matcherValid);

        // 2. Rogue image path outside pinned root
        bool matcherRogue = ZenLaunchMatcher.IsAppAllowed("msedge", "msedge.exe", allowedApps, roguePath, roots);
        bool workerRogue = Worker.IsAppAllowed("msedge", "msedge.exe", allowedApps, roguePath, roots);
        Assert.False(matcherRogue);
        Assert.Equal(workerRogue, matcherRogue);
    }
}
