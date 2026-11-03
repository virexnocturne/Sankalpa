using System;
using System.Collections.Generic;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class AdvancedSchedulingTests
{
    [Fact]
    public void NormalizeSchedule_MergesOverlappingSameDayIntervals()
    {
        // Monday 09:00 - 12:00 (540-720) and 11:00 - 15:00 (660-900)
        var rules = new List<ScheduleRule>
        {
            new() { Days = DaysOfWeek.Mon, StartMinute = 540, EndMinute = 720 },
            new() { Days = DaysOfWeek.Mon, StartMinute = 660, EndMinute = 900 }
        };

        var normalized = ScheduleLogic.NormalizeSchedule(rules);

        Assert.Single(normalized);
        Assert.Equal(DaysOfWeek.Mon, normalized[0].Days);
        Assert.Equal(540, normalized[0].StartMinute); // 09:00
        Assert.Equal(900, normalized[0].EndMinute);   // 15:00
    }

    [Fact]
    public void NormalizeSchedule_MergesAdjacentSameDayIntervals()
    {
        // Monday 09:00 - 12:00 and 12:00 - 17:00
        var rules = new List<ScheduleRule>
        {
            new() { Days = DaysOfWeek.Mon, StartMinute = 540, EndMinute = 720 },
            new() { Days = DaysOfWeek.Mon, StartMinute = 720, EndMinute = 1020 }
        };

        var normalized = ScheduleLogic.NormalizeSchedule(rules);

        Assert.Single(normalized);
        Assert.Equal(DaysOfWeek.Mon, normalized[0].Days);
        Assert.Equal(540, normalized[0].StartMinute);
        Assert.Equal(1020, normalized[0].EndMinute); // 09:00 - 17:00
    }

    [Fact]
    public void NormalizeSchedule_PreservesDisjointIntervalsForSameDay()
    {
        // Monday 09:00 - 12:00 and 14:00 - 18:00 (lunch break 12:00-14:00)
        var rules = new List<ScheduleRule>
        {
            new() { Days = DaysOfWeek.Mon, StartMinute = 540, EndMinute = 720 },
            new() { Days = DaysOfWeek.Mon, StartMinute = 840, EndMinute = 1080 }
        };

        var normalized = ScheduleLogic.NormalizeSchedule(rules);

        Assert.Equal(2, normalized.Count);
        Assert.Equal(540, normalized[0].StartMinute);
        Assert.Equal(720, normalized[0].EndMinute);
        Assert.Equal(840, normalized[1].StartMinute);
        Assert.Equal(1080, normalized[1].EndMinute);
    }

    [Fact]
    public void NormalizeSchedule_GroupsIdenticalIntervalsAcrossDays()
    {
        // Mon 08:00-17:00 and Tue 08:00-17:00
        var rules = new List<ScheduleRule>
        {
            new() { Days = DaysOfWeek.Mon, StartMinute = 480, EndMinute = 1020 },
            new() { Days = DaysOfWeek.Tue, StartMinute = 480, EndMinute = 1020 }
        };

        var normalized = ScheduleLogic.NormalizeSchedule(rules);

        Assert.Single(normalized);
        Assert.Equal(DaysOfWeek.Mon | DaysOfWeek.Tue, normalized[0].Days);
        Assert.Equal(480, normalized[0].StartMinute);
        Assert.Equal(1020, normalized[0].EndMinute);
    }

    [Fact]
    public void FormatAdvancedSummary_ProducesReadableSummary()
    {
        var rules = new List<ScheduleRule>
        {
            new() { Days = DaysOfWeek.Mon, StartMinute = 540, EndMinute = 720 },
            new() { Days = DaysOfWeek.Mon, StartMinute = 840, EndMinute = 1080 }
        };

        string summary = ScheduleLogic.FormatAdvancedSummary(rules, "24h");
        Assert.Contains("Mon", summary);
        Assert.Contains("09:00-12:00", summary);
        Assert.Contains("14:00-18:00", summary);
    }

    [Fact]
    public void AllowanceAndRewardLogic_ExhaustionAndRemainingCalculations()
    {
        // 30 min allowance = 1800 sec
        Assert.False(AllowanceAndRewardLogic.IsAllowanceExhausted(1000, 30));
        Assert.Equal(800, AllowanceAndRewardLogic.GetRemainingAllowanceSeconds(1000, 30));

        Assert.True(AllowanceAndRewardLogic.IsAllowanceExhausted(1800, 30));
        Assert.Equal(0, AllowanceAndRewardLogic.GetRemainingAllowanceSeconds(1800, 30));

        Assert.True(AllowanceAndRewardLogic.IsAllowanceExhausted(2000, 30));
        Assert.Equal(0, AllowanceAndRewardLogic.GetRemainingAllowanceSeconds(2000, 30));
    }

    [Fact]
    public void AllowanceAndRewardLogic_EarnedRewardCalculation()
    {
        // 50 min focus required for 10 min reward. 100 min focus -> 2 intervals -> 20 min earned.
        int initialBank = 5;
        int focusedSeconds = 100 * 60; // 100 min
        int newBank = AllowanceAndRewardLogic.CalculateEarnedReward(
            focusedSeconds,
            focusMinutesRequired: 50,
            rewardMinutesGranted: 10,
            currentBankedMinutes: initialBank,
            maxBankedMinutes: 60);

        Assert.Equal(25, newBank); // 5 + 20 = 25
    }

    [Fact]
    public void AllowanceAndRewardLogic_EvaluateBlockStatePrecedence()
    {
        var allowance = new AllowanceConfig
        {
            Enabled = true,
            DailyAllowanceMinutes = 30,
            OnlyWithinSchedule = true
        };

        // 1. Outside schedule: not blocked
        var resOutside = AllowanceAndRewardLogic.EvaluateBlockState(
            profileEnabled: true,
            isManualSessionActive: false,
            isManualSessionLocked: false,
            isScheduledNow: false,
            allowance: allowance,
            usedSecondsToday: 500,
            pomodoroPhase: PomodoroPhase.Idle);

        Assert.False(resOutside.IsBlocked);

        // 2. Inside schedule, allowance NOT exhausted (500s used of 1800s): not blocked
        var resInsideAllowance = AllowanceAndRewardLogic.EvaluateBlockState(
            profileEnabled: true,
            isManualSessionActive: false,
            isManualSessionLocked: false,
            isScheduledNow: true,
            allowance: allowance,
            usedSecondsToday: 500,
            pomodoroPhase: PomodoroPhase.Idle);

        Assert.False(resInsideAllowance.IsBlocked);

        // 3. Inside schedule, allowance EXHAUSTED (1800s used of 1800s): BLOCKED!
        var resExhausted = AllowanceAndRewardLogic.EvaluateBlockState(
            profileEnabled: true,
            isManualSessionActive: false,
            isManualSessionLocked: false,
            isScheduledNow: true,
            allowance: allowance,
            usedSecondsToday: 1800,
            pomodoroPhase: PomodoroPhase.Idle);

        Assert.True(resExhausted.IsBlocked);
        Assert.Equal(BlockDecisionSource.AllowanceExhausted, resExhausted.Source);

        // 4. Manual Session active: overrides everything and blocks immediately
        var resManual = AllowanceAndRewardLogic.EvaluateBlockState(
            profileEnabled: true,
            isManualSessionActive: true,
            isManualSessionLocked: true,
            isScheduledNow: false,
            allowance: allowance,
            usedSecondsToday: 0,
            pomodoroPhase: PomodoroPhase.Idle);

        Assert.True(resManual.IsBlocked);
        Assert.Equal(BlockDecisionSource.ManualSession, resManual.Source);

        // 5. Pomodoro Break active: suspends block
        var resBreak = AllowanceAndRewardLogic.EvaluateBlockState(
            profileEnabled: true,
            isManualSessionActive: false,
            isManualSessionLocked: false,
            isScheduledNow: true,
            allowance: null,
            usedSecondsToday: 0,
            pomodoroPhase: PomodoroPhase.ShortBreak);

        Assert.False(resBreak.IsBlocked);
    }
}
