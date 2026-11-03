namespace Blocker.Core;

public static class BlockSessionLogic
{
    public static bool IsActive(BlockSession? session, DateTime nowUtc)
    {
        if (session == null)
        {
            return false;
        }

        if (session.EndedUtc != null)
        {
            return false;
        }

        if (session.DurationSeconds <= 0)
        {
            return false;
        }

        DateTime endUtc = session.StartUtc.AddSeconds(session.DurationSeconds);
        return nowUtc >= session.StartUtc && nowUtc < endUtc;
    }

    public static TimeSpan GetRemainingTime(BlockSession? session, DateTime nowUtc)
    {
        if (session == null || !IsActive(session, nowUtc))
        {
            return TimeSpan.Zero;
        }

        DateTime endUtc = session.StartUtc.AddSeconds(session.DurationSeconds);
        TimeSpan remaining = endUtc - nowUtc;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Checks whether the session has genuinely expired based on wall-clock time (nowUtc >= StartUtc + DurationSeconds).
    /// </summary>
    public static bool IsExpired(BlockSession? session, DateTime nowUtc)
    {
        if (session == null)
        {
            return false;
        }

        if (session.EndedUtc != null)
        {
            return true;
        }

        if (session.DurationSeconds <= 0)
        {
            return true;
        }

        return nowUtc >= session.StartUtc && nowUtc >= session.StartUtc.AddSeconds(session.DurationSeconds);
    }

    /// <summary>
    /// Checks whether the current system clock is before the session start time, indicating backwards clock tampering.
    /// </summary>
    public static bool HasClockMovedBackwards(BlockSession? session, DateTime nowUtc)
    {
        if (session == null)
        {
            return false;
        }

        return nowUtc < session.StartUtc;
    }

    public static (bool CanStop, TimeSpan RemainingTime, string Reason) EvaluateStopRequest(
        BlockSession? session,
        DateTime nowUtc,
        bool emergencyUnlockPresent)
    {
        if (session == null || !IsActive(session, nowUtc))
        {
            return (true, TimeSpan.Zero, "No active session.");
        }

        TimeSpan remaining = GetRemainingTime(session, nowUtc);

        // DEV-ONLY: If emergency unlock is present, allow stopping even locked sessions
        if (emergencyUnlockPresent)
        {
            return (true, remaining, "Emergency unlock override applied.");
        }

        if (session.Locked)
        {
            string formattedRemaining = UiLogic.FormatCountdown(remaining);
            return (false, remaining, $"Session is locked. Remaining time: {formattedRemaining} ({remaining.TotalSeconds:F0}s remaining).");
        }

        return (true, remaining, "Session is unlocked and can be stopped.");
    }
}
