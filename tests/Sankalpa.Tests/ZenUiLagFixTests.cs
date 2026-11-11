using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blocker.Core;
using Xunit;

namespace Sankalpa.Tests;

public class ZenUiLagFixTests : IDisposable
{
    private readonly string _tempDir;

    public ZenUiLagFixTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Sankalpa_ZenUiLag_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    private static string GetRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(dir, "Blocker.sln")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir) ?? dir;
        }
        throw new DirectoryNotFoundException("Could not locate Blocker.sln repository root.");
    }

    // =========================================================================
    // (a) WinEvent Filter Accepts ONLY the 5 Intended Event Types
    // =========================================================================

    [Fact]
    public void AllowedWinEventFilter_AcceptsOnlyTheFiveIntendedEvents_AndRejectsNoisyEvents()
    {
        uint[] expectedAllowed =
        {
            AllowedWinEventFilter.EVENT_SYSTEM_FOREGROUND,    // 0x0003
            AllowedWinEventFilter.EVENT_SYSTEM_MINIMIZESTART, // 0x0016
            AllowedWinEventFilter.EVENT_SYSTEM_MINIMIZEEND,   // 0x0017
            AllowedWinEventFilter.EVENT_OBJECT_DESTROY,       // 0x8001
            AllowedWinEventFilter.EVENT_OBJECT_HIDE           // 0x8003
        };

        Assert.Equal(5, AllowedWinEventFilter.HookedEventTypes.Count);
        foreach (uint evt in expectedAllowed)
        {
            Assert.Contains(evt, AllowedWinEventFilter.HookedEventTypes);
            Assert.True(AllowedWinEventFilter.IsAllowedEventType(evt), $"Expected 0x{evt:X4} to be allowed.");
        }

        // Known lag-causing noisy events from prior range hooks (0x0003..0x0017, 0x8001..0x8003)
        uint[] rejectedEvents =
        {
            0x0001, // EVENT_SYSTEM_SOUND
            0x0002, // EVENT_SYSTEM_ALERT
            0x0004, // EVENT_SYSTEM_MENUSTART
            0x0005, // EVENT_SYSTEM_MENUEND
            0x0006, // EVENT_SYSTEM_MENUPOPUPSTART
            0x0007, // EVENT_SYSTEM_MENUPOPUPEND
            0x0008, // EVENT_SYSTEM_CAPTURESTART
            0x0009, // EVENT_SYSTEM_CAPTUREEND
            0x000A, // EVENT_SYSTEM_MOVESIZESTART
            0x000B, // EVENT_SYSTEM_MOVESIZEEND
            0x0010, // EVENT_SYSTEM_DIALOGSTART
            0x0011, // EVENT_SYSTEM_DIALOGEND
            0x0012, // EVENT_SYSTEM_SCROLLINGSTART
            0x0013, // EVENT_SYSTEM_SCROLLINGEND
            0x0014, // EVENT_SYSTEM_SWITCHSTART
            0x0015, // EVENT_SYSTEM_SWITCHEND
            0x8000, // EVENT_OBJECT_CREATE
            0x8002, // EVENT_OBJECT_SHOW
            0x8004, // EVENT_OBJECT_REORDER
            0x8005, // EVENT_OBJECT_FOCUS
            0x800C  // EVENT_OBJECT_NAMECHANGE
        };

        IntPtr tracked = new(0x1234);
        IntPtr other = new(0x5678);

        foreach (uint evt in rejectedEvents)
        {
            Assert.False(AllowedWinEventFilter.IsAllowedEventType(evt), $"Expected 0x{evt:X4} to be rejected.");
            Assert.False(
                AllowedWinEventFilter.ShouldProcess(evt, AllowedWinEventFilter.OBJID_WINDOW, tracked, tracked),
                $"ShouldProcess must reject 0x{evt:X4}.");
        }

        // Non-window object IDs must be rejected for non-foreground events
        Assert.False(AllowedWinEventFilter.ShouldProcess(
            AllowedWinEventFilter.EVENT_SYSTEM_MINIMIZESTART, idObject: 1, tracked, tracked));

        // Zero HWND must always be rejected
        Assert.False(AllowedWinEventFilter.ShouldProcess(
            AllowedWinEventFilter.EVENT_SYSTEM_FOREGROUND, AllowedWinEventFilter.OBJID_WINDOW, IntPtr.Zero, tracked));

        // DESTROY and HIDE must only pass when targeting the currently tracked HWND
        Assert.False(AllowedWinEventFilter.ShouldProcess(
            AllowedWinEventFilter.EVENT_OBJECT_DESTROY, AllowedWinEventFilter.OBJID_WINDOW, other, tracked));
        Assert.False(AllowedWinEventFilter.ShouldProcess(
            AllowedWinEventFilter.EVENT_OBJECT_HIDE, AllowedWinEventFilter.OBJID_WINDOW, other, tracked));
        Assert.True(AllowedWinEventFilter.ShouldProcess(
            AllowedWinEventFilter.EVENT_OBJECT_DESTROY, AllowedWinEventFilter.OBJID_WINDOW, tracked, tracked));
        Assert.True(AllowedWinEventFilter.ShouldProcess(
            AllowedWinEventFilter.EVENT_OBJECT_HIDE, AllowedWinEventFilter.OBJID_WINDOW, tracked, tracked));

        // Foreground & minimize notifications pass for any valid window HWND
        Assert.True(AllowedWinEventFilter.ShouldProcess(
            AllowedWinEventFilter.EVENT_SYSTEM_FOREGROUND, AllowedWinEventFilter.OBJID_WINDOW, other, tracked));
        Assert.True(AllowedWinEventFilter.ShouldProcess(
            AllowedWinEventFilter.EVENT_SYSTEM_MINIMIZEEND, AllowedWinEventFilter.OBJID_WINDOW, other, tracked));
    }

    [Fact]
    public void AllowedAppForegroundTracker_SourceUsesDiscreteFiveEventHooks_WithoutRangeHooks()
    {
        string trackerPath = Path.Combine(GetRepoRoot(), "src", "Blocker.App", "AllowedAppForegroundTracker.cs");
        string code = File.ReadAllText(trackerPath);

        Assert.Contains("AllowedWinEventFilter.HookedEventTypes", code);
        Assert.Contains("AllowedWinEventFilter.ShouldProcess(", code);
        Assert.Contains("_coalescer.Request();", code);
        Assert.DoesNotContain("SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZEEND", code);
        Assert.DoesNotContain("SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE", code);
        Assert.DoesNotContain("WINEVENT_SKIPOWNPROCESS", code);
    }

    // =========================================================================
    // (b) Repeated HUD-Visible Evaluations Do Not Re-Issue Focus/Z-Order Actions
    // =========================================================================

    [Fact]
    public void MiniHudPresentationCoordinator_IsIdempotentWhileVisible_AndActivatesOnlyOnTransitions()
    {
        var coordinator = new MiniHudPresentationCoordinator(topmostReassertCooldown: TimeSpan.FromSeconds(2));
        IntPtr vlcHwnd = new(0xAA01);
        IntPtr codeHwnd = new(0xBB02);
        DateTime t0 = new(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);

        // 1. Initial transition hidden -> shown activates allowed app & positions HUD
        MiniHudShowPlan first = coordinator.EvaluateShow(
            vlcHwnd,
            isHudWindowCurrentlyVisible: false,
            hudIsActuallyTopmost: true,
            isModalDialogOpen: false,
            utcNow: t0);

        Assert.True(first.ShouldShowOrPositionHud);
        Assert.True(first.ShouldActivateAllowedApp);
        Assert.True(first.ShouldUpdateBackdrops);
        Assert.False(first.ShouldReassertHudTopmost);

        // 2. Repeated evaluations for the same HWND while already visible must be no-ops
        for (int i = 1; i <= 50; i++)
        {
            MiniHudShowPlan repeated = coordinator.EvaluateShow(
                vlcHwnd,
                isHudWindowCurrentlyVisible: true,
                hudIsActuallyTopmost: true,
                isModalDialogOpen: false,
                utcNow: t0.AddMilliseconds(i * 20));

            Assert.False(repeated.ShouldShowOrPositionHud);
            Assert.False(repeated.ShouldActivateAllowedApp);
            Assert.False(repeated.ShouldUpdateBackdrops);
            Assert.False(repeated.ShouldReassertHudTopmost);
        }

        // 3. Lost WS_EX_TOPMOST re-asserts HUD topmost ONLY (never touches allowed app focus), rate-limited
        MiniHudShowPlan notYetCooldown = coordinator.EvaluateShow(
            vlcHwnd,
            isHudWindowCurrentlyVisible: true,
            hudIsActuallyTopmost: false,
            isModalDialogOpen: false,
            utcNow: t0.AddSeconds(1.5));
        Assert.False(notYetCooldown.ShouldReassertHudTopmost);
        Assert.False(notYetCooldown.ShouldActivateAllowedApp);

        MiniHudShowPlan lostTopmost = coordinator.EvaluateShow(
            vlcHwnd,
            isHudWindowCurrentlyVisible: true,
            hudIsActuallyTopmost: false,
            isModalDialogOpen: false,
            utcNow: t0.AddSeconds(3.5));
        Assert.True(lostTopmost.ShouldReassertHudTopmost);
        Assert.False(lostTopmost.ShouldActivateAllowedApp);
        Assert.False(lostTopmost.ShouldShowOrPositionHud);

        // Next immediate poll is within cooldown again
        MiniHudShowPlan withinCooldownAgain = coordinator.EvaluateShow(
            vlcHwnd,
            isHudWindowCurrentlyVisible: true,
            hudIsActuallyTopmost: false,
            isModalDialogOpen: false,
            utcNow: t0.AddSeconds(4.0));
        Assert.False(withinCooldownAgain.ShouldReassertHudTopmost);

        // 4. Retarget to a different HWND triggers transition once, then becomes idempotent again
        MiniHudShowPlan retarget = coordinator.EvaluateShow(
            codeHwnd,
            isHudWindowCurrentlyVisible: true,
            hudIsActuallyTopmost: true,
            isModalDialogOpen: false,
            utcNow: t0.AddSeconds(5));
        Assert.True(retarget.ShouldShowOrPositionHud);
        Assert.True(retarget.ShouldActivateAllowedApp);

        MiniHudShowPlan afterRetarget = coordinator.EvaluateShow(
            codeHwnd,
            isHudWindowCurrentlyVisible: true,
            hudIsActuallyTopmost: true,
            isModalDialogOpen: false,
            utcNow: t0.AddSeconds(5.1));
        Assert.False(afterRetarget.ShouldShowOrPositionHud);
        Assert.False(afterRetarget.ShouldActivateAllowedApp);

        // 5. Explicit warm/cold switch requests a single transition even for the same HWND
        coordinator.RequestExplicitSwitch();
        MiniHudShowPlan explicitSwitch = coordinator.EvaluateShow(
            codeHwnd,
            isHudWindowCurrentlyVisible: true,
            hudIsActuallyTopmost: true,
            isModalDialogOpen: false,
            utcNow: t0.AddSeconds(6));
        Assert.True(explicitSwitch.ShouldShowOrPositionHud);
        Assert.True(explicitSwitch.ShouldActivateAllowedApp);

        // 6. Modal unlock dialog suppresses HUD/focus/re-assertion work
        MiniHudShowPlan whileModalOpen = coordinator.EvaluateShow(
            vlcHwnd,
            isHudWindowCurrentlyVisible: true,
            hudIsActuallyTopmost: false,
            isModalDialogOpen: true,
            utcNow: t0.AddSeconds(10));
        Assert.Equal(default, whileModalOpen);

        // 7. After NotifyHidden, next show activates allowed app again
        coordinator.NotifyHidden();
        MiniHudShowPlan reshow = coordinator.EvaluateShow(
            codeHwnd,
            isHudWindowCurrentlyVisible: true,
            hudIsActuallyTopmost: true,
            isModalDialogOpen: false,
            utcNow: t0.AddSeconds(11));
        Assert.True(reshow.ShouldShowOrPositionHud);
        Assert.True(reshow.ShouldActivateAllowedApp);
    }

    // =========================================================================
    // (c) Trailing-Edge Coalescer Never Drops Foreground Change or Window Loss
    // =========================================================================

    [Fact]
    public void TrailingEdgeCoalescer_CollapsesBursts_AndGuaranteesTrailingEdgeAfterMidEvalEvents()
    {
        var postedQueue = new Queue<Action>();
        int currentForeground = 1; // 1 = VLC (allowed)
        var observedForegroundStates = new List<int>();
        using var coalescer = new TrailingEdgeCoalescer(
            evaluate: () =>
            {
                int seen = Volatile.Read(ref currentForeground);
                observedForegroundStates.Add(seen);

                // Simulate a foreground switch or tracked-window loss arriving mid-evaluation
                if (seen == 1 && observedForegroundStates.Count == 1)
                {
                    Volatile.Write(ref currentForeground, 0); // 0 = lost tracked window / unauthorized foreground
                    // Re-trigger via Request() while evaluation is in flight!
                }
            },
            post: action => postedQueue.Enqueue(action));

        // Burst of 25 requests collapses into a single queued dispatcher callback
        for (int i = 0; i < 25; i++)
        {
            coalescer.Request();
        }
        Assert.Single(postedQueue);

        // Run first scheduled evaluation; simulate a mid-eval Request() caused by foreground switch/hide
        bool midEvalTriggered = false;
        var midEvalQueue = new Queue<Action>();
        int midEvalState = 1;
        var midEvalObserved = new List<int>();
        TrailingEdgeCoalescer? coalescer2 = null;
        coalescer2 = new TrailingEdgeCoalescer(
            evaluate: () =>
            {
                int snapshot = Volatile.Read(ref midEvalState);
                midEvalObserved.Add(snapshot);
                if (!midEvalTriggered)
                {
                    midEvalTriggered = true;
                    // Simulate WinEvent arriving while EvaluateNow is already running:
                    Volatile.Write(ref midEvalState, 99);
                    coalescer2!.Request();
                    // Further duplicate requests mid-eval must collapse into that same trailing run:
                    coalescer2.Request();
                    coalescer2.Request();
                }
            },
            post: action => midEvalQueue.Enqueue(action));

        using (coalescer2)
        {
            for (int i = 0; i < 10; i++)
            {
                coalescer2.Request();
            }

            Assert.Single(midEvalQueue);

            // Drain scheduled work sequentially as WPF Dispatcher does
            while (midEvalQueue.Count > 0)
            {
                midEvalQueue.Dequeue().Invoke();
            }

            // Must have evaluated initial run (1) AND trailing edge (99) — never dropped!
            Assert.Equal(new[] { 1, 99 }, midEvalObserved);

            // Direct evaluation (watchdog/debounce) also honors trailing-edge requests
            Volatile.Write(ref midEvalState, 200);
            midEvalTriggered = true;
            coalescer2.RunDirect();
            Assert.Equal(new[] { 1, 99, 200 }, midEvalObserved);

            // CancelPending clears unstarted scheduled callback; Dispose blocks subsequent requests
            coalescer2.CancelPending();
            coalescer2.Dispose();
            int beforeDisposed = midEvalQueue.Count;
            coalescer2.Request();
            Assert.Equal(beforeDisposed, midEvalQueue.Count);
        }
    }

    // =========================================================================
    // (d) Password Verification Runs Once Per Attempt and Fails Closed
    // =========================================================================

    [Fact]
    public async Task PasswordVerificationCoordinator_VerifiesOncePerAttempt_OffThread_AndFailsClosed()
    {
        int validatorThreadId = -1;
        bool validatorOnThreadPool = false;

        var coordinator = new PasswordVerificationCoordinator(pwd =>
        {
            validatorThreadId = Environment.CurrentManagedThreadId;
            validatorOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            return pwd == "correct-secret-pass";
        });

        // Wrong password: runs validator once, fails closed, no secret leaked in error message
        PasswordVerificationAttemptResult wrong = await coordinator.VerifyAttemptAsync("wrong-guess-pass");
        Assert.False(wrong.Succeeded);
        Assert.False(wrong.IgnoredInFlight);
        Assert.False(wrong.TimedOut);
        Assert.False(coordinator.IsVerified);
        Assert.Equal(1, coordinator.VerificationCallCount);
        Assert.DoesNotContain("wrong-guess-pass", wrong.ErrorMessage ?? string.Empty);
        Assert.DoesNotContain("correct-secret-pass", wrong.ErrorMessage ?? string.Empty);

        // Empty password: fails closed without invoking PBKDF2 validator
        PasswordVerificationAttemptResult empty = await coordinator.VerifyAttemptAsync("");
        Assert.False(empty.Succeeded);
        Assert.False(coordinator.IsVerified);
        Assert.Equal(1, coordinator.VerificationCallCount);

        // Correct password called from a dedicated non-threadpool thread (simulating WPF UI thread):
        // validator must run on a ThreadPool worker off the caller thread.
        int uiThreadId = -1;
        PasswordVerificationAttemptResult ok = default;
        using var done = new ManualResetEventSlim(false);
        var uiThread = new Thread(() =>
        {
            uiThreadId = Environment.CurrentManagedThreadId;
            ok = coordinator.VerifyAttemptAsync("correct-secret-pass").GetAwaiter().GetResult();
            done.Set();
        })
        { IsBackground = true };
        if (OperatingSystem.IsWindows())
        {
            uiThread.SetApartmentState(ApartmentState.STA);
        }
        uiThread.Start();
        Assert.True(done.Wait(5000));

        Assert.True(ok.Succeeded);
        Assert.True(coordinator.IsVerified);
        Assert.Equal(2, coordinator.VerificationCallCount);
        Assert.True(validatorOnThreadPool);
        Assert.NotEqual(uiThreadId, validatorThreadId);

        // Reset clears verified flag
        coordinator.Reset();
        Assert.False(coordinator.IsVerified);
    }

    [Fact]
    public async Task PasswordVerificationCoordinator_FailsClosedOnException_Timeout_AndConcurrentOverlap()
    {
        // Exception case: must fail closed
        var throwingCoord = new PasswordVerificationCoordinator(_ => throw new InvalidOperationException("crypto failure"));
        PasswordVerificationAttemptResult errResult = await throwingCoord.VerifyAttemptAsync("any");
        Assert.False(errResult.Succeeded);
        Assert.False(throwingCoord.IsVerified);
        Assert.Equal(1, throwingCoord.VerificationCallCount);

        // Timeout case + overlapping second click case
        using var releaseGate = new ManualResetEventSlim(false);
        var slowCoord = new PasswordVerificationCoordinator(
            _ =>
            {
                releaseGate.Wait(2000);
                return true;
            },
            timeout: TimeSpan.FromMilliseconds(80));

        Task<PasswordVerificationAttemptResult> inFlight = slowCoord.VerifyAttemptAsync("pass");
        // Wait briefly until the worker enters the slow delegate
        Assert.True(SpinWait.SpinUntil(() => slowCoord.IsVerifying, 1000));

        // Overlapping submit while already verifying must be ignored and must NOT call validator twice
        PasswordVerificationAttemptResult overlap = await slowCoord.VerifyAttemptAsync("pass");
        Assert.False(overlap.Succeeded);
        Assert.True(overlap.IgnoredInFlight);
        Assert.False(slowCoord.IsVerified);

        PasswordVerificationAttemptResult timedOut = await inFlight;
        releaseGate.Set();

        Assert.False(timedOut.Succeeded);
        Assert.True(timedOut.TimedOut);
        Assert.False(slowCoord.IsVerified);
        Assert.Equal(1, slowCoord.VerificationCallCount);
    }

    // =========================================================================
    // (e) Throttler Background Worker Parity (MTA COM-Safe, Identical Semantics)
    // =========================================================================

    [Fact]
    public void ThrottlerBackgroundWorker_ProducesIdenticalThrottleAndRestoreResultsOffUiThread()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var targetProc = Process.Start(new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "-n 30 127.0.0.1",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        Assert.NotNull(targetProc);

        string directJournal = Path.Combine(_tempDir, "direct", "throttled_apps.json");
        string workerJournal = Path.Combine(_tempDir, "worker", "throttled_apps.json");

        try
        {
            int targetPid = targetProc.Id;
            var allowedApps = new List<string> { "ping", "csrss", "blocker.service" };
            var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ping"] = Environment.SystemDirectory
            };

            // 1. Direct synchronous throttler baseline
            using var directThrottler = new BackgroundAppThrottler(
                directJournal,
                workingSetTrimDelay: TimeSpan.FromMilliseconds(50));
            directThrottler.StartSession(allowedApps, roots);
            directThrottler.UpdateForegroundApp(0);
            directThrottler.Tick();

            Assert.True(directThrottler.IsThrottled(targetPid), "Direct throttler should throttle background allowed process.");
            Assert.False(directThrottler.IsThrottled(0));
            Assert.False(directThrottler.IsThrottled(4));
            Assert.False(directThrottler.IsThrottled(Environment.ProcessId));

            targetProc.Refresh();
            ProcessPriorityClass directThrottledPriority = targetProc.PriorityClass;
            Assert.Equal(ProcessPriorityClass.BelowNormal, directThrottledPriority);

            directThrottler.EndSession();
            Assert.False(directThrottler.IsSessionActive);
            Assert.Equal(0, directThrottler.ThrottledCount);
            targetProc.Refresh();
            Assert.Equal(ProcessPriorityClass.Normal, targetProc.PriorityClass);

            // 2. Worker-thread throttler: must produce identical state transitions and full restore on EndSession
            using var workerThrottler = new BackgroundAppThrottler(
                workerJournal,
                workingSetTrimDelay: TimeSpan.FromMilliseconds(50));
            using var worker = new ThrottlerBackgroundWorker(workerThrottler, "Sankalpa.TestThrottlerWorker");

            worker.EnqueueStartSession(allowedApps, roots);
            Assert.True(SpinWait.SpinUntil(() => workerThrottler.IsSessionActive, 3000));

            // Coalesced foreground burst: latest PID wins (0 -> background all allowed apps)
            worker.EnqueueForegroundUpdate(targetPid);
            worker.EnqueueForegroundUpdate(999999);
            worker.EnqueueForegroundUpdate(0);
            Assert.True(SpinWait.SpinUntil(() => workerThrottler.IsThrottled(targetPid) && File.Exists(workerJournal), 4000));

            Assert.False(workerThrottler.IsThrottled(0));
            Assert.False(workerThrottler.IsThrottled(4));
            Assert.False(workerThrottler.IsThrottled(Environment.ProcessId));

            targetProc.Refresh();
            Assert.Equal(directThrottledPriority, targetProc.PriorityClass);
            Assert.True(File.Exists(workerJournal));

            // Fast synchronous restore of foreground target happens immediately on calling thread
            worker.EnqueueForegroundUpdate(targetPid);
            Assert.False(workerThrottler.IsThrottled(targetPid));
            targetProc.Refresh();
            Assert.Equal(ProcessPriorityClass.Normal, targetProc.PriorityClass);

            // Re-throttle in background, verify Tick and bounded FlushOnExit/EndSession restore + journal cleanup
            worker.EnqueueForegroundUpdate(0);
            Assert.True(SpinWait.SpinUntil(() => workerThrottler.IsThrottled(targetPid) && File.Exists(workerJournal), 4000));
            Thread.Sleep(70);
            worker.EnqueueTick();

            bool endedCleanly = worker.EnqueueEndSession(waitForCompletion: true, boundedWaitMs: 3000);
            Assert.True(endedCleanly);
            Assert.False(workerThrottler.IsSessionActive);
            Assert.Equal(0, workerThrottler.ThrottledCount);
            Assert.False(File.Exists(workerJournal));

            targetProc.Refresh();
            Assert.Equal(ProcessPriorityClass.Normal, targetProc.PriorityClass);

            // Re-start and verify FlushOnExit(3000) restores and removes journal synchronously on app shutdown
            worker.EnqueueStartSession(allowedApps, roots);
            Assert.True(SpinWait.SpinUntil(() => workerThrottler.IsSessionActive, 3000));
            worker.EnqueueForegroundUpdate(0);
            Assert.True(SpinWait.SpinUntil(() => workerThrottler.IsThrottled(targetPid) && File.Exists(workerJournal), 4000));
            Assert.True(worker.FlushOnExit(3000));
            Assert.False(workerThrottler.IsSessionActive);
            Assert.Equal(0, workerThrottler.ThrottledCount);
            Assert.False(File.Exists(workerJournal));
            targetProc.Refresh();
            Assert.Equal(ProcessPriorityClass.Normal, targetProc.PriorityClass);
        }
        finally
        {
            try { targetProc.Kill(); } catch { }
        }
    }
}
