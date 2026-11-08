using System;
using System.Collections.Generic;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class AppPickerLogicTests
{
    [Theory]
    [InlineData("discord", "discord.exe")]
    [InlineData("Discord.exe", "Discord.exe")]
    [InlineData("C:\\Program Files\\Steam\\steam.exe", "steam.exe")]
    [InlineData("  notepad  ", "notepad.exe")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeExeName_ReturnsNormalizedExe(string input, string expected)
    {
        Assert.Equal(expected, AppPickerLogic.NormalizeExeName(input));
    }

    [Theory]
    [InlineData("Discord.exe", "Discord")]
    [InlineData("steam", "steam")]
    [InlineData("C:\\Tools\\mytool.exe", "mytool")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void GetDisplayName_ReturnsNameWithoutExe(string input, string expected)
    {
        Assert.Equal(expected, AppPickerLogic.GetDisplayName(input));
    }

    [Fact]
    public void IsSystemProcess_ProtectedProcess_ReturnsTrue()
    {
        Assert.True(AppPickerLogic.IsSystemProcess(null, "svchost", true));
        Assert.True(AppPickerLogic.IsSystemProcess(null, "csrss", false));
    }

    [Fact]
    public void IsSystemProcess_WindowsDirectoryPath_ReturnsTrue()
    {
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string path = System.IO.Path.Combine(winDir, "System32", "conhost.exe");
        Assert.True(AppPickerLogic.IsSystemProcess(path, "conhost", true));
    }

    [Fact]
    public void IsSystemProcess_UserAppWithWindow_ReturnsFalse()
    {
        Assert.False(AppPickerLogic.IsSystemProcess("C:\\Apps\\Game.exe", "Game", true));
    }

    [Fact]
    public void IsSystemProcess_NoWindowAndNoPath_ReturnsTrue()
    {
        Assert.True(AppPickerLogic.IsSystemProcess(null, "mystery_proc", false));
    }

    [Fact]
    public void FilterAndDeduplicate_FiltersOutSystem_WhenShowAllFalse()
    {
        var items = new List<AppPickerItem>
        {
            new() { DisplayName = "Discord", ExeName = "Discord.exe", Category = AppPickerLogic.CatPopular, IsSystemProcess = false },
            new() { DisplayName = "svchost", ExeName = "svchost.exe", Category = AppPickerLogic.CatSystem, IsSystemProcess = true }
        };

        var result = AppPickerLogic.FilterAndDeduplicate(items, null, showAllProcesses: false);

        Assert.Single(result);
        Assert.Equal("Discord.exe", result[0].ExeName);
    }

    [Fact]
    public void FilterAndDeduplicate_IncludesSystem_WhenShowAllTrue()
    {
        var items = new List<AppPickerItem>
        {
            new() { DisplayName = "Discord", ExeName = "Discord.exe", Category = AppPickerLogic.CatPopular, IsSystemProcess = false },
            new() { DisplayName = "svchost", ExeName = "svchost.exe", Category = AppPickerLogic.CatSystem, IsSystemProcess = true }
        };

        var result = AppPickerLogic.FilterAndDeduplicate(items, null, showAllProcesses: true);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FilterAndDeduplicate_FiltersBySearchText()
    {
        var items = new List<AppPickerItem>
        {
            new() { DisplayName = "Discord", ExeName = "Discord.exe", Category = AppPickerLogic.CatPopular },
            new() { DisplayName = "Steam", ExeName = "steam.exe", Category = AppPickerLogic.CatPopular },
            new() { DisplayName = "VLC Media Player", ExeName = "vlc.exe", Category = AppPickerLogic.CatInstalled }
        };

        var result = AppPickerLogic.FilterAndDeduplicate(items, "vlc", showAllProcesses: false);

        Assert.Single(result);
        Assert.Equal("vlc.exe", result[0].ExeName);

        var result2 = AppPickerLogic.FilterAndDeduplicate(items, "cord", showAllProcesses: false);
        Assert.Single(result2);
        Assert.Equal("Discord.exe", result2[0].ExeName);
    }

    [Fact]
    public void FilterAndDeduplicate_DeduplicatesByExe_PrioritizingCategory()
    {
        var items = new List<AppPickerItem>
        {
            new() { DisplayName = "Steam Client", ExeName = "steam.exe", Category = AppPickerLogic.CatRunning },
            new() { DisplayName = "Steam", ExeName = "steam.exe", Category = AppPickerLogic.CatPopular },
            new() { DisplayName = "Steam Shortcut", ExeName = "STEAM.EXE", Category = AppPickerLogic.CatInstalled }
        };

        var result = AppPickerLogic.FilterAndDeduplicate(items, null, showAllProcesses: false);

        Assert.Single(result);
        Assert.Equal("Steam", result[0].DisplayName);
        Assert.Equal(AppPickerLogic.CatPopular, result[0].Category);
    }

    [Fact]
    public void BuildFlatPickerList_CreatesHeadersAndAppRows_InOrder()
    {
        var raw = new List<AppPickerItem>
        {
            new() { DisplayName = "Discord", ExeName = "discord.exe", Category = AppPickerLogic.CatPopular },
            new() { DisplayName = "Chrome", ExeName = "chrome.exe", Category = AppPickerLogic.CatRunning },
            new() { DisplayName = "VLC", ExeName = "vlc.exe", Category = AppPickerLogic.CatInstalled }
        };

        var flat = AppPickerLogic.BuildFlatPickerList(raw, alreadyBlockedExes: null, searchText: null, showAllProcesses: false);

        // Expect: Header (Popular), Discord, Header (Running), Chrome, Header (Installed), VLC
        Assert.Equal(6, flat.Count);
        Assert.True(flat[0].IsHeader);
        Assert.Equal(AppPickerLogic.CatPopular, flat[0].HeaderTitle);
        Assert.False(flat[1].IsHeader);
        Assert.Equal("Discord", flat[1].DisplayName);

        Assert.True(flat[2].IsHeader);
        Assert.Equal(AppPickerLogic.CatRunning, flat[2].HeaderTitle);
        Assert.False(flat[3].IsHeader);
        Assert.Equal("Chrome", flat[3].DisplayName);

        Assert.True(flat[4].IsHeader);
        Assert.Equal(AppPickerLogic.CatInstalled, flat[4].HeaderTitle);
        Assert.False(flat[5].IsHeader);
        Assert.Equal("VLC", flat[5].DisplayName);
    }

    [Fact]
    public void Headers_AreNotSelectable_AndDisabled()
    {
        var header = new AppPickerRow
        {
            IsHeader = true,
            HeaderTitle = "Popular distractions"
        };

        Assert.True(header.IsHeader);
        Assert.False(header.IsSelectable);
        Assert.False(header.IsEnabled);
    }

    [Fact]
    public void AlreadyBlockedRows_AreDisabled_Ticked_AndHaveStatusText()
    {
        var raw = new List<AppPickerItem>
        {
            new() { DisplayName = "Discord", ExeName = "Discord.exe", Category = AppPickerLogic.CatPopular }
        };

        var flat = AppPickerLogic.BuildFlatPickerList(raw, alreadyBlockedExes: new[] { "discord.exe" }, searchText: null, showAllProcesses: false);

        Assert.Equal(2, flat.Count); // Header + Discord
        var appRow = flat[1];
        Assert.False(appRow.IsHeader);
        Assert.True(appRow.IsAlreadyBlocked);
        Assert.True(appRow.IsSelected); // Checked
        Assert.False(appRow.IsEnabled); // Disabled
        Assert.False(appRow.IsSelectable);
        Assert.Equal("already blocked", appRow.StatusText);
    }

    [Fact]
    public void GetSelectedCount_CountsOnlyNewlySelectedItems()
    {
        var rows = new List<AppPickerRow>
        {
            new() { IsHeader = true, HeaderTitle = "Popular" },
            new() { IsHeader = false, ExeName = "discord.exe", IsAlreadyBlocked = true, IsSelected = true }, // already blocked
            new() { IsHeader = false, ExeName = "steam.exe", IsAlreadyBlocked = false, IsSelected = true }, // user ticked
            new() { IsHeader = false, ExeName = "spotify.exe", IsAlreadyBlocked = false, IsSelected = false }, // not ticked
            new() { IsHeader = false, ExeName = "vlc.exe", IsAlreadyBlocked = false, IsSelected = true } // user ticked
        };

        int count = AppPickerLogic.GetSelectedCount(rows);
        Assert.Equal(2, count);
    }

    [Fact]
    public void GetSelectedExeNames_ReturnsNormalizedDeduplicatedNames()
    {
        var rows = new List<AppPickerRow>
        {
            new() { IsHeader = true, HeaderTitle = "Popular" },
            new() { IsHeader = false, ExeName = "steam.exe", IsAlreadyBlocked = false, IsSelected = true },
            new() { IsHeader = false, ExeName = "STEAM.EXE", IsAlreadyBlocked = false, IsSelected = true },
            new() { IsHeader = false, ExeName = "notepad", IsAlreadyBlocked = false, IsSelected = true },
            new() { IsHeader = false, ExeName = "blocked.exe", IsAlreadyBlocked = true, IsSelected = true }
        };

        var names = AppPickerLogic.GetSelectedExeNames(rows);

        Assert.Equal(2, names.Count);
        Assert.Contains("steam.exe", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("notepad.exe", names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppPickerLogic_ExcludesSankalpaAndBlockerSelfApps()
    {
        var rawItems = new List<AppPickerItem>
        {
            new() { DisplayName = "Sankalpa version 1.3.3", ExeName = "Sankalpa.exe", Path = @"C:\Users\User\AppData\Local\Programs\Sankalpa\Sankalpa.exe", Category = AppPickerLogic.CatInstalled },
            new() { DisplayName = "Sankalpa Service", ExeName = "Blocker.Service.exe", Category = AppPickerLogic.CatSystem },
            new() { DisplayName = "Native Messaging Host", ExeName = "Blocker.NativeHost.exe", Category = AppPickerLogic.CatSystem },
            new() { DisplayName = "Discord", ExeName = "Discord.exe", Category = AppPickerLogic.CatInstalled },
            new() { DisplayName = "Visual Studio Code", ExeName = "Code.exe", Category = AppPickerLogic.CatInstalled }
        };

        var filtered = AppPickerLogic.FilterAndDeduplicate(rawItems, null, showAllProcesses: true);

        Assert.Equal(2, filtered.Count);
        Assert.DoesNotContain(filtered, i => i.ExeName.Contains("Sankalpa", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(filtered, i => i.ExeName.Contains("Blocker", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(filtered, i => i.ExeName.Equals("Discord.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(filtered, i => i.ExeName.Equals("Code.exe", StringComparison.OrdinalIgnoreCase));
    }
}
