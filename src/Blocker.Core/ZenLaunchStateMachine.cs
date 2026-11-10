using System;

namespace Blocker.Core;

/// <summary>
/// Explicit state enumeration for Zen mode application launch and switch lifecycle.
/// </summary>
public enum ZenLaunchState
{
    Idle,
    Launching,
    Active,
    ReturnedToZen,
    Closing,
    Failed
}

/// <summary>
/// Pure, thread-safe state machine governing Zen Focus allowed application launches and transitions.
/// Enforces complete reset on all exit paths (cancel, error, timeout, window close, session end).
/// </summary>
public class ZenLaunchStateMachine
{
    private readonly object _syncRoot = new();

    public ZenLaunchState CurrentState { get; private set; } = ZenLaunchState.Idle;
    public string? CurrentAppName { get; private set; }
    public IntPtr ActiveHwnd { get; private set; } = IntPtr.Zero;
    public int ActivePid { get; private set; }
    public string? LastError { get; private set; }
    public DateTime StateEnteredUtc { get; private set; } = DateTime.UtcNow;

    public event Action<ZenLaunchState, ZenLaunchState>? StateChanged;

    public ZenLaunchStateMachine()
    {
    }

    /// <summary>
    /// Requests a launch or switch to the specified application.
    /// Transitions to Active immediately if a usable window exists (warm switch),
    /// or Launching if a process launch is required (cold launch).
    /// </summary>
    public bool RequestLaunch(string appName, bool hasUsableWindow, IntPtr? existingHwnd = null, int? existingPid = null)
    {
        if (string.IsNullOrWhiteSpace(appName)) return false;

        lock (_syncRoot)
        {
            if (CurrentState == ZenLaunchState.Closing) return false;

            CurrentAppName = appName;
            LastError = null;

            if (hasUsableWindow)
            {
                ActiveHwnd = existingHwnd ?? IntPtr.Zero;
                ActivePid = existingPid ?? 0;
                TransitionTo(ZenLaunchState.Active);
            }
            else
            {
                ActiveHwnd = IntPtr.Zero;
                ActivePid = 0;
                TransitionTo(ZenLaunchState.Launching);
            }
            return true;
        }
    }

    /// <summary>
    /// Discovered a USABLE top-level window for the launching or active application.
    /// </summary>
    public bool WindowDiscovered(IntPtr hwnd, int pid)
    {
        if (hwnd == IntPtr.Zero) return false;

        lock (_syncRoot)
        {
            if (CurrentState != ZenLaunchState.Launching && CurrentState != ZenLaunchState.Active)
            {
                return false;
            }

            ActiveHwnd = hwnd;
            ActivePid = pid;
            LastError = null;
            TransitionTo(ZenLaunchState.Active);
            return true;
        }
    }

    /// <summary>
    /// Reports a failure during process startup or window discovery.
    /// </summary>
    public void LaunchFailed(string reason)
    {
        lock (_syncRoot)
        {
            if (CurrentState == ZenLaunchState.Closing) return;

            LastError = reason;
            TransitionTo(ZenLaunchState.Failed);
        }
    }

    /// <summary>
    /// Reports that the discovery window search timed out.
    /// </summary>
    public void Timeout()
    {
        lock (_syncRoot)
        {
            if (CurrentState == ZenLaunchState.Launching)
            {
                LastError = "Discovery timed out";
                TransitionTo(ZenLaunchState.Failed);
            }
        }
    }

    /// <summary>
    /// User or watchdog requested Return to Zen screen.
    /// Cleans up active window tracking and enters ReturnedToZen.
    /// </summary>
    public void ReturnToZen()
    {
        lock (_syncRoot)
        {
            if (CurrentState == ZenLaunchState.Closing) return;

            ActiveHwnd = IntPtr.Zero;
            ActivePid = 0;
            TransitionTo(ZenLaunchState.ReturnedToZen);
        }
    }

    /// <summary>
    /// The active application window was closed or exited by the user.
    /// </summary>
    public void WindowClosed()
    {
        lock (_syncRoot)
        {
            if (CurrentState == ZenLaunchState.Active || CurrentState == ZenLaunchState.Launching)
            {
                ActiveHwnd = IntPtr.Zero;
                ActivePid = 0;
                TransitionTo(ZenLaunchState.ReturnedToZen);
            }
        }
    }

    /// <summary>
    /// Zen session is terminating.
    /// </summary>
    public void StartClosing()
    {
        lock (_syncRoot)
        {
            ActiveHwnd = IntPtr.Zero;
            ActivePid = 0;
            TransitionTo(ZenLaunchState.Closing);
        }
    }

    /// <summary>
    /// Complete reset to Idle state, clearing all application tracking and errors.
    /// </summary>
    public void Reset()
    {
        lock (_syncRoot)
        {
            CurrentAppName = null;
            ActiveHwnd = IntPtr.Zero;
            ActivePid = 0;
            LastError = null;
            TransitionTo(ZenLaunchState.Idle);
        }
    }

    private void TransitionTo(ZenLaunchState newState)
    {
        if (CurrentState == newState) return;

        var prev = CurrentState;
        CurrentState = newState;
        StateEnteredUtc = DateTime.UtcNow;

        LogStateTransition(prev, newState);
        StateChanged?.Invoke(prev, newState);
    }

    private void LogStateTransition(ZenLaunchState from, ZenLaunchState to)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string app = CurrentAppName ?? "None";
        string msg = $"[ZenLaunch] [{timestamp}] State changed: {from} -> {to} (App: '{app}', HWND: 0x{ActiveHwnd:X}, PID: {ActivePid})";
        System.Diagnostics.Debug.WriteLine(msg);
    }
}
