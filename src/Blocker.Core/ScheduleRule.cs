using System;

namespace Blocker.Core;

[Flags]
public enum DaysOfWeek
{
    None = 0,
    Mon = 1,
    Tue = 2,
    Wed = 4,
    Thu = 8,
    Fri = 16,
    Sat = 32,
    Sun = 64,
    All = Mon | Tue | Wed | Thu | Fri | Sat | Sun,
    Weekdays = Mon | Tue | Wed | Thu | Fri,
    Weekend = Sat | Sun
}

public class ScheduleRule
{
    public DaysOfWeek Days { get; set; } = DaysOfWeek.None;
    public int StartMinute { get; set; } // 0 - 1439
    public int EndMinute { get; set; }   // 0 - 1439

    public bool CrossesMidnight => EndMinute < StartMinute;

    public bool IsValid =>
        StartMinute >= 0 && StartMinute < 1440 &&
        EndMinute >= 0 && EndMinute < 1440 &&
        StartMinute != EndMinute &&
        Days != DaysOfWeek.None;

    public static DaysOfWeek FromDayOfWeek(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => DaysOfWeek.Mon,
        DayOfWeek.Tuesday => DaysOfWeek.Tue,
        DayOfWeek.Wednesday => DaysOfWeek.Wed,
        DayOfWeek.Thursday => DaysOfWeek.Thu,
        DayOfWeek.Friday => DaysOfWeek.Fri,
        DayOfWeek.Saturday => DaysOfWeek.Sat,
        DayOfWeek.Sunday => DaysOfWeek.Sun,
        _ => DaysOfWeek.None
    };

    public static DayOfWeek ToDayOfWeek(DaysOfWeek day) => day switch
    {
        DaysOfWeek.Mon => DayOfWeek.Monday,
        DaysOfWeek.Tue => DayOfWeek.Tuesday,
        DaysOfWeek.Wed => DayOfWeek.Wednesday,
        DaysOfWeek.Thu => DayOfWeek.Thursday,
        DaysOfWeek.Fri => DayOfWeek.Friday,
        DaysOfWeek.Sat => DayOfWeek.Saturday,
        DaysOfWeek.Sun => DayOfWeek.Sunday,
        _ => DayOfWeek.Monday
    };
}
