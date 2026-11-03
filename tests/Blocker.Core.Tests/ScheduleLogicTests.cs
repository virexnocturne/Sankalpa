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
}
