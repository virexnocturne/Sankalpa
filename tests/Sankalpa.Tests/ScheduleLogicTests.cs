using System;
using System.Collections.Generic;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class ScheduleLogicTests
{
    [Fact]
    public void IsActive_StandardWindow_ActiveWithinRange()
    {
        // Monday 09:00 - 17:00
        var rules = new List<ScheduleRule>
        {
            new() { Days = DaysOfWeek.Mon, StartMinute = 540, EndMinute = 1020 }
        };

        // Monday 10:30 (active)
        DateTime monActive = new DateTime(2026, 11, 2, 10, 30, 0); // Nov 2, 2026 is Monday
        Assert.True(ScheduleLogic.IsActive(rules, monActive));

        // Monday 08:59 (inactive)
        DateTime monBefore = new DateTime(2026, 11, 2, 8, 59, 0);
        Assert.False(ScheduleLogic.IsActive(rules, monBefore));

        // Monday 17:00 (inactive, half-open)
        DateTime monAfter = new DateTime(2026, 11, 2, 17, 0, 0);
        Assert.False(ScheduleLogic.IsActive(rules, monAfter));

        // Tuesday 10:30 (inactive day)
        DateTime tueInactive = new DateTime(2026, 11, 3, 10, 30, 0);
        Assert.False(ScheduleLogic.IsActive(rules, tueInactive));
    }

    [Fact]
    public void IsActive_MidnightCrossing_ActiveOnStartDayAndNextDayMorning()
    {
        // Monday 22:00 - 06:00 (StartMinute=1320, EndMinute=360)
        var rules = new List<ScheduleRule>
        {
            new() { Days = DaysOfWeek.Mon, StartMinute = 1320, EndMinute = 360 }
        };

        // Monday 23:00 (active)
        DateTime monNight = new DateTime(2026, 11, 2, 23, 0, 0);
        Assert.True(ScheduleLogic.IsActive(rules, monNight));

        // Monday 21:59 (inactive)
        DateTime monBefore = new DateTime(2026, 11, 2, 21, 59, 0);
        Assert.False(ScheduleLogic.IsActive(rules, monBefore));

        // Tuesday 03:00 (active because started Monday)
        DateTime tueMorning = new DateTime(2026, 11, 3, 3, 0, 0);
        Assert.True(ScheduleLogic.IsActive(rules, tueMorning));

        // Tuesday 06:00 (inactive)
        DateTime tueAfter = new DateTime(2026, 11, 3, 6, 0, 0);
        Assert.False(ScheduleLogic.IsActive(rules, tueAfter));

        // Tuesday 23:00 (inactive because rule was only for Mon)
        DateTime tueNight = new DateTime(2026, 11, 3, 23, 0, 0);
        Assert.False(ScheduleLogic.IsActive(rules, tueNight));
    }

    [Fact]
    public void IsActive_SundayToMondayWrap_HandlesMidnightCrossing()
    {
        // Sunday 22:00 - 06:00
        var rules = new List<ScheduleRule>
        {
            new() { Days = DaysOfWeek.Sun, StartMinute = 1320, EndMinute = 360 }
        };

        // Sunday Nov 8, 2026 23:30 (active)
        DateTime sunNight = new DateTime(2026, 11, 8, 23, 30, 0);
        Assert.True(ScheduleLogic.IsActive(rules, sunNight));

        // Monday Nov 9, 2026 02:15 (active on Monday morning from Sunday night)
        DateTime monMorning = new DateTime(2026, 11, 9, 2, 15, 0);
        Assert.True(ScheduleLogic.IsActive(rules, monMorning));

        // Monday Nov 9, 2026 06:00 (inactive)
        DateTime monAfter = new DateTime(2026, 11, 9, 6, 0, 0);
        Assert.False(ScheduleLogic.IsActive(rules, monAfter));
    }

    [Fact]
    public void IsActive_OverlappingRules_ActiveIfAnyMatches()
    {
        var rules = new List<ScheduleRule>
        {
            new() { Days = DaysOfWeek.Mon, StartMinute = 540, EndMinute = 720 },  // 09:00 - 12:00
            new() { Days = DaysOfWeek.Mon, StartMinute = 660, EndMinute = 900 }   // 11:00 - 15:00
        };

        // Monday 11:30 is in both
        Assert.True(ScheduleLogic.IsActive(rules, new DateTime(2026, 11, 2, 11, 30, 0)));

        // Monday 14:00 is in second rule only
        Assert.True(ScheduleLogic.IsActive(rules, new DateTime(2026, 11, 2, 14, 0, 0)));

        // Monday 16:00 is after both
        Assert.False(ScheduleLogic.IsActive(rules, new DateTime(2026, 11, 2, 16, 0, 0)));
    }

    [Fact]
    public void IsActive_EmptyOrNullRules_ReturnsFalse()
    {
        Assert.False(ScheduleLogic.IsActive(null, DateTime.Now));
        Assert.False(ScheduleLogic.IsActive(new List<ScheduleRule>(), DateTime.Now));
    }

    [Fact]
    public void NextChange_CalculatesNextTransitionAccurately()
    {
        // Monday 09:00 - 17:00
        var rules = new List<ScheduleRule>
        {
            new() { Days = DaysOfWeek.Mon, StartMinute = 540, EndMinute = 1020 }
        };

        // Monday 08:00 (currently inactive): next change is 09:00 (active start)
        DateTime monBefore = new DateTime(2026, 11, 2, 8, 0, 0);
        var change1 = ScheduleLogic.NextChange(rules, monBefore);
        Assert.NotNull(change1);
        Assert.Equal(new DateTime(2026, 11, 2, 9, 0, 0), change1.Value);

        // Monday 10:00 (currently active): next change is 17:00 (active end)
        DateTime monDuring = new DateTime(2026, 11, 2, 10, 0, 0);
        var change2 = ScheduleLogic.NextChange(rules, monDuring);
        Assert.NotNull(change2);
        Assert.Equal(new DateTime(2026, 11, 2, 17, 0, 0), change2.Value);
    }

    [Fact]
    public void FormatSummary_ReturnsExpectedDescriptions()
    {
        Assert.Equal("No schedule: blocked at all times when On", ScheduleLogic.FormatSummary(null));
        Assert.Equal("No schedule: blocked at all times when On", ScheduleLogic.FormatSummary(new List<ScheduleRule>()));

        var rules = new List<ScheduleRule>
        {
            new() { Days = DaysOfWeek.Weekdays, StartMinute = 480, EndMinute = 840 }
        };
        Assert.Equal("Mon-Fri 08:00-14:00", ScheduleLogic.FormatSummary(rules));
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(480, "08:00")]
    [InlineData(720, "12:00")]
    [InlineData(1020, "17:00")]
    [InlineData(1439, "23:59")]
    public void FormatMinute_24Hour_ReturnsExpectedFormat(int minuteOfDay, string expected)
    {
        Assert.Equal(expected, ScheduleLogic.FormatMinute(minuteOfDay, "24h"));
    }

    [Theory]
    [InlineData(0, "12:00 AM")]
    [InlineData(480, "8:00 AM")]
    [InlineData(720, "12:00 PM")]
    [InlineData(1020, "5:00 PM")]
    [InlineData(1439, "11:59 PM")]
    public void FormatMinute_12Hour_ReturnsExpectedFormat(int minuteOfDay, string expected)
    {
        Assert.Equal(expected, ScheduleLogic.FormatMinute(minuteOfDay, "12h"));
    }

    [Theory]
    [InlineData("08:00", 480)]
    [InlineData("17:00", 1020)]
    [InlineData("00:00", 0)]
    [InlineData("0:00", 0)]
    [InlineData("8:00", 480)]
    [InlineData("23:59", 1439)]
    public void TryParseMinute_24HourInputs_ParsesCorrectly(string input, int expectedMinute)
    {
        bool success = ScheduleLogic.TryParseMinute(input, out int minute);
        Assert.True(success);
        Assert.Equal(expectedMinute, minute);
    }

    [Theory]
    [InlineData("8:00 AM", 480)]
    [InlineData("5:00 PM", 1020)]
    [InlineData("12:00 PM", 720)]
    [InlineData("12:00 AM", 0)]
    [InlineData("8am", 480)]
    [InlineData("5pm", 1020)]
    [InlineData("8 am", 480)]
    [InlineData("5 pm", 1020)]
    [InlineData("12am", 0)]
    [InlineData("12pm", 720)]
    [InlineData("8:30 PM", 1230)]
    [InlineData("8:30pm", 1230)]
    [InlineData("8:00 a.m.", 480)]
    [InlineData("5:00 p.m.", 1020)]
    public void TryParseMinute_12HourInputs_ParsesCorrectly(string input, int expectedMinute)
    {
        bool success = ScheduleLogic.TryParseMinute(input, out int minute);
        Assert.True(success);
        Assert.Equal(expectedMinute, minute);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-time")]
    [InlineData("25:00")]
    [InlineData("12:60")]
    [InlineData("-1:00")]
    [InlineData("24:00")]
    public void TryParseMinute_InvalidInputs_ReturnsFalse(string? input)
    {
        bool success = ScheduleLogic.TryParseMinute(input, out int minute);
        Assert.False(success);
        Assert.Equal(0, minute);
    }

    [Fact]
    public void FormatSummary_12HourAnd24Hour_ProducesCorrectFormatting()
    {
        var rules = new List<ScheduleRule>
        {
            new() { Days = DaysOfWeek.Weekdays, StartMinute = 480, EndMinute = 1020 },
            new() { Days = DaysOfWeek.Weekend, StartMinute = 540, EndMinute = 720 }
        };

        string summary24 = ScheduleLogic.FormatSummary(rules, "24h");
        Assert.Equal("Mon-Fri 08:00-17:00, Weekends 09:00-12:00", summary24);

        string summary12 = ScheduleLogic.FormatSummary(rules, "12h");
        Assert.Equal("Mon-Fri 8:00 AM-5:00 PM, Weekends 9:00 AM-12:00 PM", summary12);
    }

    [Fact]
    public void FormatSingleRule_12HourAnd24Hour_FormatsCorrectly()
    {
        var rule = new ScheduleRule
        {
            Days = DaysOfWeek.Mon | DaysOfWeek.Wed | DaysOfWeek.Fri,
            StartMinute = 480,
            EndMinute = 1020
        };

        Assert.Equal("Mon, Wed, Fri 08:00-17:00", ScheduleLogic.FormatSingleRule(rule, "24h"));
        Assert.Equal("Mon, Wed, Fri 8:00 AM-5:00 PM", ScheduleLogic.FormatSingleRule(rule, "12h"));
    }
}
