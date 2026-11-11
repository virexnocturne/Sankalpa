using System;
using System.Collections.Generic;
using System.Threading;

namespace Blocker.Core;

/// <summary>
/// Filters WinEvent callbacks to the exact five window events required by AllowedAppForegroundTracker.
/// </summary>
public static class AllowedWinEventFilter
{
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const int OBJID_WINDOW = 0;

    /// <summary>
    /// Exact set of WinEvent IDs registered with SetWinEventHook (one hook per event ID).
    /// </summary>
    public static readonly IReadOnlyList<uint> HookedEventTypes = new[]
    {
        EVENT_SYSTEM_FOREGROUND,
        EVENT_SYSTEM_MINIMIZESTART,
        EVENT_SYSTEM_MINIMIZEEND,
        EVENT_OBJECT_DESTROY,
        EVENT_OBJECT_HIDE
    };

    /// <summary>
    /// Returns true only for the five intended WinEvent types.
    /// Rejects menu, capture, move/size, dialog, scrolling, switch, and OBJECT_SHOW events.
    /// </summary>
    public static bool IsAllowedEventType(uint eventType) =>
        eventType == EVENT_SYSTEM_FOREGROUND ||
        eventType == EVENT_SYSTEM_MINIMIZESTART ||
        eventType == EVENT_SYSTEM_MINIMIZEEND ||
        eventType == EVENT_OBJECT_DESTROY ||
        eventType == EVENT_OBJECT_HIDE;

    /// <summary>
    /// Validates event type first, then object ID, non-zero HWND, and tracked HWND match for destroy/hide.
    /// </summary>
    public static bool ShouldProcess(uint eventType, int idObject, IntPtr hwnd, IntPtr trackedHwnd)
    {
        if (!IsAllowedEventType(eventType))
        {
            return false;
        }

        if (idObject != OBJID_WINDOW && eventType != EVENT_SYSTEM_FOREGROUND)
        {
            return false;
        }

        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if ((eventType == EVENT_OBJECT_DESTROY || eventType == EVENT_OBJECT_HIDE) && hwnd != trackedHwnd)
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// Lock-free, single-pending-flag coalescer with guaranteed trailing-edge evaluation.
/// Collapses bursts of WinEvent triggers into one asynchronous dispatcher evaluation while
/// guaranteeing that any trigger arriving mid-evaluation schedules a follow-up trailing evaluation.
/// </summary>
public sealed class TrailingEdgeCoalescer : IDisposable
{
    private const int StateIdle = 0;
    private const int StateScheduled = 1;
    private const int StateRunning = 2;
    private const int StateRunningAndPending = 3;

    private readonly Action _evaluate;
    private readonly Action<Action> _post;
    private int _state;
    private int _disposed;

    public TrailingEdgeCoalescer(Action evaluate, Action<Action> post)
    {
        _evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
        _post = post ?? throw new ArgumentNullException(nameof(post));
    }

    /// <summary>
    /// Requests an asynchronous evaluation. Collapses redundant requests while queued,
    /// and marks a trailing re-run if requested while already executing.
    /// </summary>
    public void Request()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        while (true)
        {
            int current = Volatile.Read(ref _state);
            if (current == StateScheduled || current == StateRunningAndPending)
            {
                return;
            }

            if (current == StateIdle)
            {
                if (Interlocked.CompareExchange(ref _state, StateScheduled, StateIdle) == StateIdle)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        Interlocked.CompareExchange(ref _state, StateIdle, StateScheduled);
                        return;
                    }
                    _post(ExecuteScheduled);
                    return;
                }
            }
            else if (current == StateRunning)
            {
                if (Interlocked.CompareExchange(ref _state, StateRunningAndPending, StateRunning) == StateRunning)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Dispatcher callback entrypoint for queued coalesced evaluations.
    /// </summary>
    public void ExecuteScheduled()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.CompareExchange(ref _state, StateIdle, StateScheduled);
            return;
        }

        if (Interlocked.CompareExchange(ref _state, StateRunning, StateScheduled) != StateScheduled)
        {
            return;
        }

        RunAndScheduleTrailing();
    }

    /// <summary>
    /// Runs evaluation immediately (e.g., from debounce/watchdog timer), consuming any queued
    /// scheduled run and guaranteeing trailing re-run if a new request arrives during execution.
    /// </summary>
    public void RunDirect()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        while (true)
        {
            int current = Volatile.Read(ref _state);
            if (current == StateRunning || current == StateRunningAndPending)
            {
                if (Interlocked.CompareExchange(ref _state, StateRunningAndPending, StateRunning) == StateRunning ||
                    Volatile.Read(ref _state) == StateRunningAndPending)
                {
                    return;
                }
                continue;
            }

            if (Interlocked.CompareExchange(ref _state, StateRunning, current) == current)
            {
                break;
            }
        }

        RunAndScheduleTrailing();
    }

    private void RunAndScheduleTrailing()
    {
        try
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _evaluate();
            }
        }
        finally
        {
            while (true)
            {
                int current = Volatile.Read(ref _state);
                if (current == StateRunning)
                {
                    if (Interlocked.CompareExchange(ref _state, StateIdle, StateRunning) == StateRunning)
                    {
                        break;
                    }
                }
                else if (current == StateRunningAndPending)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        if (Interlocked.CompareExchange(ref _state, StateIdle, StateRunningAndPending) == StateRunningAndPending)
                        {
                            break;
                        }
                    }
                    else if (Interlocked.CompareExchange(ref _state, StateScheduled, StateRunningAndPending) == StateRunningAndPending)
                    {
                        _post(ExecuteScheduled);
                        break;
                    }
                }
                else
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Cancels any pending scheduled evaluation before an explicit mode transition.
    /// </summary>
    public void CancelPending()
    {
        while (true)
        {
            int current = Volatile.Read(ref _state);
            if (current == StateScheduled)
            {
                if (Interlocked.CompareExchange(ref _state, StateIdle, StateScheduled) == StateScheduled)
                {
                    return;
                }
            }
            else if (current == StateRunningAndPending)
            {
                if (Interlocked.CompareExchange(ref _state, StateRunning, StateRunningAndPending) == StateRunningAndPending)
                {
                    return;
                }
            }
            else
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, 1);
        CancelPending();
    }
}
