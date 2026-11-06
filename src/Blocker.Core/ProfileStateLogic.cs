using System;
using System.Collections.Generic;
using System.Linq;

namespace Blocker.Core;

public static class ProfileStateLogic
{
    public static bool IsProfileActive(Profile profile, BlockSession? activeSession, DateTime localNow, out string activeReason)
    {
        if (activeSession != null && activeSession.ProfileId == profile.Id)
        {
            activeReason = "Manual";
            return true;
        }

        if (profile.Schedule != null && profile.Schedule.Count > 0 && ScheduleLogic.IsActive(profile.Schedule, localNow))
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

        if (profile.Schedule != null && profile.Schedule.Count > 0 && ScheduleLogic.IsActive(profile.Schedule, localNow))
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

        if (profile.Schedule != null && profile.Schedule.Count > 0 && ScheduleLogic.IsActive(profile.Schedule, localNow))
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
