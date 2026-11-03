using System;
using System.Collections.Generic;
using System.Linq;

namespace Blocker.Core;

/// <summary>
/// Pure scheduling logic.
/// Note on Daylight Saving Time (DST): Evaluation uses local wall-clock minutes (Hour * 60 + Minute).
/// As the local clock advances or falls back, rules continue to match the local wall-clock time.
/// </summary>
public static class ScheduleLogic
{
    public static bool IsActive(IEnumerable<ScheduleRule>? rules, DateTime localNow)
    {
        if (rules == null) return false;

        DayOfWeek today = localNow.DayOfWeek;
        DayOfWeek yesterday = localNow.AddDays(-1).DayOfWeek;
        int currentMinute = localNow.Hour * 60 + localNow.Minute;

        DaysOfWeek todayFlag = ScheduleRule.FromDayOfWeek(today);
        DaysOfWeek yesterdayFlag = ScheduleRule.FromDayOfWeek(yesterday);

        foreach (var rule in rules)
        {
            if (!rule.IsValid) continue;

            if (!rule.CrossesMidnight)
            {
                // Standard window within the same calendar day [StartMinute, EndMinute)
                if (rule.Days.HasFlag(todayFlag))
                {
                    if (currentMinute >= rule.StartMinute && currentMinute < rule.EndMinute)
                    {
                        return true;
                    }
                }
            }
            else
            {
                // Midnight-crossing window (e.g. 22:00 to 06:00, belongs to day it starts on)
                // 1. Started today and still before midnight [StartMinute, 1440)
                if (rule.Days.HasFlag(todayFlag) && currentMinute >= rule.StartMinute)
                {
                    return true;
                }

                // 2. Started yesterday and still before morning end [0, EndMinute)
                if (rule.Days.HasFlag(yesterdayFlag) && currentMinute < rule.EndMinute)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static DateTime? NextChange(IEnumerable<ScheduleRule>? rules, DateTime localNow)
    {
        if (rules == null) return null;

        var validRules = rules.Where(r => r.IsValid).ToList();
        if (validRules.Count == 0) return null;

        bool currentActive = IsActive(validRules, localNow);

        // Collect all potential transition boundary timestamps across the next 8 days
        var candidates = new HashSet<DateTime>();
        DateTime baseDate = localNow.Date;

        for (int d = 0; d <= 8; d++)
        {
            DateTime dayDate = baseDate.AddDays(d);
            DayOfWeek dow = dayDate.DayOfWeek;
            DaysOfWeek dowFlag = ScheduleRule.FromDayOfWeek(dow);

            foreach (var r in validRules)
            {
                if (r.Days.HasFlag(dowFlag))
                {
                    // Start of window
                    candidates.Add(dayDate.AddMinutes(r.StartMinute));

                    // End of window
                    if (!r.CrossesMidnight)
                    {
                        candidates.Add(dayDate.AddMinutes(r.EndMinute));
                    }
                    else
                    {
                        candidates.Add(dayDate.AddDays(1).AddMinutes(r.EndMinute));
                    }
                }
            }
        }

        var sorted = candidates
            .Where(t => t > localNow)
            .OrderBy(t => t)
            .ToList();

        foreach (var t in sorted)
        {
            // Evaluate state at the boundary
            bool activeAtT = IsActive(validRules, t);
            if (activeAtT != currentActive)
            {
                return t;
            }
        }

        return null;
    }

    public static string FormatSummary(IEnumerable<ScheduleRule>? rules) => FormatSummary(rules, "24h");

    public static string FormatSummary(IEnumerable<ScheduleRule>? rules, string timeFormat)
    {
        if (rules == null) return "No schedule: blocked at all times when On";

        var valid = rules.Where(r => r.IsValid).ToList();
        if (valid.Count == 0) return "No schedule: blocked at all times when On";

        var parts = valid.Select(r => FormatSingleRule(r, timeFormat)).ToList();
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Normalizes a collection of schedule rules by validating intervals,
    /// merging overlapping or adjacent intervals on the same day,
    /// and grouping identical intervals across days.
    /// </summary>
    public static List<ScheduleRule> NormalizeSchedule(IEnumerable<ScheduleRule>? rules)
    {
        if (rules == null) return new List<ScheduleRule>();

        var validRules = rules.Where(r => r.IsValid).ToList();
        if (validRules.Count == 0) return new List<ScheduleRule>();

        var singleDays = new[]
        {
            DaysOfWeek.Mon, DaysOfWeek.Tue, DaysOfWeek.Wed,
            DaysOfWeek.Thu, DaysOfWeek.Fri, DaysOfWeek.Sat, DaysOfWeek.Sun
        };

        var dayIntervals = new Dictionary<DaysOfWeek, List<(int Start, int End, bool CrossesMidnight)>>();
        foreach (var day in singleDays)
        {
            dayIntervals[day] = new List<(int Start, int End, bool CrossesMidnight)>();
        }

        foreach (var rule in validRules)
        {
            foreach (var day in singleDays)
            {
                if (rule.Days.HasFlag(day))
                {
                    dayIntervals[day].Add((rule.StartMinute, rule.EndMinute, rule.CrossesMidnight));
                }
            }
        }

        var normalizedDayRules = new Dictionary<DaysOfWeek, List<(int Start, int End, bool CrossesMidnight)>>();
        foreach (var day in singleDays)
        {
            var intervals = dayIntervals[day];
            if (intervals.Count == 0) continue;

            var sameDay = intervals.Where(i => !i.CrossesMidnight).OrderBy(i => i.Start).ToList();
            var midnight = intervals.Where(i => i.CrossesMidnight).OrderBy(i => i.Start).ToList();

            var mergedSameDay = new List<(int Start, int End, bool CrossesMidnight)>();
            foreach (var cur in sameDay)
            {
                if (mergedSameDay.Count == 0)
                {
                    mergedSameDay.Add(cur);
                }
                else
                {
                    var last = mergedSameDay[^1];
                    if (cur.Start <= last.End)
                    {
                        // Overlapping or adjacent -> merge
                        mergedSameDay[^1] = (last.Start, Math.Max(last.End, cur.End), false);
                    }
                    else
                    {
                        mergedSameDay.Add(cur);
                    }
                }
            }

            var mergedMidnight = midnight
                .GroupBy(m => (m.Start, m.End))
                .Select(g => g.First())
                .ToList();

            var dayMerged = new List<(int Start, int End, bool CrossesMidnight)>();
            dayMerged.AddRange(mergedSameDay);
            dayMerged.AddRange(mergedMidnight);
            normalizedDayRules[day] = dayMerged;
        }

        var intervalToDays = new Dictionary<(int Start, int End, bool CrossesMidnight), DaysOfWeek>();
        foreach (var (day, intervals) in normalizedDayRules)
        {
            foreach (var interval in intervals)
            {
                if (intervalToDays.TryGetValue(interval, out var existingDays))
                {
                    intervalToDays[interval] = existingDays | day;
                }
                else
                {
                    intervalToDays[interval] = day;
                }
            }
        }

        var result = new List<ScheduleRule>();
        foreach (var (interval, days) in intervalToDays)
        {
            result.Add(new ScheduleRule
            {
                Days = days,
                StartMinute = interval.Start,
                EndMinute = interval.End
            });
        }

        return result.OrderBy(r => r.StartMinute).ThenBy(r => r.EndMinute).ToList();
    }

    /// <summary>
    /// Checks whether two schedule rule collections are semantically equivalent after normalization.
    /// </summary>
    public static bool AreSchedulesEquivalent(IEnumerable<ScheduleRule>? a, IEnumerable<ScheduleRule>? b)
    {
        var normA = NormalizeSchedule(a);
        var normB = NormalizeSchedule(b);
        if (normA.Count != normB.Count) return false;
        for (int i = 0; i < normA.Count; i++)
        {
            if (normA[i].Days != normB[i].Days ||
                normA[i].StartMinute != normB[i].StartMinute ||
                normA[i].EndMinute != normB[i].EndMinute)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Formats an advanced day-by-day summary of the schedule rules, grouping by days.
    /// Example: "Mon: 09:00-12:00, 14:00-18:00 | Tue-Fri: 08:00-17:00"
    /// </summary>
    public static string FormatAdvancedSummary(IEnumerable<ScheduleRule>? rules, string timeFormat = "24h")
    {
        var normalized = NormalizeSchedule(rules);
        if (normalized.Count == 0) return "No schedule: blocked at all times when On";

        var parts = normalized.Select(r => FormatSingleRule(r, timeFormat)).ToList();
        return string.Join(" | ", parts);
    }

    public static string FormatSingleRule(ScheduleRule rule) => FormatSingleRule(rule, "24h");

    public static string FormatSingleRule(ScheduleRule rule, string timeFormat)
    {
        string daysStr = FormatDays(rule.Days);
        string timeStr = $"{FormatTimeMinute(rule.StartMinute, timeFormat)}-{FormatTimeMinute(rule.EndMinute, timeFormat)}";
        return $"{daysStr} {timeStr}";
    }

    public static string FormatTimeMinute(int minute, string timeFormat = "24h")
    {
        int hours = (minute / 60) % 24;
        int mins = minute % 60;
        if (string.Equals(timeFormat, "12h", StringComparison.OrdinalIgnoreCase))
        {
            int h12 = hours % 12;
            if (h12 == 0) h12 = 12;
            string ampm = hours < 12 ? "AM" : "PM";
            return $"{h12}:{mins:D2} {ampm}";
        }
        return $"{hours:D2}:{mins:D2}";
    }

    public static string FormatDays(DaysOfWeek days)
    {
        if (days == DaysOfWeek.All) return "Every day";
        if (days == DaysOfWeek.Weekdays) return "Mon-Fri";
        if (days == DaysOfWeek.Weekend) return "Weekends";

        var names = new List<string>();
        if (days.HasFlag(DaysOfWeek.Mon)) names.Add("Mon");
        if (days.HasFlag(DaysOfWeek.Tue)) names.Add("Tue");
        if (days.HasFlag(DaysOfWeek.Wed)) names.Add("Wed");
        if (days.HasFlag(DaysOfWeek.Thu)) names.Add("Thu");
        if (days.HasFlag(DaysOfWeek.Fri)) names.Add("Fri");
        if (days.HasFlag(DaysOfWeek.Sat)) names.Add("Sat");
        if (days.HasFlag(DaysOfWeek.Sun)) names.Add("Sun");

        return names.Count > 0 ? string.Join(", ", names) : "None";
    }
}
