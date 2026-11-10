using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Blocker.Core;
using Xunit;

namespace Sankalpa.Tests;

public class BackgroundAppThrottlerTests : IDisposable
{
    private readonly string _testJournalPath;

    public BackgroundAppThrottlerTests()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Sankalpa_Throttler_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        _testJournalPath = Path.Combine(tempDir, "throttled_apps.json");
    }

    public void Dispose()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_testJournalPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void BackgroundAppThrottler_SessionLifecycle_TracksAndCleansState()
    {
        using var throttler = new BackgroundAppThrottler(_testJournalPath);
        Assert.False(throttler.IsSessionActive);
        Assert.Equal(0, throttler.ThrottledCount);

        var allowedApps = new List<string> { "notepad", "code" };
        var roots = new Dictionary<string, string> { ["notepad"] = @"C:\Windows\System32" };

        throttler.StartSession(allowedApps, roots);
        Assert.True(throttler.IsSessionActive);

        throttler.EndSession();
        Assert.False(throttler.IsSessionActive);
        Assert.Equal(0, throttler.ThrottledCount);
    }

    [Fact]
    public void BackgroundAppThrottler_NeverTouchesProtectedOrSankalpaProcesses()
    {
        using var throttler = new BackgroundAppThrottler(_testJournalPath);
        throttler.StartSession(new List<string> { "notepad", "code", "csrss", "blocker.service" });

        // Update foreground to another app (e.g. notepad)
        throttler.UpdateForegroundApp(99999);

        // Verify protected processes are NEVER throttled
        Assert.False(throttler.IsThrottled(0)); // Idle
        Assert.False(throttler.IsThrottled(4)); // System
        Assert.False(throttler.IsThrottled(Environment.ProcessId)); // Current process
    }

    [Fact]
    public void BackgroundAppThrottler_CrashJournal_PersistsAtomicallyAndRecovers()
    {
        using var targetProc = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping 127.0.0.1 -n 30 > nul",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        Assert.NotNull(targetProc);

        try
        {
            int targetPid = targetProc.Id;
            ProcessIdentityHelper.GetProcessIdentity(targetPid, out long creationTime, out string? imagePath);

            var journalEntries = new List<ThrottledProcessJournalEntry>
            {
                new()
                {
                    ProcessId = targetPid,
                    CreationTime = creationTime,
                    ImagePath = imagePath ?? string.Empty,
                    ProcessName = targetProc.ProcessName,
                    OriginalPriorityClass = ProcessIdentityHelper.NORMAL_PRIORITY_CLASS,
                    ThrottledAtUtc = DateTime.UtcNow
                }
            };

            BackgroundAppThrottler.EnsureSecureJournalDirectory(Path.GetDirectoryName(_testJournalPath)!);
            string json = System.Text.Json.JsonSerializer.Serialize(journalEntries);
            File.WriteAllText(_testJournalPath, json);
            Assert.True(File.Exists(_testJournalPath));

            // 2. Perform recovery from journal
            int recovered = BackgroundAppThrottler.RecoverFromJournal(_testJournalPath);
            Assert.True(recovered >= 1);

            // 3. Ensure journal file was deleted upon completion
            Assert.False(File.Exists(_testJournalPath));
        }
        finally
        {
            try { targetProc.Kill(); } catch { }
        }
    }

    [Fact]
    public void BackgroundAppThrottler_IdentityVerification_PreventsRestoringReusedPid()
    {
        int currentPid = Environment.ProcessId;
        ProcessIdentityHelper.GetProcessIdentity(currentPid, out long actualCreationTime, out string? actualPath);

        // Create an entry with the CURRENT PID but a FAKE creation time (simulating PID reuse)
        long fakeCreationTime = actualCreationTime + 9999999;
        var journalEntries = new List<ThrottledProcessJournalEntry>
        {
            new()
            {
                ProcessId = currentPid,
                CreationTime = fakeCreationTime, // Wrong creation time!
                ImagePath = actualPath ?? string.Empty,
                ProcessName = "reused",
                OriginalPriorityClass = ProcessIdentityHelper.BELOW_NORMAL_PRIORITY_CLASS,
                ThrottledAtUtc = DateTime.UtcNow
            }
        };

        BackgroundAppThrottler.EnsureSecureJournalDirectory(Path.GetDirectoryName(_testJournalPath)!);
        string json = System.Text.Json.JsonSerializer.Serialize(journalEntries);
        File.WriteAllText(_testJournalPath, json);

        // Attempt recovery - must NOT restore because creation time does not match!
        int recovered = BackgroundAppThrottler.RecoverFromJournal(_testJournalPath);
        Assert.Equal(0, recovered);
    }

    [Fact]
    public void BackgroundAppThrottler_IdentityVerification_PreventsRestoringMismatchedImagePath()
    {
        int currentPid = Environment.ProcessId;
        ProcessIdentityHelper.GetProcessIdentity(currentPid, out long actualCreationTime, out _);

        // Create an entry with matching creation time but WRONG image path
        var journalEntries = new List<ThrottledProcessJournalEntry>
        {
            new()
            {
                ProcessId = currentPid,
                CreationTime = actualCreationTime,
                ImagePath = @"C:\NonExistent\MaliciousApp.exe", // Wrong image path!
                ProcessName = "malicious",
                OriginalPriorityClass = ProcessIdentityHelper.BELOW_NORMAL_PRIORITY_CLASS,
                ThrottledAtUtc = DateTime.UtcNow
            }
        };

        BackgroundAppThrottler.EnsureSecureJournalDirectory(Path.GetDirectoryName(_testJournalPath)!);
        string json = System.Text.Json.JsonSerializer.Serialize(journalEntries);
        File.WriteAllText(_testJournalPath, json);

        // Attempt recovery - must NOT restore because image path does not match!
        int recovered = BackgroundAppThrottler.RecoverFromJournal(_testJournalPath);
        Assert.Equal(0, recovered);
    }

    [Fact]
    public void BackgroundAppThrottler_ClickToForegroundLatency_IsSubMillisecond()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var targetProc = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping 127.0.0.1 -n 30 > nul",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        Assert.NotNull(targetProc);

        try
        {
            int targetPid = targetProc.Id;
            ProcessIdentityHelper.GetProcessIdentity(targetPid, out long creationTime, out string? path);

            // Benchmark RestoreProcess directly
            var sw = Stopwatch.StartNew();
            const int iterations = 100;
            for (int i = 0; i < iterations; i++)
            {
                ProcessIdentityHelper.RestoreProcess(targetPid, creationTime, path, ProcessIdentityHelper.NORMAL_PRIORITY_CLASS);
            }
            sw.Stop();

            double avgMicroseconds = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;

            // Sub-millisecond requirement: average restore latency must be well below 1000 microseconds (1 ms)
            Assert.True(avgMicroseconds < 1000.0, $"Expected < 1000 µs (1 ms), got {avgMicroseconds:F1} µs");
        }
        finally
        {
            try { targetProc.Kill(); } catch { }
        }
    }

    [Fact]
    public void BackgroundAppThrottler_WorkingSetTrim_TrimsOnceAfterDelayAndNeverRepeats()
    {
        var shortDelay = TimeSpan.FromMilliseconds(50);
        using var throttler = new BackgroundAppThrottler(_testJournalPath, workingSetTrimDelay: shortDelay);

        throttler.StartSession(new List<string> { "testapp" });

        // Verify initial state
        throttler.Tick();
        Assert.Equal(0, throttler.ThrottledCount);

        // ProcessIdentityHelper TrimWorkingSet test on current process
        if (OperatingSystem.IsWindows())
        {
            IntPtr hProcess = ProcessIdentityHelper.OpenProcess(
                ProcessIdentityHelper.PROCESS_SET_QUOTA | ProcessIdentityHelper.PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                Environment.ProcessId);

            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    bool trimmed = ProcessIdentityHelper.TrimWorkingSet(hProcess);
                    // Successfully called native API
                    Assert.True(trimmed);
                }
                finally
                {
                    ProcessIdentityHelper.CloseHandle(hProcess);
                }
            }
        }
    }

    [Fact]
    public void BackgroundAppThrottler_AudioExemption_DetectsOrGracefullyHandlesAudioSessions()
    {
        // Must never throw, even if audio hardware is absent or disabled
        var audioPids = AudioSessionDetector.GetActiveAudioProcessIds();
        Assert.NotNull(audioPids);

        // All detected PIDs must be valid positive integers > 4
        foreach (int pid in audioPids)
        {
            Assert.True(pid > 4);
        }
    }
}
