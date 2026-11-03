using System.Diagnostics;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class ProcessMatcherTests
{
    [Theory]
    [InlineData("notepad", "NOTEPAD")]
    [InlineData("Notepad", "notepad")]
    [InlineData("DISCORD", "discord")]
    [InlineData("spotify", "SPOTIFY")]
    [InlineData("CHROME", "chrome")]
    public void IsBlocked_ShouldBeCaseInsensitive(string processName, string blockedEntry)
    {
        var blockedList = new[] { blockedEntry };
        bool result = ProcessMatcher.IsBlocked(processName, blockedList);
        Assert.True(result);
    }

    [Theory]
    [InlineData("discord", "discord.exe")]
    [InlineData("discord.exe", "discord")]
    [InlineData("discord.exe", "discord.exe")]
    [InlineData("discord", "discord")]
    [InlineData("steam.exe", "steam")]
    [InlineData("steam", "steam.exe")]
    public void IsBlocked_ShouldTreatNamesWithAndWithoutExeEqually(string processName, string blockedEntry)
    {
        var blockedList = new[] { blockedEntry };
        bool result = ProcessMatcher.IsBlocked(processName, blockedList);
        Assert.True(result);
    }

    [Fact]
    public void IsBlocked_ShouldIgnoreEmptyAndWhitespaceEntriesInBlockedList()
    {
        var blockedList = new[] { "", "   ", "\t", "\r\n", null, "notepad.exe" };

        Assert.True(ProcessMatcher.IsBlocked("notepad", blockedList));
        Assert.False(ProcessMatcher.IsBlocked("calc", blockedList));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsBlocked_ShouldReturnFalseForNullOrWhitespaceProcessName(string? processName)
    {
        var blockedList = new[] { "notepad.exe", "calc.exe" };
        bool result = ProcessMatcher.IsBlocked(processName, blockedList);
        Assert.False(result);
    }

    [Fact]
    public void IsBlocked_ShouldReturnFalseWhenBlockedListIsNull()
    {
        bool result = ProcessMatcher.IsBlocked("notepad", null);
        Assert.False(result);
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("explorer.exe")]
    [InlineData("csrss")]
    [InlineData("csrss.exe")]
    [InlineData("winlogon")]
    [InlineData("winlogon.exe")]
    [InlineData("wininit")]
    [InlineData("wininit.exe")]
    [InlineData("services")]
    [InlineData("services.exe")]
    [InlineData("lsass")]
    [InlineData("lsass.exe")]
    [InlineData("svchost")]
    [InlineData("svchost.exe")]
    [InlineData("system")]
    [InlineData("system.exe")]
    [InlineData("smss")]
    [InlineData("smss.exe")]
    [InlineData("dwm")]
    [InlineData("dwm.exe")]
    [InlineData("taskmgr")]
    [InlineData("taskmgr.exe")]
    [InlineData("TASKMGR")]
    [InlineData("EXPLORER")]
    [InlineData("blocker")]
    [InlineData("blocker.exe")]
    [InlineData("blocker.service")]
    [InlineData("Blocker.Service.exe")]
    public void IsBlocked_ShouldNeverBlockProtectedProcessesEvenIfInBlockedList(string protectedProcessName)
    {
        var blockedList = new[]
        {
            "explorer",
            "explorer.exe",
            "csrss",
            "csrss.exe",
            "winlogon",
            "winlogon.exe",
            "wininit",
            "wininit.exe",
            "services",
            "services.exe",
            "lsass",
            "lsass.exe",
            "svchost",
            "svchost.exe",
            "system",
            "system.exe",
            "smss",
            "smss.exe",
            "dwm",
            "dwm.exe",
            "taskmgr",
            "taskmgr.exe",
            "blocker",
            "blocker.service",
            "blocker.service.exe"
        };

        bool result = ProcessMatcher.IsBlocked(protectedProcessName, blockedList);
        Assert.False(result);
    }

    [Fact]
    public void IsBlocked_ShouldNeverBlockCurrentProcessEvenIfInBlockedList()
    {
        string currentProcess = Process.GetCurrentProcess().ProcessName;
        var blockedList = new[] { currentProcess, currentProcess + ".exe" };

        bool result = ProcessMatcher.IsBlocked(currentProcess, blockedList);
        Assert.False(result);

        bool resultWithExe = ProcessMatcher.IsBlocked(currentProcess + ".exe", blockedList);
        Assert.False(resultWithExe);
    }

    [Fact]
    public void IsBlocked_ShouldReturnFalseWhenProcessIsNotInBlockedList()
    {
        var blockedList = new[] { "notepad.exe", "discord" };
        bool result = ProcessMatcher.IsBlocked("calc", blockedList);
        Assert.False(result);
    }

    // --- Phase 1.5 Tests ---

    [Theory]
    [InlineData("mytool", "notepad.exe", "notepad")]
    [InlineData("renamed_app", "NOTEPAD.EXE", "notepad.exe")]
    [InlineData("custom_runner", "discord.exe", "discord")]
    [InlineData("tool", "NOTEPAD.EXE.MUI", "notepad.exe")]
    [InlineData("utility", "spotify", "spotify.exe")]
    public void IsBlocked_ShouldBlockRenamedProcessBasedOnOriginalFilename(string processName, string originalFileName, string blockedEntry)
    {
        var blockedList = new[] { blockedEntry };
        bool result = ProcessMatcher.IsBlocked(processName, blockedList, originalFileName);
        Assert.True(result);
    }

    [Fact]
    public void IsBlocked_ShouldReturnFalseWhenNeitherProcessNameNorOriginalFilenameMatches()
    {
        var blockedList = new[] { "notepad.exe", "discord.exe" };
        bool result = ProcessMatcher.IsBlocked("mytool", blockedList, "calc.exe");
        Assert.False(result);
    }

    [Theory]
    [InlineData("explorer", "notepad.exe")]
    [InlineData("csrss.exe", "discord.exe")]
    [InlineData("winlogon", "notepad")]
    [InlineData("dwm", "spotify.exe")]
    [InlineData("taskmgr.exe", "chrome.exe")]
    [InlineData("blocker.service", "notepad.exe")]
    public void IsBlocked_ShouldNeverBlockIfProcessNameIsProtected_EvenIfOriginalFilenameMatchesBlocklist(string protectedProcessName, string blockedOriginalFileName)
    {
        var blockedList = new[] { "notepad.exe", "discord.exe", "spotify.exe", "chrome.exe" };
        bool result = ProcessMatcher.IsBlocked(protectedProcessName, blockedList, blockedOriginalFileName);
        Assert.False(result);
    }

    [Theory]
    [InlineData("mytool", "explorer.exe")]
    [InlineData("notepad", "csrss")]
    [InlineData("notepad.exe", "winlogon.exe")]
    [InlineData("discord", "dwm.exe")]
    [InlineData("spotify", "taskmgr")]
    [InlineData("app.exe", "blocker.service")]
    public void IsBlocked_ShouldNeverBlockIfOriginalFilenameIsProtected_EvenIfProcessNameMatchesBlocklist(string processName, string protectedOriginalFileName)
    {
        var blockedList = new[] { "mytool", "notepad", "notepad.exe", "discord", "spotify", "app.exe" };
        bool result = ProcessMatcher.IsBlocked(processName, blockedList, protectedOriginalFileName);
        Assert.False(result);
    }

    [Fact]
    public void IsBlocked_ShouldNeverBlockIfOriginalFilenameIsCurrentProcess()
    {
        string currentProcess = Process.GetCurrentProcess().ProcessName;
        var blockedList = new[] { "mytool" };

        bool result = ProcessMatcher.IsBlocked("mytool", blockedList, currentProcess);
        Assert.False(result);

        bool resultWithExe = ProcessMatcher.IsBlocked("mytool", blockedList, currentProcess + ".exe");
        Assert.False(resultWithExe);
    }
}
