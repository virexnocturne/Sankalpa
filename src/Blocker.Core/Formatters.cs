using System;
using System.Globalization;

namespace Blocker.Core;

public static class DurationFormatter
{
    /// <summary>
    /// Formats a duration into standard user-facing strings:
    /// "58 min", "2 h 14 min", "1 hour", "2 hours", "1 day", "3 days", etc.
    /// </summary>
    public static string Format(TimeSpan timeSpan)
    {
        if (timeSpan < TimeSpan.Zero)
        {
            timeSpan = TimeSpan.Zero;
        }

        int totalDays = (int)timeSpan.TotalDays;
        int hours = timeSpan.Hours;
        int minutes = timeSpan.Minutes;

        if (totalDays >= 1)
        {
            if (hours == 0 && minutes == 0)
            {
                return totalDays == 1 ? "1 day" : $"{totalDays} days";
            }
            if (hours > 0 && minutes == 0)
            {
                string dayPart = totalDays == 1 ? "1 day" : $"{totalDays} days";
                return $"{dayPart} {hours} h";
            }
            if (hours > 0 && minutes > 0)
            {
                string dayPart = totalDays == 1 ? "1 day" : $"{totalDays} days";
                return $"{dayPart} {hours} h {minutes} min";
            }
            string dayOnly = totalDays == 1 ? "1 day" : $"{totalDays} days";
            return $"{dayOnly} {minutes} min";
        }

        int totalHours = (int)timeSpan.TotalHours;
        if (totalHours >= 1)
        {
            if (minutes == 0)
            {
                return totalHours == 1 ? "1 hour" : $"{totalHours} hours";
            }
            return $"{totalHours} h {minutes} min";
        }

        return $"{minutes} min";
    }

    public static string FormatMinutes(int minutes)
    {
        return Format(TimeSpan.FromMinutes(minutes));
    }

    /// <summary>
    /// Formats a remaining duration in countdown format:
    /// "mm:ss" when under 1 hour (e.g. "24:59", "00:05"),
    /// or "hh:mm:ss" / "h:mm:ss" when 1 hour or more.
    /// </summary>
    public static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        int totalHours = (int)remaining.TotalHours;
        if (totalHours >= 1)
        {
            return $"{(totalHours < 10 ? "0" : "")}{totalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
        }

        return $"{remaining.Minutes:D2}:{remaining.Seconds:D2}";
    }

    /// <summary>
    /// Formats duration as a hyphenated adjective for session messages:
    /// e.g. "1-minute", "25-minute", "1-hour", "2-hour", "1-day", "3-day".
    /// </summary>
    public static string FormatAdjective(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        int totalDays = (int)duration.TotalDays;
        if (totalDays >= 1 && duration.Hours == 0 && duration.Minutes == 0)
        {
            return $"{totalDays}-day";
        }

        int totalHours = (int)duration.TotalHours;
        if (totalHours >= 1 && duration.Minutes == 0)
        {
            return $"{totalHours}-hour";
        }

        int totalMinutes = (int)Math.Round(duration.TotalMinutes);
        return $"{totalMinutes}-minute";
    }

    public static string FormatAdjectiveMinutes(int minutes)
    {
        return FormatAdjective(TimeSpan.FromMinutes(minutes));
    }
}

public static class TimeFormatter
{
    /// <summary>
    /// Formats a time following the 12-hour or 24-hour setting.
    /// When the date is today, outputs "Today, 3:40 PM" or "Today, 15:40".
    /// </summary>
    public static string Format(DateTime targetTime, bool is24Hour, DateTime? referenceNow = null)
    {
        DateTime now = referenceNow ?? DateTime.Now;
        string timePart = FormatTimeOnly(targetTime, is24Hour);

        if (targetTime.Date == now.Date)
        {
            return $"Today, {timePart}";
        }

        if (targetTime.Date == now.Date.AddDays(1))
        {
            return $"Tomorrow, {timePart}";
        }

        string datePart = targetTime.ToString("MMM d", CultureInfo.InvariantCulture);
        return $"{datePart}, {timePart}";
    }

    public static string FormatTimeOnly(DateTime targetTime, bool is24Hour)
    {
        if (is24Hour)
        {
            return targetTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        return targetTime.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    public static string FormatTimeOfDay(TimeSpan timeOfDay, bool is24Hour)
    {
        DateTime dt = DateTime.Today.Add(timeOfDay);
        return FormatTimeOnly(dt, is24Hour);
    }

    public static string FormatScheduleChip(string prefix, TimeSpan start, TimeSpan end, bool is24Hour)
    {
        string s = FormatTimeOfDay(start, is24Hour);
        string e = FormatTimeOfDay(end, is24Hour);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return $"{s} - {e}";
        }
        return $"{prefix} {s} - {e}";
    }
}
