using System;
using System.Collections.Generic;
using System.Threading;

namespace Blocker.Core;

/// <summary>
/// Dedicated single-thread background worker for <see cref="BackgroundAppThrottler"/>.
/// Keeps process enumeration, Core Audio COM detection, EcoQoS/memory throttling,
/// working-set trimming, and crash-journal disk I/O off the WPF UI thread using
/// latest-wins coalescing while keeping target-app priority restore synchronous
/// and cheap prior to window activation.
/// </summary>
public sealed class ThrottlerBackgroundWorker : IDisposable
{
    private readonly record struct LifecycleWorkItem(Action Work, ManualResetEventSlim? Completion);

    private static ThrottlerBackgroundWorker? _defaultInstance;
    private static readonly object _instanceLock = new();

    public static ThrottlerBackgroundWorker Default
    {
        get
        {
            if (_defaultInstance == null)
            {
                lock (_instanceLock)
                {
                    _defaultInstance ??= new ThrottlerBackgroundWorker(BackgroundAppThrottler.Default);
                }
            }
            return _defaultInstance;
        }
    }

    public static ThrottlerBackgroundWorker? DefaultIfCreated => Volatile.Read(ref _defaultInstance);

    private readonly BackgroundAppThrottler _throttler;
    private readonly object _queueLock = new();
    private readonly Queue<LifecycleWorkItem> _lifecycleQueue = new();
    private readonly Thread _workerThread;

    private bool _hasPendingForegroundUpdate;
    private int _latestForegroundPid;
    private IntPtr _latestForegroundHwnd;
    private bool _hasPendingTick;
    private bool _disposed;

    public ThrottlerBackgroundWorker(BackgroundAppThrottler throttler, string threadName = "Sankalpa.ThrottlerWorker")
    {
        _throttler = throttler ?? throw new ArgumentNullException(nameof(throttler));
        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = threadName
        };

        // Core Audio COM (MMDeviceEnumerator / IAudioSessionManager2) operates natively in MTA.
        if (OperatingSystem.IsWindows())
        {
            _workerThread.SetApartmentState(ApartmentState.MTA);
        }

        _workerThread.Start();
    }

    public BackgroundAppThrottler Throttler => _throttler;

    /// <summary>
    /// Enqueues session initialization (including crash-journal recovery) on the worker.
    /// Clears any stale pending foreground/tick work first.
    /// </summary>
    public void EnqueueStartSession(
        IReadOnlyList<string>? allowedApps,
        IReadOnlyDictionary<string, string>? allowedAppRoots = null)
    {
        var appsSnapshot = allowedApps != null ? new List<string>(allowedApps) : null;
        var rootsSnapshot = allowedAppRoots != null
            ? new Dictionary<string, string>(allowedAppRoots, StringComparer.OrdinalIgnoreCase)
            : null;

        EnqueueLifecycle(
            () => _throttler.StartSession(appsSnapshot, rootsSnapshot),
            clearPendingHotWork: true,
            waitForCompletion: false,
            boundedWaitMs: 0);
    }

    /// <summary>
    /// Synchronously and cheaply restores the target foreground app's already-throttled
    /// processes before window activation, then coalesces full process-tree/COM evaluation
    /// to the background worker (latest-wins).
    /// </summary>
    public void EnqueueForegroundUpdate(int foregroundPid, IntPtr foregroundHwnd = default)
    {
        _throttler.FastRestoreForegroundTarget(foregroundPid);

        lock (_queueLock)
        {
            if (_disposed) return;
            _hasPendingForegroundUpdate = true;
            _latestForegroundPid = foregroundPid;
            _latestForegroundHwnd = foregroundHwnd;
            Monitor.Pulse(_queueLock);
        }
    }

    /// <summary>
    /// Requests a background periodic tick (coalesced to at most one pending tick).
    /// </summary>
    public void EnqueueTick()
    {
        lock (_queueLock)
        {
            if (_disposed) return;
            _hasPendingTick = true;
            Monitor.Pulse(_queueLock);
        }
    }

    /// <summary>
    /// Ends the throttling session and restores all throttled processes off the UI thread,
    /// optionally blocking (bounded) for guaranteed completion on emergency unlock or app exit.
    /// </summary>
    public bool EnqueueEndSession(bool waitForCompletion = false, int boundedWaitMs = 3000)
    {
        bool completed = EnqueueLifecycle(
            () => _throttler.EndSession(),
            clearPendingHotWork: true,
            waitForCompletion: waitForCompletion,
            boundedWaitMs: boundedWaitMs);

        if (waitForCompletion && !completed)
        {
            try { _throttler.EndSession(); } catch { }
        }

        return completed;
    }

    /// <summary>
    /// Restores all throttled processes off the UI thread, optionally waiting boundedly.
    /// </summary>
    public bool EnqueueRestoreAll(bool waitForCompletion = false, int boundedWaitMs = 3000)
    {
        bool completed = EnqueueLifecycle(
            () => _throttler.RestoreAll(),
            clearPendingHotWork: true,
            waitForCompletion: waitForCompletion,
            boundedWaitMs: boundedWaitMs);

        if (waitForCompletion && !completed)
        {
            try { _throttler.RestoreAll(); } catch { }
        }

        return completed;
    }

    /// <summary>
    /// Waits (bounded) for all queued restore/lifecycle work to finish on app exit,
    /// forcing synchronous EndSession/RestoreAll fallback if a session remained active or timed out.
    /// </summary>
    public bool FlushOnExit(int boundedWaitMs = 3000)
    {
        bool completed = EnqueueLifecycle(
            () =>
            {
                if (_throttler.IsSessionActive)
                {
                    _throttler.EndSession();
                }
                else if (_throttler.ThrottledCount > 0)
                {
                    _throttler.RestoreAll();
                }
            },
            clearPendingHotWork: true,
            waitForCompletion: true,
            boundedWaitMs: boundedWaitMs);

        if (!completed)
        {
            try
            {
                if (_throttler.IsSessionActive)
                {
                    _throttler.EndSession();
                }
                else if (_throttler.ThrottledCount > 0)
                {
                    _throttler.RestoreAll();
                }
            }
            catch { }
        }

        return completed;
    }

    private bool EnqueueLifecycle(
        Action work,
        bool clearPendingHotWork,
        bool waitForCompletion,
        int boundedWaitMs)
    {
        ManualResetEventSlim? completion = waitForCompletion ? new ManualResetEventSlim(false) : null;
        try
        {
            bool enqueued = false;
            lock (_queueLock)
            {
                if (!_disposed)
                {
                    if (clearPendingHotWork)
                    {
                        _hasPendingForegroundUpdate = false;
                        _hasPendingTick = false;
                    }
                    _lifecycleQueue.Enqueue(new LifecycleWorkItem(work, completion));
                    enqueued = true;
                    Monitor.Pulse(_queueLock);
                }
            }

            if (!enqueued)
            {
                return false;
            }

            if (completion != null)
            {
                return completion.Wait(boundedWaitMs);
            }

            return true;
        }
        finally
        {
            // Keep completion handle valid if timed out? Wait: only dispose AFTER Wait only if signalled OR
            // signal inside try/finally carefully. If timed out, let's NOT dispose completion out from under WorkerLoop's Set(),
            // or wrap Set() with try/catch ObjectDisposedException. Doing both is 100% safe!
            if (completion != null && completion.IsSet)
            {
                completion.Dispose();
            }
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            LifecycleWorkItem? lifecycleItem = null;
            bool runForeground = false;
            int fgPid = 0;
            IntPtr fgHwnd = IntPtr.Zero;
            bool runTick = false;

            lock (_queueLock)
            {
                while (!_disposed &&
                       _lifecycleQueue.Count == 0 &&
                       !_hasPendingForegroundUpdate &&
                       !_hasPendingTick)
                {
                    Monitor.Wait(_queueLock);
                }

                if (_lifecycleQueue.Count > 0)
                {
                    lifecycleItem = _lifecycleQueue.Dequeue();
                }
                else if (_disposed)
                {
                    break;
                }
                else if (_hasPendingForegroundUpdate)
                {
                    runForeground = true;
                    fgPid = _latestForegroundPid;
                    fgHwnd = _latestForegroundHwnd;
                    _hasPendingForegroundUpdate = false;
                }
                else if (_hasPendingTick)
                {
                    runTick = true;
                    _hasPendingTick = false;
                }
            }

            if (lifecycleItem.HasValue)
            {
                var item = lifecycleItem.Value;
                try
                {
                    item.Work();
                }
                catch { }
                finally
                {
                    try { item.Completion?.Set(); } catch (ObjectDisposedException) { }
                }
            }
            else if (runForeground)
            {
                try { _throttler.UpdateForegroundApp(fgPid, fgHwnd); } catch { }
            }
            else if (runTick)
            {
                try { _throttler.Tick(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        lock (_queueLock)
        {
            if (_disposed) return;
            _disposed = true;
            _hasPendingForegroundUpdate = false;
            _hasPendingTick = false;
            Monitor.PulseAll(_queueLock);
        }

        if (_workerThread.IsAlive && Thread.CurrentThread != _workerThread)
        {
            _workerThread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
