using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class UiLogicTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(-5, "00:00")]
    [InlineData(45, "00:45")]
    [InlineData(60, "01:00")]
    [InlineData(125, "02:05")]
    [InlineData(3600, "01:00:00")]
    [InlineData(3665, "01:01:05")]
    [InlineData(7200, "02:00:00")]
    [InlineData(86400, "1d")]
    [InlineData(90000, "1d 1h")]
    [InlineData(602434, "6d 23h")]
    [InlineData(604800, "1w")]
    [InlineData(691200, "1w 1d")]
    [InlineData(2592000, "1mo")]
    [InlineData(3024000, "1mo 5d")]
    [InlineData(31536000, "1y")]
    public void FormatCountdown_ReturnsExpectedFormat(long seconds, string expected)
    {
        string actual = UiLogic.FormatCountdown(seconds);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(-10, "00:00")]
    [InlineData(45, "00:45")]
    [InlineData(300, "05:00")]
    [InlineData(600, "10:00")]
    [InlineData(1200, "20:00")]
    [InlineData(1800, "30:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "01:00:00")]
    [InlineData(4500, "01:15:00")]
    [InlineData(7200, "02:00:00")]
    public void FormatMeditationTimer_AdaptiveMinutesAndHours(long seconds, string expected)
    {
        string actual = UiLogic.FormatMeditationTimer(seconds);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BlockSessionLogic_EvaluateStopRequest_FormatsTrueCountdownForMultiDaySessions()
    {
        var session = new BlockSession
        {
            Id = 1,
            ProfileId = 1,
            StartUtc = DateTime.UtcNow,
            DurationSeconds = 604800, // 1 week
            Locked = true
        };

        var (canStop, remaining, reason) = BlockSessionLogic.EvaluateStopRequest(session, DateTime.UtcNow.AddSeconds(2366), emergencyUnlockPresent: false);
        Assert.False(canStop);
        Assert.Contains("6d 23h", reason);
        Assert.DoesNotContain("20:34", reason);
    }

    [Fact]
    public void ValidateProcessInput_Empty_ReturnsFalse()
    {
        bool ok = UiLogic.ValidateProcessInput("   ", out var norm, out var err);
        Assert.False(ok);
        Assert.NotNull(err);
        Assert.Null(norm);
    }

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("csrss")]
    [InlineData("taskmgr.exe")]
    [InlineData("winlogon")]
    public void ValidateProcessInput_Protected_ReturnsFalse(string protectedProc)
    {
        bool ok = UiLogic.ValidateProcessInput(protectedProc, out var norm, out var err);
        Assert.False(ok);
        Assert.Contains("protected", err, StringComparison.OrdinalIgnoreCase);
        Assert.Null(norm);
    }

    [Theory]
    [InlineData("notepad.exe", "notepad.exe")]
    [InlineData("notepad", "notepad.exe")]
    [InlineData("Discord", "discord.exe")]
    [InlineData(@"C:\Tools\mytool.exe", "mytool.exe")]
    public void ValidateProcessInput_Valid_ReturnsNormalized(string input, string expected)
    {
        bool ok = UiLogic.ValidateProcessInput(input, out var norm, out var err);
        Assert.True(ok);
        Assert.Null(err);
        Assert.Equal(expected, norm);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("nodots")]
    [InlineData("site with spaces.com")]
    [InlineData("*.wildcard.com")]
    [InlineData("has#comment.com")]
    public void ValidateDomainInput_Invalid_ReturnsFalse(string domain)
    {
        bool ok = UiLogic.ValidateDomainInput(domain, out var norm, out var err);
        Assert.False(ok);
        Assert.NotNull(err);
    }

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("www.example.com", "example.com")]
    [InlineData("m.example.com", "example.com")]
    [InlineData("https://www.example.com/r/popular", "example.com")]
    [InlineData("HTTP://example.org/", "example.org")]
    public void ValidateDomainInput_Valid_ReturnsNormalized(string domain, string expected)
    {
        bool ok = UiLogic.ValidateDomainInput(domain, out var norm, out var err);
        Assert.True(ok);
        Assert.Null(err);
        Assert.Equal(expected, norm);
    }

    [Fact]
    public void CanEditProfile_LockedActiveSession_ReturnsFalseForSameProfile()
    {
        var profile = new Profile { Name = "DeepWork" };
        var status = new StatusData
        {
            Active = true,
            Locked = true,
            Profile = "DeepWork"
        };

        bool canEdit = UiLogic.CanEditProfile(profile, status, out string? reason);
        Assert.False(canEdit);
        Assert.Contains("cannot be edited while an active locked session", reason);

        // Different profile should be editable
        var otherProfile = new Profile { Name = "Other" };
        bool canEditOther = UiLogic.CanEditProfile(otherProfile, status, out reason);
        Assert.True(canEditOther);
        Assert.Null(reason);
    }

    [Fact]
    public void CanDeleteProfile_DefaultProfile_ReturnsFalse()
    {
        var defaultProf = new Profile { Name = "Default" };
        bool canDelete = UiLogic.CanDeleteProfile(defaultProf, null, out string? reason);
        Assert.False(canDelete);
        Assert.Contains("cannot be deleted", reason);
    }

    [Fact]
    public void CanDeleteProfile_LockedActiveSession_ReturnsFalse()
    {
        var prof = new Profile { Name = "Work" };
        var status = new StatusData
        {
            Active = true,
            Locked = true,
            Profile = "Work"
        };

        bool canDelete = UiLogic.CanDeleteProfile(prof, status, out string? reason);
        Assert.False(canDelete);
        Assert.Contains("cannot be deleted while an active locked session", reason);
    }

    [Fact]
    public void CanStartSession_ChecksValidation()
    {
        // When active session already exists
        var activeStatus = new StatusData { Active = true };
        bool canStart1 = UiLogic.CanStartSession(activeStatus, "Study", 25, out string? err1);
        Assert.False(canStart1);
        Assert.Contains("already active", err1);

        // When profile empty
        var idleStatus = new StatusData { Active = false };
        bool canStart2 = UiLogic.CanStartSession(idleStatus, "", 25, out string? err2);
        Assert.False(canStart2);
        Assert.Contains("select a profile", err2);

        // When minutes <= 0
        bool canStart3 = UiLogic.CanStartSession(idleStatus, "Study", 0, out string? err3);
        Assert.False(canStart3);
        Assert.Contains("greater than zero", err3);

        // Valid
        bool canStart4 = UiLogic.CanStartSession(idleStatus, "Study", 25, out string? err4);
        Assert.True(canStart4);
        Assert.Null(err4);
    }

    [Theory]
    [InlineData(true, true, new string[] { "youtube.com" }, true)]
    [InlineData(true, false, new string[] { "youtube.com" }, false)]
    [InlineData(false, true, new string[] { "youtube.com" }, false)]
    [InlineData(false, true, new string[] { }, true)]
    [InlineData(false, false, new string[] { }, false)]
    public void CalculateHostsBlockingOk_FollowsElevationAndDomainRules(bool elevated, bool hostsApplySuccess, string[] domains, bool expected)
    {
        bool actual = UiLogic.CalculateHostsBlockingOk(elevated, hostsApplySuccess, domains);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ValidateProfileSave_EmptyName_Fails()
    {
        bool ok = UiLogic.ValidateProfileSave("", 1, 0, null, null, out var err);
        Assert.False(ok);
        Assert.Contains("name cannot be empty", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateProfileSave_DuplicateName_Fails()
    {
        var existing = new[] { "Work", "Default", "Gaming" };
        bool ok = UiLogic.ValidateProfileSave("WORK", 1, 0, existing, null, out var err);
        Assert.False(ok);
        Assert.Contains("already exists", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateProfileSave_SameNameWhenEditingExisting_Succeeds()
    {
        var existing = new[] { "Work", "Default", "Gaming" };
        bool ok = UiLogic.ValidateProfileSave("Work", 1, 0, existing, "Work", out var err);
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void ValidateProfileSave_ZeroAppsAndSites_Fails()
    {
        bool ok = UiLogic.ValidateProfileSave("Productive", 0, 0, null, null, out var err);
        Assert.False(ok);
        Assert.Contains("must contain at least one", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateProfileSave_HasAppOrSite_Succeeds()
    {
        bool okApp = UiLogic.ValidateProfileSave("AppOnly", 1, 0, null, null, out var err1);
        Assert.True(okApp);
        Assert.Null(err1);

        bool okSite = UiLogic.ValidateProfileSave("SiteOnly", 0, 1, null, null, out var err2);
        Assert.True(okSite);
        Assert.Null(err2);
    }

    [Theory]
    [InlineData(3, 2, 1, false, "3 apps, 2 sites, 1 exception")]
    [InlineData(1, 1, 0, false, "1 app, 1 site")]
    [InlineData(2, 0, 0, false, "2 apps, 0 sites")]
    [InlineData(0, 5, 2, false, "0 apps, 5 sites, 2 exceptions")]
    [InlineData(2, 0, 1, true, "2 apps, all sites, 1 exception")]
    [InlineData(1, 0, 0, true, "1 app, all sites")]
    public void FormatCardSummary_FormatsSingularAndPluralCorrectly(int apps, int sites, int exceptions, bool blockAll, string expected)
    {
        string actual = UiLogic.FormatCardSummary(apps, sites, exceptions, blockAll);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, "0 apps")]
    [InlineData(1, "1 app")]
    [InlineData(5, "5 apps")]
    public void FormatAppsBadge_FormatsCorrectly(int count, string expected)
    {
        Assert.Equal(expected, UiLogic.FormatAppsBadge(count));
    }

    [Theory]
    [InlineData(0, false, "0 sites")]
    [InlineData(1, false, "1 site")]
    [InlineData(4, false, "4 sites")]
    [InlineData(0, true, "all sites")]
    [InlineData(3, true, "all sites")]
    public void FormatSitesBadge_FormatsCorrectly(int count, bool blockAll, string expected)
    {
        Assert.Equal(expected, UiLogic.FormatSitesBadge(count, blockAll));
    }

    [Theory]
    [InlineData(0, "0 exceptions")]
    [InlineData(1, "1 exception")]
    [InlineData(3, "3 exceptions")]
    public void FormatExceptionsBadge_FormatsCorrectly(int count, string expected)
    {
        Assert.Equal(expected, UiLogic.FormatExceptionsBadge(count));
    }

    [Fact]
    public void RequiresExtensionNote_ReturnsTrue_WhenBlockAllWebsites()
    {
        bool req = UiLogic.RequiresExtensionNote(blockAllWebsites: true, webRules: null);
        Assert.True(req);
    }

    [Fact]
    public void RequiresExtensionNote_ReturnsTrue_WhenAnyExceptionRuleExists()
    {
        var rules = new List<WebRule>
        {
            new WebRule { Pattern = "example.com", Allow = false },
            new WebRule { Pattern = "example.com/login", Allow = true }
        };
        bool req = UiLogic.RequiresExtensionNote(blockAllWebsites: false, webRules: rules);
        Assert.True(req);
    }

    [Theory]
    [InlineData("example.com/news")]
    [InlineData("*.example.com")]
    [InlineData("example.com/$")]
    public void RequiresExtensionNote_ReturnsTrue_WhenPathOrWildcardExists(string pattern)
    {
        var rules = new List<WebRule>
        {
            new WebRule { Pattern = pattern, Allow = false }
        };
        bool req = UiLogic.RequiresExtensionNote(blockAllWebsites: false, webRules: rules);
        Assert.True(req);
    }

    [Fact]
    public void RequiresExtensionNote_ReturnsFalse_WhenOnlyPlainDomainBlocks()
    {
        var rules = new List<WebRule>
        {
            new WebRule { Pattern = "example.com", Allow = false },
            new WebRule { Pattern = "example.org", Allow = false }
        };
        bool req = UiLogic.RequiresExtensionNote(blockAllWebsites: false, webRules: rules);
        Assert.False(req);
    }

    [Fact]
    public void TestUrlAgainstRules_Blocked_ReturnsBlockedByPattern()
    {
        var rules = new List<WebRule>
        {
            new WebRule { Pattern = "example.com", Allow = false }
        };

        string res = UiLogic.TestUrlAgainstRules("https://shop.example.com", blockAllWebsites: false, webRules: rules);
        Assert.Equal("Blocked by example.com", res);
    }

    [Fact]
    public void TestUrlAgainstRules_AllowedByException_ReturnsAllowedByExceptionPattern()
    {
        var rules = new List<WebRule>
        {
            new WebRule { Pattern = "example.com", Allow = false },
            new WebRule { Pattern = "example.com/news", Allow = true }
        };

        string res = UiLogic.TestUrlAgainstRules("https://example.com/news/123", blockAllWebsites: false, webRules: rules);
        Assert.Equal("Allowed by exception example.com/news", res);
    }

    [Fact]
    public void TestUrlAgainstRules_NotBlocked_ReturnsNotBlocked()
    {
        var rules = new List<WebRule>
        {
            new WebRule { Pattern = "example.com", Allow = false }
        };

        string res = UiLogic.TestUrlAgainstRules("https://example.org", blockAllWebsites: false, webRules: rules);
        Assert.Equal("Not blocked", res);
    }

    [Fact]
    public void TestUrlAgainstRules_BlockAll_ReturnsBlockedByAllWebsites()
    {
        var rules = new List<WebRule>();

        string res = UiLogic.TestUrlAgainstRules("https://example.org", blockAllWebsites: true, webRules: rules);
        Assert.Equal("Blocked by all websites", res);
    }

    [Fact]
    public void TestUrlAgainstRules_InvalidUrl_ReturnsValidationMessage()
    {
        string emptyRes = UiLogic.TestUrlAgainstRules("", false, null);
        Assert.Equal("Enter a URL above to test", emptyRes);

        string invalidRes = UiLogic.TestUrlAgainstRules("ftp://invalid", false, null);
        Assert.Equal("Enter a valid http:// or https:// URL to test", invalidRes);
    }

    [Fact]
    public void TryAddRuleToList_Valid_AddsNormalizedPattern()
    {
        var list = new List<string>();
        bool ok = UiLogic.TryAddRuleToList(list, "HTTPS://WWW.EXAMPLE.COM/PATH/", out string norm, out string? err);
        Assert.True(ok);
        Assert.Null(err);
        Assert.Equal("www.example.com/path", norm);
        Assert.Single(list);
        Assert.Equal("www.example.com/path", list[0]);
    }

    [Fact]
    public void TryAddRuleToList_Duplicate_ReturnsError()
    {
        var list = new List<string> { "example.com" };
        bool ok = UiLogic.TryAddRuleToList(list, "EXAMPLE.COM", out _, out string? err);
        Assert.False(ok);
        Assert.NotNull(err);
        Assert.Contains("already in the list", err);
        Assert.Single(list);
    }

    [Fact]
    public void TryAddRuleToList_InvalidPattern_PassesThroughValidationMessage()
    {
        var list = new List<string>();
        bool ok = UiLogic.TryAddRuleToList(list, "example<bad>.com", out _, out string? err);
        Assert.False(ok);
        Assert.NotNull(err);
        Assert.Contains("Invalid character", err);
        Assert.Empty(list);
    }

    [Fact]
    public void RemoveRuleFromList_RemovesCorrectly()
    {
        var list = new List<string> { "example.com", "example.org" };
        bool removed = UiLogic.RemoveRuleFromList(list, "example.com");
        Assert.True(removed);
        Assert.Single(list);
        Assert.Equal("example.org", list[0]);

        bool notFound = UiLogic.RemoveRuleFromList(list, "nonexistent.com");
        Assert.False(notFound);
        Assert.Single(list);
    }

    [Fact]
    public void CanEnableSystemWide_Tests()
    {
        // 1. Plain domains -> true
        var plainRules = new List<WebRule>
        {
            new WebRule { Pattern = "example.com", Allow = false },
            new WebRule { Pattern = "example.org", Allow = false }
        };
        bool ok = UiLogic.CanEnableSystemWide(blockAllWebsites: false, plainRules, out string? hint);
        Assert.True(ok);
        Assert.Null(hint);

        // 2. Block all -> false
        bool blockAllOk = UiLogic.CanEnableSystemWide(blockAllWebsites: true, plainRules, out string? blockAllHint);
        Assert.False(blockAllOk);
        Assert.NotNull(blockAllHint);

        // 3. Exception -> false
        var withException = new List<WebRule>
        {
            new WebRule { Pattern = "example.com", Allow = false },
            new WebRule { Pattern = "example.org", Allow = true }
        };
        bool exOk = UiLogic.CanEnableSystemWide(blockAllWebsites: false, withException, out string? exHint);
        Assert.False(exOk);
        Assert.NotNull(exHint);

        // 4. Path or wildcard -> false
        var withPath = new List<WebRule>
        {
            new WebRule { Pattern = "example.com/news", Allow = false }
        };
        bool pathOk = UiLogic.CanEnableSystemWide(blockAllWebsites: false, withPath, out string? pathHint);
        Assert.False(pathOk);
        Assert.NotNull(pathHint);

        // 5. Empty rules -> false (no domain rules)
        bool emptyOk = UiLogic.CanEnableSystemWide(blockAllWebsites: false, new List<WebRule>(), out string? emptyHint);
        Assert.False(emptyOk);
        Assert.NotNull(emptyHint);
    }

    [Fact]
    public void FormatExtensionStatusSegment_Tests()
    {
        string connected = UiLogic.FormatExtensionStatusSegment(new[] { "chrome", "msedge" });
        Assert.Equal("Extension: connected (chrome, msedge)", connected);

        string notConnected = UiLogic.FormatExtensionStatusSegment(null);
        Assert.Equal("Extension: not connected", notConnected);
    }

    [Fact]
    public void FormatWarningBanners_Tests()
    {
        var banners = UiLogic.FormatWarningBanners(
            fallbackActive: true,
            missingBrowsers: new[] { "chrome", "msedge" },
            blockAllUncovered: true,
            perBrowserIncognito: new Dictionary<string, bool> { ["chrome"] = false });

        Assert.Equal(3, banners.Count);
        Assert.Contains(banners, b => b.Contains("The browser extension is missing in chrome, msedge"));
        Assert.Contains(banners, b => b.Contains("Block-all mode cannot be enforced in chrome, msedge"));
        Assert.Contains(banners, b => b.Contains("Allow the extension in incognito or it can be bypassed"));
    }

    [Theory]
    [InlineData("2026-10-29T14:30:00", "24h", "14:30")]
    [InlineData("2026-10-29T14:30:00", "12h", "2:30 PM")]
    [InlineData("2026-10-29T08:05:00", "24h", "08:05")]
    [InlineData("2026-10-29T08:05:00", "12h", "8:05 AM")]
    [InlineData("2026-10-29T00:00:00", "24h", "00:00")]
    [InlineData("2026-10-29T00:00:00", "12h", "12:00 AM")]
    [InlineData("2026-10-29T12:00:00", "24h", "12:00")]
    [InlineData("2026-10-29T12:00:00", "12h", "12:00 PM")]
    [InlineData("2026-10-29T23:59:00", "24h", "23:59")]
    [InlineData("2026-10-29T23:59:00", "12h", "11:59 PM")]
    public void FormatTime_WorksFor12hAnd24h(string isoTime, string format, string expected)
    {
        var dt = DateTime.Parse(isoTime);
        string actual = UiLogic.FormatTime(dt, format);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("2026-10-29T14:30:00", "24h", "Thu 14:30")]
    [InlineData("2026-10-29T14:30:00", "12h", "Thu 2:30 PM")]
    public void FormatTimeWithDay_WorksFor12hAnd24h(string isoTime, string format, string expected)
    {
        var dt = DateTime.Parse(isoTime);
        string actual = UiLogic.FormatTimeWithDay(dt, format);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, "24h", "00:00")]
    [InlineData(0, "12h", "12:00 AM")]
    [InlineData(480, "24h", "08:00")]
    [InlineData(480, "12h", "8:00 AM")]
    [InlineData(720, "24h", "12:00")]
    [InlineData(720, "12h", "12:00 PM")]
    [InlineData(870, "24h", "14:30")]
    [InlineData(870, "12h", "2:30 PM")]
    [InlineData(1439, "24h", "23:59")]
    [InlineData(1439, "12h", "11:59 PM")]
    public void ScheduleLogic_FormatTimeMinute_Works(int minute, string format, string expected)
    {
        string actual = ScheduleLogic.FormatTimeMinute(minute, format);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ScheduleLogic_FormatSummary_12hAnd24h()
    {
        var rules = new List<ScheduleRule>
        {
            new() { Days = DaysOfWeek.Weekdays, StartMinute = 540, EndMinute = 1020 } // 09:00 to 17:00
        };

        string summary24 = ScheduleLogic.FormatSummary(rules, "24h");
        Assert.Equal("Mon-Fri 09:00-17:00", summary24);

        string summary12 = ScheduleLogic.FormatSummary(rules, "12h");
        Assert.Equal("Mon-Fri 9:00 AM-5:00 PM", summary12);
    }
}
