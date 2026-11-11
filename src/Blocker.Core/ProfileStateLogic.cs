using System;
using System.Collections.Generic;
using System.Linq;

namespace Blocker.Core;

public static class ProfileStateLogic
{
    private static DateTime ToUtcTimestamp(DateTime dt)
    {
        return dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime()
        };
    }

    public static bool IsScheduleBypassed(Profile profile, DateTime localNow)
    {
        if (profile.ScheduleBypassedUntilUtc == null)
        {
            return false;
        }

        DateTime nowUtc = ToUtcTimestamp(localNow);
        DateTime bypassUtc = ToUtcTimestamp(profile.ScheduleBypassedUntilUtc.Value);
        return nowUtc < bypassUtc;
    }

    public static bool IsScheduleActive(Profile profile, DateTime localNow)
    {
        if (profile.Schedule == null || profile.Schedule.Count == 0)
        {
            return false;
        }

        if (IsScheduleBypassed(profile, localNow))
        {
            return false;
        }

        return ScheduleLogic.IsActive(profile.Schedule, localNow);
    }

    public static DateTime? ComputeScheduleBypassUntilUtc(List<ScheduleRule>? schedule, DateTime localNow)
    {
        if (schedule == null || schedule.Count == 0 || !ScheduleLogic.IsActive(schedule, localNow))
        {
            return null;
        }

        DateTime nowUtc = ToUtcTimestamp(localNow);
        DateTime? nextChange = ScheduleLogic.NextChange(schedule, localNow);
        if (nextChange.HasValue)
        {
            DateTime untilUtc = ToUtcTimestamp(nextChange.Value);
            return untilUtc > nowUtc ? untilUtc : nowUtc.AddMinutes(1);
        }

        // Unbounded (e.g., 24/7) schedule has no inactive transition: bypass for 24h while preserving rules.
        return nowUtc.AddHours(24);
    }

    public static DateTime? GetEffectiveNextChange(Profile profile, DateTime localNow)
    {
        if (profile.Schedule == null || profile.Schedule.Count == 0)
        {
            return null;
        }

        if (!IsScheduleBypassed(profile, localNow))
        {
            return ScheduleLogic.NextChange(profile.Schedule, localNow);
        }

        DateTime bypassLocal = ToUtcTimestamp(profile.ScheduleBypassedUntilUtc!.Value).ToLocalTime();
        return ScheduleLogic.NextChange(profile.Schedule, bypassLocal) ?? bypassLocal;
    }

    public static bool IsProfileActive(Profile profile, BlockSession? activeSession, DateTime localNow, out string activeReason)
    {
        if (activeSession != null && activeSession.ProfileId == profile.Id)
        {
            activeReason = "Manual";
            return true;
        }

        if (IsScheduleActive(profile, localNow))
        {
            activeReason = "Schedule";
            return true;
        }

        if (!profile.Enabled)
        {
            activeReason = "None";
            return false;
        }

        activeReason = "AlwaysOn";
        return true;
    }

    public static bool IsProfileActiveWithSessions(Profile profile, IEnumerable<BlockSession>? activeSessions, DateTime localNow, out string activeReason)
    {
        if (activeSessions != null && activeSessions.Any(s => s.ProfileId == profile.Id))
        {
            activeReason = "Manual";
            return true;
        }

        return IsProfileActive(profile, (BlockSession?)null, localNow, out activeReason);
    }

    public static bool IsProfileLockedNow(Profile profile, BlockSession? activeSession, DateTime localNow)
    {
        if (activeSession != null && activeSession.ProfileId == profile.Id && activeSession.Locked)
        {
            return true;
        }

        if (IsScheduleActive(profile, localNow))
        {
            return true;
        }

        if (profile.LockMode == LockMode.None)
        {
            return false;
        }

        return IsProfileActive(profile, activeSession, localNow, out _);
    }

    public static bool IsProfileLockedNowWithSessions(Profile profile, IEnumerable<BlockSession>? activeSessions, DateTime localNow)
    {
        var session = activeSessions?.FirstOrDefault(s => s.ProfileId == profile.Id);
        if (session != null && session.Locked)
        {
            return true;
        }

        if (IsScheduleActive(profile, localNow))
        {
            return true;
        }

        if (profile.LockMode == LockMode.None)
        {
            return false;
        }

        return IsProfileActiveWithSessions(profile, activeSessions, localNow, out _);
    }

    public static (List<string> Apps, List<string> Domains) ComputeActiveUnion(
        IEnumerable<Profile> profiles,
        BlockSession? activeSession,
        DateTime localNow)
    {
        var activeProfiles = profiles.Where(p => IsProfileActive(p, activeSession, localNow, out _)).ToList();

        var apps = activeProfiles
            .SelectMany(p => p.BlockedProcesses)
            .Select(AppPickerLogic.NormalizeExeName)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var domains = activeProfiles
            .SelectMany(p => p.BlockedDomains)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (apps, domains);
    }

    public static (List<string> Apps, List<string> Domains) ComputeActiveUnionWithSessions(
        IEnumerable<Profile> profiles,
        IEnumerable<BlockSession>? activeSessions,
        DateTime localNow)
    {
        var activeProfiles = profiles.Where(p => IsProfileActiveWithSessions(p, activeSessions, localNow, out _)).ToList();

        var apps = activeProfiles
            .SelectMany(p => p.BlockedProcesses)
            .Select(AppPickerLogic.NormalizeExeName)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var domains = activeProfiles
            .SelectMany(p => p.BlockedDomains)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (apps, domains);
    }
}
