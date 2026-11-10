using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Blocker.Core;
using Xunit;

namespace Sankalpa.Tests;

public class LowLevelKeyboardHookHardeningTests
{
    private static string GetRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Blocker.sln")) || File.Exists(Path.Combine(dir, "Sankalpa.sln")) || Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir)!;
        }
        return AppContext.BaseDirectory;
    }

    [Fact]
    public void EmergencyChordTracker_ContinuousHold_TriggersAt3000ms()
    {
        var tracker = new EmergencyChordTracker();
        Assert.False(tracker.IsHolding);
        Assert.Equal(0, tracker.AccumulatedHeldMs);

        // First press at t = 0
        Assert.False(tracker.Update(true, 0));
        Assert.True(tracker.IsHolding);
        Assert.Equal(0, tracker.AccumulatedHeldMs);

        // Intermediate ticks
        for (long t = 50; t < 3000; t += 50)
        {
            Assert.False(tracker.Update(true, t));
            Assert.True(tracker.IsHolding);
            Assert.Equal(t, tracker.AccumulatedHeldMs);
        }

        // At exactly 3000 ms, triggers emergency
        bool triggered = tracker.Update(true, 3000);
        Assert.True(triggered);
        Assert.Equal(3000, tracker.AccumulatedHeldMs);
    }

    [Fact]
    public void EmergencyChordTracker_MomentaryDropWithin300ms_RecoversAndCountsRealHeldTime()
    {
        var tracker = new EmergencyChordTracker();

        // Hold for 1,000 ms
        for (long t = 0; t <= 1000; t += 50)
        {
            tracker.Update(true, t);
        }
        Assert.Equal(1000, tracker.AccumulatedHeldMs);

        // Key drop / ghosting for 200 ms (t = 1050 to t = 1200)
        tracker.Update(false, 1050);
        tracker.Update(false, 1100);
        tracker.Update(false, 1200);

        // Still considered holding due to 300 ms grace tolerance
        Assert.True(tracker.IsHolding);
        // Accumulated real held time did NOT advance during the drop
        Assert.Equal(1000, tracker.AccumulatedHeldMs);

        // Chord recovers at t = 1250 (gap was 200 ms <= 300 ms)
        Assert.False(tracker.Update(true, 1250));
        Assert.True(tracker.IsHolding);
        Assert.Equal(1000, tracker.AccumulatedHeldMs);

        // Continue holding until real accumulated time reaches 3,000 ms (another 2,000 ms held)
        long current = 1250;
        for (int i = 0; i < 39; i++)
        {
            current += 50;
            Assert.False(tracker.Update(true, current));
        }

        // 40th tick (total +2000 ms from 1250 = 3250)
        current += 50;
        bool triggered = tracker.Update(true, current);
        Assert.True(triggered);
        Assert.Equal(3000, tracker.AccumulatedHeldMs);
    }

    [Fact]
    public void EmergencyChordTracker_ContinuousReleaseExceeding300ms_CancelsHold()
    {
        var tracker = new EmergencyChordTracker();

        // Hold for 1,500 ms
        for (long t = 0; t <= 1500; t += 50)
        {
            tracker.Update(true, t);
        }
        Assert.Equal(1500, tracker.AccumulatedHeldMs);

        // Released at t = 1550
        tracker.Update(false, 1550);
        Assert.True(tracker.IsHolding);

        // Still within grace period at t = 1800 (gap 250 ms)
        tracker.Update(false, 1800);
        Assert.True(tracker.IsHolding);

        // Release exceeds 300 ms at t = 1860 (gap 310 ms > 300 ms)
        tracker.Update(false, 1860);
        Assert.False(tracker.IsHolding);
        Assert.Equal(0, tracker.AccumulatedHeldMs);

        // Pressing chord again at t = 2000 starts a fresh hold
        tracker.Update(true, 2000);
        Assert.True(tracker.IsHolding);
        Assert.Equal(0, tracker.AccumulatedHeldMs);
    }

    [Fact]
    public void LowLevelKeyboardHook_SourceCodeContract_VerifiesThreadPriorityAndChords()
    {
        string root = GetRepoRoot();
        string hookPath = Path.Combine(root, "src", "Blocker.Core", "LowLevelKeyboardHook.cs");
        Assert.True(File.Exists(hookPath));

        string code = File.ReadAllText(hookPath);

        // 1. Thread priority set to Highest on hook thread
        Assert.Contains("_hookThread.Priority = ThreadPriority.Highest;", code);

        // 2. Dedicated polling thread for emergency chord
        Assert.Contains("EmergencyChordPollingThread", code);
        Assert.Contains("EmergencyPollingLoop", code);

        // 3. Subsystem logging for hook and polling
        Assert.Contains("[CHORD] via hook", code);
        Assert.Contains("[CHORD] via poll", code);

        // 4. Single fire guard using Interlocked.CompareExchange
        Assert.Contains("Interlocked.CompareExchange(ref _emergencyTriggered, 1, 0)", code);

        // 5. Rehooking safety installs new hook before unhooking old hook
        Assert.Contains("public bool Rehook()", code);
        Assert.Contains("newHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, hMod, 0);", code);
        Assert.Contains("UnhookWindowsHookEx(oldHookHandle);", code);

        // 6. Synchronous blocking preserved
        Assert.Contains("vk == VK_TAB && (winDown || altDown)", code);
        Assert.Contains("vk == VK_LWIN || vk == VK_RWIN", code);
        Assert.Contains("vk == VK_ESCAPE && (altDown || ctrlDown)", code);
        Assert.Contains("ctrlDown && shiftDown", code);
        Assert.Contains("VK_SNAPSHOT", code);
    }

    [Fact]
    public void LowLevelKeyboardHook_SingleFireGuard_FiresAtMostOnce()
    {
        using var hook = new LowLevelKeyboardHook();
        Assert.False(hook.IsEmergencyTriggered);

        int eventCount = 0;
        hook.EmergencyEscapeTriggered += () => Interlocked.Increment(ref eventCount);

        // Invoke private TriggerEmergency via reflection twice simultaneously
        var triggerMethod = typeof(LowLevelKeyboardHook).GetMethod("TriggerEmergency", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(triggerMethod);

        triggerMethod.Invoke(hook, new object[] { "hook" });
        triggerMethod.Invoke(hook, new object[] { "poll" });

        Assert.True(hook.IsEmergencyTriggered);

        // Allow async Task.Run dispatch to complete with polling timeout
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref eventCount) == 0 && sw.ElapsedMilliseconds < 2000)
        {
            Thread.Sleep(20);
        }
        Assert.Equal(1, Volatile.Read(ref eventCount));

        // Clean up any emergency unlock markers written during test
        try
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var allMarkers = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "EMERGENCY_UNLOCK"),
                Path.Combine(Directory.GetCurrentDirectory(), "EMERGENCY_UNLOCK"),
                Path.Combine(appData, "Blocker", "EMERGENCY_UNLOCK"),
                Path.Combine(appData, "Sankalpa", "EMERGENCY_UNLOCK"),
                Path.Combine(programData, "Sankalpa", "EMERGENCY_UNLOCK"),
                Path.Combine(programData, "Blocker", "EMERGENCY_UNLOCK")
            };
            foreach (var f in allMarkers)
            {
                if (File.Exists(f)) { try { File.Delete(f); } catch { } }
            }
        }
        catch { }
    }
}
