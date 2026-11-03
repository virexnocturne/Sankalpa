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

        if (!profile.Enabled)
        {
            activeReason = "None";
            return false;
        }

        if (profile.Schedule == null || profile.Schedule.Count == 0)
        {
            activeReason = "AlwaysOn";
            return true;
        }

        if (ScheduleLogic.IsActive(profile.Schedule, localNow))
        {
            activeReason = "Schedule";
            return true;
        }

        activeReason = "None";
        return false;
    }

    public static bool IsProfileLockedNow(Profile profile, BlockSession? activeSession, DateTime localNow)
    {
        if (profile.LockMode == LockMode.None)
        {
            return false;
        }

        return IsProfileActive(profile, activeSession, localNow, out _);
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
}
