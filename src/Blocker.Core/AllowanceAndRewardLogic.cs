using System;

namespace Blocker.Core;

/// <summary>
/// Configuration for daily time allowances (e.g. 30 minutes of YouTube / social media per day).
/// </summary>
public class AllowanceConfig
{
    public bool Enabled { get; set; } = false;
    public int DailyAllowanceMinutes { get; set; } = 30;
    public bool OnlyWithinSchedule { get; set; } = false;
}

/// <summary>
/// Configuration for earned rewards (e.g. 50 min of focus earns 10 min of reward time).
/// </summary>
public class RewardConfig
{
    public bool Enabled { get; set; } = false;
    public int FocusMinutesRequired { get; set; } = 50;
    public int RewardMinutesGranted { get; set; } = 10;
    public int MaxBankedMinutes { get; set; } = 60;
}

/// <summary>
/// Pomodoro / Focus session phases.
/// </summary>
public enum PomodoroPhase
{
    Idle = 0,
    Focus = 1,
    ShortBreak = 2,
    LongBreak = 3
}

/// <summary>
/// Configuration for Pomodoro focus sessions.
/// </summary>
public class PomodoroConfig
{
    public bool Enabled { get; set; } = false;
    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public int CyclesBeforeLongBreak { get; set; } = 4;
}

/// <summary>
/// The resolved origin source of an active block decision.
/// </summary>
public enum BlockDecisionSource
{
    None = 0,
    TamperProtection = 1,
    ManualSession = 2,
    Schedule = 3,
    AllowanceExhausted = 4,
    PomodoroFocus = 5
}

/// <summary>
/// Pure business logic for daily time allowances, rewards, and pomodoro break coordination.
/// Enforces a strict priority hierarchy so that blocks never conflict, overlap, or accidentally open.
/// </summary>
public static class AllowanceAndRewardLogic
{
    /// <summary>
    /// Checks if a daily allowance has been fully consumed today.
    /// </summary>
    public static bool IsAllowanceExhausted(int usedSecondsToday, int dailyAllowanceMinutes)
    {
        if (dailyAllowanceMinutes <= 0) return true;
        return usedSecondsToday >= (dailyAllowanceMinutes * 60);
    }

    /// <summary>
    /// Returns the remaining seconds of daily allowance available today.
    /// </summary>
    public static int GetRemainingAllowanceSeconds(int usedSecondsToday, int dailyAllowanceMinutes)
    {
        if (dailyAllowanceMinutes <= 0) return 0;
        int totalAllowanceSeconds = dailyAllowanceMinutes * 60;
        return Math.Max(0, totalAllowanceSeconds - usedSecondsToday);
    }

    /// <summary>
    /// Calculates reward minutes earned based on cumulative focused minutes.
    /// Caps at MaxBankedMinutes.
    /// </summary>
    public static int CalculateEarnedReward(
        int focusedSeconds,
        int focusMinutesRequired,
        int rewardMinutesGranted,
        int currentBankedMinutes,
        int maxBankedMinutes)
    {
        if (focusMinutesRequired <= 0 || rewardMinutesGranted <= 0) return currentBankedMinutes;

        int totalFocusedMinutes = focusedSeconds / 60;
        int intervalsEarned = totalFocusedMinutes / focusMinutesRequired;
        int totalEarnedMinutes = intervalsEarned * rewardMinutesGranted;

        int newBank = currentBankedMinutes + totalEarnedMinutes;
        return Math.Min(maxBankedMinutes, newBank);
    }

    /// <summary>
    /// Evaluates whether a profile should block an app or domain considering schedule, allowance, and active breaks.
    /// Priority order:
    ///   1. Manual Session (Hard enforcement - highest priority)
    ///   2. Schedule Window (When in schedule window)
    ///   3. Allowance Window (If allowance active, allows usage until exhausted)
    ///   4. Pomodoro Focus / Break state
    /// </summary>
    public static (bool IsBlocked, BlockDecisionSource Source, string Reason) EvaluateBlockState(
        bool profileEnabled,
        bool isManualSessionActive,
        bool isManualSessionLocked,
        bool isScheduledNow,
        AllowanceConfig? allowance,
        int usedSecondsToday,
        PomodoroPhase pomodoroPhase)
    {
        // 1. Manual Session
        if (isManualSessionActive)
        {
            return (true, BlockDecisionSource.ManualSession, "Manual Block Session active");
        }

        if (!profileEnabled)
        {
            return (false, BlockDecisionSource.None, "Profile is disabled");
        }

        // 2. Pomodoro Phase check
        if (pomodoroPhase == PomodoroPhase.ShortBreak || pomodoroPhase == PomodoroPhase.LongBreak)
        {
            // Break is active; temporarily suspend block unless a hard manual lock was set
            return (false, BlockDecisionSource.None, $"Pomodoro {pomodoroPhase} active");
        }

        if (pomodoroPhase == PomodoroPhase.Focus)
        {
            return (true, BlockDecisionSource.PomodoroFocus, "Pomodoro Focus session active");
        }

        // 3. Schedule & Allowance Interaction
        if (allowance != null && allowance.Enabled)
        {
            bool inAllowanceScope = !allowance.OnlyWithinSchedule || isScheduledNow;

            if (inAllowanceScope)
            {
                bool exhausted = IsAllowanceExhausted(usedSecondsToday, allowance.DailyAllowanceMinutes);
                if (exhausted)
                {
                    return (true, BlockDecisionSource.AllowanceExhausted,
                        $"Daily allowance of {allowance.DailyAllowanceMinutes}m exhausted");
                }
                else
                {
                    int remSec = GetRemainingAllowanceSeconds(usedSecondsToday, allowance.DailyAllowanceMinutes);
                    return (false, BlockDecisionSource.None,
                        $"Allowance active ({remSec / 60}m {remSec % 60}s remaining)");
                }
            }
        }

        // 4. Standard Schedule
        if (isScheduledNow)
        {
            return (true, BlockDecisionSource.Schedule, "Scheduled Block active");
        }

        return (false, BlockDecisionSource.None, "Outside active schedule");
    }
}
