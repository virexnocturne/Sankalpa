using System;
using System.Collections.Generic;

namespace Blocker.Core;

/// <summary>
/// Mutable state representing the active mini-HUD session and tracked application window.
/// </summary>
public class HudSessionState
{
    public bool IsAllowedAppMode { get; set; }
    public IntPtr TrackedHwnd { get; set; } = IntPtr.Zero;
    public int TrackedPid { get; set; }
    public string? TrackedProcessName { get; set; }
    public bool IsLaunching { get; set; }
    public DateTime LaunchGraceUntilUtc { get; set; } = DateTime.MinValue;
    public DateTime? WindowLostSinceUtc { get; set; }

    public void ResetToZen()
    {
        IsAllowedAppMode = false;
        TrackedHwnd = IntPtr.Zero;
        TrackedPid = 0;
        TrackedProcessName = null;
        IsLaunching = false;
        LaunchGraceUntilUtc = DateTime.MinValue;
        WindowLostSinceUtc = null;
    }
}

/// <summary>
/// Immutable snapshot of window attributes used for evaluating HUD visibility and targeting.
/// </summary>
public record WindowSnapshot(
    IntPtr Hwnd,
    int Pid,
    string? ProcessName,
    string? ImagePath,
    string? ClassName,
    int Width,
    int Height,
    bool IsWindow,
    bool IsVisible,
    bool IsIconic,
    bool HasTitle);

/// <summary>
/// Evaluation result specifying actions for the HUD and Zen screen.
/// </summary>
public record HudEvaluationResult(
    bool ShouldShowHud,
    bool ShouldRestoreZenScreen,
    bool ShouldRetarget,
    IntPtr NewTrackedHwnd,
    int NewTrackedPid,
    string? NewTrackedProcessName,
    string Reason);

/// <summary>
/// Pure decision logic governing whether the Mini-HUD should be visible, hidden, or retargeted,
/// and whether the Zen shield kiosk should be restored.
/// </summary>
public static class HudVisibilityLogic
{
    public static HudEvaluationResult Evaluate(
        HudSessionState state,
        WindowSnapshot? foreground,
        WindowSnapshot? tracked,
        DateTime nowUtc,
        IReadOnlyCollection<string> allowedProcessNames,
        IReadOnlyDictionary<string, string>? allowedRoots,
        int ownPid,
        int debounceMs = ZenLaunchConstants.DefaultDebounceMs)
    {
        // 1. If session is not in allowed-app mode, HUD is always hidden and shield remains in control
        if (!state.IsAllowedAppMode)
        {
            state.WindowLostSinceUtc = null;
            return new HudEvaluationResult(
                ShouldShowHud: false,
                ShouldRestoreZenScreen: false,
                ShouldRetarget: false,
                NewTrackedHwnd: IntPtr.Zero,
                NewTrackedPid: 0,
                NewTrackedProcessName: null,
                Reason: "Session is not in allowed-app mode");
        }

        // 2. Active cold launch in progress
        if (state.IsLaunching)
        {
            bool hasValidTracked = tracked != null && tracked.Hwnd != IntPtr.Zero && tracked.IsWindow && tracked.IsVisible && !tracked.IsIconic;
            if (hasValidTracked)
            {
                // Discovered tracked window!
                state.IsLaunching = false;
                state.WindowLostSinceUtc = null;
            }
            else
            {
                // Window not yet ready or launch failed
                if (nowUtc >= state.LaunchGraceUntilUtc)
                {
                    // Cold launch timed out or failed -> fail closed, restore Zen screen, no orphan HUD
                    state.ResetToZen();
                    return new HudEvaluationResult(
                        ShouldShowHud: false,
                        ShouldRestoreZenScreen: true,
                        ShouldRetarget: false,
                        NewTrackedHwnd: IntPtr.Zero,
                        NewTrackedPid: 0,
                        NewTrackedProcessName: null,
                        Reason: "Cold launch grace period expired without a valid window");
                }

                // Still within grace period, keep HUD hidden until window appears
                return new HudEvaluationResult(
                    ShouldShowHud: false,
                    ShouldRestoreZenScreen: false,
                    ShouldRetarget: false,
                    NewTrackedHwnd: IntPtr.Zero,
                    NewTrackedPid: 0,
                    NewTrackedProcessName: null,
                    Reason: "Launching: waiting for window discovery");
            }
        }

        // 3. Check if foreground window is another real allowed application window (e.g. Alt+Tab between allowed apps)
        if (foreground != null &&
            foreground.Hwnd != IntPtr.Zero &&
            foreground.IsWindow &&
            foreground.IsVisible &&
            !foreground.IsIconic &&
            ZenLaunchMatcher.IsRealAppWindow(foreground.ClassName, foreground.Width, foreground.Height, foreground.IsIconic, foreground.HasTitle) &&
            ZenLaunchMatcher.IsAppAllowed(foreground.ProcessName, foreground.ImagePath, allowedProcessNames, allowedRoots))
        {
            if (foreground.Hwnd != state.TrackedHwnd)
            {
                // Retarget to new allowed foreground window!
                state.TrackedHwnd = foreground.Hwnd;
                state.TrackedPid = foreground.Pid;
                state.TrackedProcessName = foreground.ProcessName;
                state.WindowLostSinceUtc = null;

                return new HudEvaluationResult(
                    ShouldShowHud: true,
                    ShouldRestoreZenScreen: false,
                    ShouldRetarget: true,
                    NewTrackedHwnd: foreground.Hwnd,
                    NewTrackedPid: foreground.Pid,
                    NewTrackedProcessName: foreground.ProcessName,
                    Reason: "Retargeted to new foreground allowed app window");
            }
        }

        // 4. Verify status of the tracked allowed window
        bool trackedValid = tracked != null &&
                            tracked.Hwnd == state.TrackedHwnd &&
                            tracked.IsWindow &&
                            tracked.IsVisible &&
                            !tracked.IsIconic;

        if (!trackedValid)
        {
            // Tracked window was minimized, hidden, destroyed, or lost
            if (state.WindowLostSinceUtc == null)
            {
                state.WindowLostSinceUtc = nowUtc;
            }

            double elapsedMs = (nowUtc - state.WindowLostSinceUtc.Value).TotalMilliseconds;
            if (elapsedMs >= debounceMs)
            {
                // Debounce expired: fail-closed, restore Zen shield
                state.ResetToZen();
                return new HudEvaluationResult(
                    ShouldShowHud: false,
                    ShouldRestoreZenScreen: true,
                    ShouldRetarget: false,
                    NewTrackedHwnd: IntPtr.Zero,
                    NewTrackedPid: 0,
                    NewTrackedProcessName: null,
                    Reason: $"Tracked allowed window lost for {elapsedMs:F0}ms (>= debounce {debounceMs}ms)");
            }

            // Within debounce window: hide HUD immediately so it does not float orphaned
            return new HudEvaluationResult(
                ShouldShowHud: false,
                ShouldRestoreZenScreen: false,
                ShouldRetarget: false,
                NewTrackedHwnd: state.TrackedHwnd,
                NewTrackedPid: state.TrackedPid,
                NewTrackedProcessName: state.TrackedProcessName,
                Reason: "Tracked window lost, debouncing before restoring Zen screen");
        }

        // 5. Tracked window is valid. Now evaluate the foreground window.
        // Case 5a: Tracked window is directly in the foreground
        if (foreground != null && foreground.Hwnd == state.TrackedHwnd)
        {
            state.WindowLostSinceUtc = null;
            return new HudEvaluationResult(
                ShouldShowHud: true,
                ShouldRestoreZenScreen: false,
                ShouldRetarget: false,
                NewTrackedHwnd: state.TrackedHwnd,
                NewTrackedPid: state.TrackedPid,
                NewTrackedProcessName: state.TrackedProcessName,
                Reason: "Tracked allowed window is in foreground");
        }

        // Case 5b: Foreground is a neutral window (our own HUD/shield/dialog, or trusted OS helper)
        bool isNeutral = foreground != null && ZenLaunchMatcher.IsNeutralWindow(
            isOwnProcess: foreground.Pid == ownPid,
            imagePath: foreground.ImagePath,
            processName: foreground.ProcessName);

        if (isNeutral)
        {
            state.WindowLostSinceUtc = null;
            return new HudEvaluationResult(
                ShouldShowHud: true,
                ShouldRestoreZenScreen: false,
                ShouldRetarget: false,
                NewTrackedHwnd: state.TrackedHwnd,
                NewTrackedPid: state.TrackedPid,
                NewTrackedProcessName: state.TrackedProcessName,
                Reason: "Focus is on a neutral window (HUD, shield dialog, or trusted helper)");
        }

        // Case 5c: Foreground is an unauthorized window or unknown window
        if (state.WindowLostSinceUtc == null)
        {
            state.WindowLostSinceUtc = nowUtc;
        }

        double breachElapsedMs = (nowUtc - state.WindowLostSinceUtc.Value).TotalMilliseconds;
        if (breachElapsedMs >= debounceMs)
        {
            state.ResetToZen();
            return new HudEvaluationResult(
                ShouldShowHud: false,
                ShouldRestoreZenScreen: true,
                ShouldRetarget: false,
                NewTrackedHwnd: IntPtr.Zero,
                NewTrackedPid: 0,
                NewTrackedProcessName: null,
                Reason: $"Unauthorized window in foreground for {breachElapsedMs:F0}ms (>= debounce {debounceMs}ms)");
        }

        return new HudEvaluationResult(
            ShouldShowHud: false,
            ShouldRestoreZenScreen: false,
            ShouldRetarget: false,
            NewTrackedHwnd: state.TrackedHwnd,
            NewTrackedPid: state.TrackedPid,
            NewTrackedProcessName: state.TrackedProcessName,
            Reason: "Unauthorized window in foreground, debouncing");
    }
}
