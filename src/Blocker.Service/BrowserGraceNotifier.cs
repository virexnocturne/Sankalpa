using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Blocker.Core;

namespace Blocker.Service;

public class BrowserGraceState
{
    public string ExeName { get; set; } = string.Empty;
    public DateTime FirstSeenGraceUtc { get; set; } = DateTime.UtcNow;
    public bool InitialNotified { get; set; }
    public bool IntermediateNotified { get; set; }
    public bool FinalNotified { get; set; }
    public string? LastMilestone { get; set; }
}

/// <summary>
/// Manages truthful BrowserGrace notifications and Action Center toast lifecycle.
/// Enforces a 4-second settle delay before the first countdown warning without altering the 60-second kill deadline.
/// Verifies USABLE window presence (visible, >= 100x100, in pinned root) to eliminate fake notifications on closed browsers.
/// </summary>
public static class BrowserGraceNotifier
{
    /// <summary>
    /// Settle delay before the initial warning is eligible to dispatch.
    /// Single-source constant per Shared Contract C-5.
    /// </summary>
    public const int SettleDelaySeconds = 4;

    public const string ToastGroup = "Sankalpa.BrowserGrace";

    private static readonly ConcurrentDictionary<string, BrowserGraceState> _activeStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resets all internal tracking state. Used for clean service restarts and unit tests.
    /// </summary>
    public static void Reset()
    {
        _activeStates.Clear();
    }

    /// <summary>
    /// Returns whether a given browser is currently tracked in grace.
    /// </summary>
    public static bool IsTrackedInGrace(string browserExe)
    {
        string norm = NormalizeExe(browserExe);
        return _activeStates.ContainsKey(norm);
    }

    /// <summary>
    /// Processes fallback plan results for browsers currently in their grace period.
    /// Handles settle delay, USABLE window validation, milestone notifications, and toast retractions.
    /// </summary>
    public static void ProcessGraceNotifications(FallbackPlanResult fallbackPlan, IBlockStore db)
    {
        if (fallbackPlan == null) return;

        var activeExes = new HashSet<string>(
            fallbackPlan.GracePeriodBrowsers.Select(NormalizeExe),
            StringComparer.OrdinalIgnoreCase);

        // 1. Detect and retract notifications for browsers that are no longer in grace (closed, killed, or healthy)
        var trackedExes = _activeStates.Keys.ToList();
        foreach (var trackedExe in trackedExes)
        {
            if (!activeExes.Contains(trackedExe))
            {
                if (_activeStates.TryRemove(trackedExe, out _))
                {
                    ServiceNotificationDispatcher.RetractNotification(tag: trackedExe, group: ToastGroup);
                }
            }
        }

        // 2. Process each browser currently in grace
        foreach (var gb in fallbackPlan.GracePeriodBrowsers)
        {
            string gExe = NormalizeExe(gb);
            int remSec = fallbackPlan.GracePeriodRemainingSeconds.TryGetValue(gExe, out int s) ? s : 60;

            var state = _activeStates.GetOrAdd(gExe, exe => new BrowserGraceState
            {
                ExeName = exe,
                FirstSeenGraceUtc = DateTime.UtcNow
            });

            // Calculate elapsed time in grace.
            // Support both wall-clock time and virtual/test monotonic clock countdowns.
            double wallClockElapsedSec = (DateTime.UtcNow - state.FirstSeenGraceUtc).TotalSeconds;
            int clockElapsedSec = Math.Max(0, 60 - remSec);
            double effectiveElapsedSec = Math.Max(wallClockElapsedSec, clockElapsedSec);

            // Settle delay rule: wait for continuous unhealthiness >= SettleDelaySeconds before the first warning.
            // Kill deadline is unchanged at t=60s.
            if (effectiveElapsedSec < SettleDelaySeconds)
            {
                // In settle delay: do not dispatch toast yet
                continue;
            }

            // USABLE window verification:
            // Never show a countdown for a browser that is not RUNNING with a USABLE window (>= 100x100, visible, pinned root).
            if (!WindowsNotificationHelper.HasUsableWindow(gExe))
            {
                // Background process surviving without a usable window: retract any existing toast and suppress
                ServiceNotificationDispatcher.RetractNotification(tag: gExe, group: ToastGroup);
                continue;
            }

            // Determine milestone:
            // - Initial warning: >= 4s continuous unhealthiness (60s-31s remaining)
            // - Intermediate warning: <= 30s remaining
            // - Final warning: <= 10s remaining
            string milestone;
            bool shouldDispatch = false;

            if (remSec <= 10)
            {
                milestone = "final";
                if (!state.FinalNotified)
                {
                    state.FinalNotified = true;
                    shouldDispatch = true;
                }
            }
            else if (remSec <= 30)
            {
                milestone = "intermediate";
                if (!state.IntermediateNotified)
                {
                    state.IntermediateNotified = true;
                    shouldDispatch = true;
                }
            }
            else
            {
                milestone = "initial";
                if (!state.InitialNotified)
                {
                    state.InitialNotified = true;
                    shouldDispatch = true;
                }
            }

            if (shouldDispatch)
            {
                state.LastMilestone = milestone;
                string dispName = BrowserCatalog.GetDisplayName(gExe);
                string graceMsg = $"Extension required. {dispName} will close in {remSec}s unless Sankalpa extension is active and 'Allow in InPrivate' is enabled.";

                bool dispatched = ServiceNotificationDispatcher.DispatchNotification(
                    kind: "BrowserGrace",
                    name: gExe,
                    profileOrReason: graceMsg,
                    tag: gExe,
                    group: ToastGroup,
                    expirationSeconds: remSec + 2
                );

                db.RecordBlockEvent(new BlockEvent
                {
                    TimeUtc = DateTime.UtcNow,
                    Kind = "BrowserGrace",
                    Name = gExe,
                    ProfileName = graceMsg,
                    ServiceDispatched = true // Handled by service notification pipeline; avoids duplicate UI balloon
                });
            }
        }
    }

    private static string NormalizeExe(string processName)
    {
        string norm = (processName ?? string.Empty).Trim();
        if (!norm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            norm += ".exe";
        }
        return norm;
    }
}
