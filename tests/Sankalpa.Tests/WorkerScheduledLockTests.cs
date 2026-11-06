using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blocker.Core;
using Xunit;

namespace Sankalpa.Tests;

public class WorkerScheduledLockTests
{
    private static bool EvaluateHardLock(
        IEnumerable<Profile> allProfiles,
        bool anySessionLocked,
        DateTime localNow)
    {
        bool anyScheduleLocked = allProfiles.Any(p =>
            p.Schedule != null &&
            p.Schedule.Count > 0 &&
            ScheduleLogic.IsActive(p.Schedule, localNow));

        return anySessionLocked || anyScheduleLocked;
    }

    [Fact]
    public void ActiveSchedule_EvaluatesToLocked_EvenWithNoActiveSessions()
    {
        // Monday 10:00 AM (Minute 600)
        var mondayTenAm = new DateTime(2026, 11, 2, 10, 0, 0);

        var profiles = new List<Profile>
        {
            new Profile
            {
                Id = 1,
                Name = "WorkFocus",
                Enabled = true,
                Schedule = new List<ScheduleRule>
                {
                    new ScheduleRule
                    {
                        Days = DaysOfWeek.Weekdays,
                        StartMinute = 540, // 09:00
                        EndMinute = 1020   // 17:00
                    }
                }
            }
        };

        bool anySessionLocked = false;
        bool isHardLocked = EvaluateHardLock(profiles, anySessionLocked, mondayTenAm);

        Assert.True(isHardLocked, "Active scheduled profile should cause lock condition to evaluate to true.");
    }

    [Fact]
    public void OutsideScheduleHours_EvaluatesToUnlocked_WhenNoSessionLocked()
    {
        // Monday 8:30 PM (20:30 = Minute 1230)
        var mondayEvening = new DateTime(2026, 11, 2, 20, 30, 0);

        var profiles = new List<Profile>
        {
            new Profile
            {
                Id = 1,
                Name = "WorkFocus",
                Enabled = true,
                Schedule = new List<ScheduleRule>
                {
                    new ScheduleRule
                    {
                        Days = DaysOfWeek.Weekdays,
                        StartMinute = 540, // 09:00
                        EndMinute = 1020   // 17:00
                    }
                }
            }
        };

        bool anySessionLocked = false;
        bool isHardLocked = EvaluateHardLock(profiles, anySessionLocked, mondayEvening);

        Assert.False(isHardLocked, "Outside schedule hours should evaluate to false when no session is locked.");
    }

    [Fact]
    public void DisabledProfile_WithActiveSchedule_TriggersLock()
    {
        // Monday 10:00 AM
        var mondayTenAm = new DateTime(2026, 11, 2, 10, 0, 0);

        var profiles = new List<Profile>
        {
            new Profile
            {
                Id = 1,
                Name = "WorkFocus",
                Enabled = false, // Disabled
                Schedule = new List<ScheduleRule>
                {
                    new ScheduleRule
                    {
                        Days = DaysOfWeek.Weekdays,
                        StartMinute = 540,
                        EndMinute = 1020
                    }
                }
            }
        };

        bool anySessionLocked = false;
        bool isHardLocked = EvaluateHardLock(profiles, anySessionLocked, mondayTenAm);

        Assert.True(isHardLocked, "Disabled profile with matching schedule should trigger lock.");
    }

    [Fact]
    public void DisabledProfile_OutsideScheduleHours_DoesNotTriggerLock()
    {
        // Monday 8:30 PM (20:30 = Minute 1230)
        var mondayEvening = new DateTime(2026, 11, 2, 20, 30, 0);

        var profiles = new List<Profile>
        {
            new Profile
            {
                Id = 1,
                Name = "WorkFocus",
                Enabled = false, // Disabled
                Schedule = new List<ScheduleRule>
                {
                    new ScheduleRule
                    {
                        Days = DaysOfWeek.Weekdays,
                        StartMinute = 540,
                        EndMinute = 1020
                    }
                }
            }
        };

        bool anySessionLocked = false;
        bool isHardLocked = EvaluateHardLock(profiles, anySessionLocked, mondayEvening);

        Assert.False(isHardLocked, "Disabled profile outside schedule hours should not trigger lock.");
    }

    [Fact]
    public void ProfileWithEmptyOrNullSchedule_DoesNotTriggerLock()
    {
        var now = DateTime.Now;

        var profiles = new List<Profile>
        {
            new Profile
            {
                Id = 1,
                Name = "AlwaysOnProfile",
                Enabled = true,
                Schedule = new List<ScheduleRule>()
            },
            new Profile
            {
                Id = 2,
                Name = "NullScheduleProfile",
                Enabled = true,
                Schedule = null!
            }
        };

        bool anySessionLocked = false;
        bool isHardLocked = EvaluateHardLock(profiles, anySessionLocked, now);

        Assert.False(isHardLocked, "Profiles without schedule rules should not trigger scheduled lockdown.");
    }

    [Fact]
    public void ManualLockedSession_TriggersLock_RegardlessOfSchedule()
    {
        // Monday 8:30 PM (outside schedule)
        var mondayEvening = new DateTime(2026, 11, 2, 20, 30, 0);

        var profiles = new List<Profile>
        {
            new Profile
            {
                Id = 1,
                Name = "WorkFocus",
                Enabled = true,
                Schedule = new List<ScheduleRule>
                {
                    new ScheduleRule
                    {
                        Days = DaysOfWeek.Mon,
                        StartMinute = 540,
                        EndMinute = 1020
                    }
                }
            }
        };

        bool anySessionLocked = true;
        bool isHardLocked = EvaluateHardLock(profiles, anySessionLocked, mondayEvening);

        Assert.True(isHardLocked, "Manual locked session must keep lockdown active even outside schedule hours.");
    }

    [Fact]
    public void OvernightSchedule_CorrectlyEvaluatesActiveDuringLateNight()
    {
        // Monday 23:30 (overnight schedule 22:00 -> 06:00 next day)
        var mondayLateNight = new DateTime(2026, 11, 2, 23, 30, 0);

        var profiles = new List<Profile>
        {
            new Profile
            {
                Id = 1,
                Name = "NightBlock",
                Enabled = true,
                Schedule = new List<ScheduleRule>
                {
                    new ScheduleRule
                    {
                        Days = DaysOfWeek.Mon,
                        StartMinute = 1320, // 22:00
                        EndMinute = 360     // 06:00
                    }
                }
            }
        };

        bool isHardLocked = EvaluateHardLock(profiles, anySessionLocked: false, mondayLateNight);
        Assert.True(isHardLocked, "Overnight schedule should trigger lock during active night window.");
    }

    [Fact]
    public void WorkerSource_ContainsScheduledLockdownChecksInBothLocations()
    {
        // Find Worker.cs relative to test assembly or repository root
        string currentDir = AppContext.BaseDirectory;
        string? repoRoot = null;
        var dir = new DirectoryInfo(currentDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Blocker.sln")))
            {
                repoRoot = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(repoRoot);
        string workerCsPath = Path.Combine(repoRoot!, "src", "Blocker.Service", "Worker.cs");
        Assert.True(File.Exists(workerCsPath), $"Worker.cs not found at {workerCsPath}");

        string source = File.ReadAllText(workerCsPath);

        // Verify anyHardLocked is defined and used for process & service lockdown
        string expectedPattern = "bool anyScheduleLocked = allProfiles.Any(p => p.Schedule != null && p.Schedule.Count > 0 && ScheduleLogic.IsActive(p.Schedule, localNow));";
        int matchCount = CountOccurrences(source, expectedPattern);

        Assert.True(matchCount >= 2, $"Expected at least 2 occurrences of anyScheduleLocked logic in Worker.cs, found {matchCount}");
        Assert.Contains("bool anyHardLocked = anySessionLocked || anyScheduleLocked;", source);

        // Verify decoupled checks exist in both locations (startup and periodic sync)
        Assert.Contains("if (anyHardLocked)", source);
        Assert.Contains("if (!SecurityHardener.IsLockedDown)", source);
        Assert.Contains("if (!SecurityHardener.IsServiceLockedDown)", source);
        Assert.Contains("if (SecurityHardener.IsLockedDown)", source);
        Assert.Contains("if (SecurityHardener.IsServiceLockedDown)", source);

        int procLockdownCount = CountOccurrences(source, "if (!SecurityHardener.IsLockedDown)");
        int svcLockdownCount = CountOccurrences(source, "if (!SecurityHardener.IsServiceLockedDown)");
        int procUnlockCount = CountOccurrences(source, "if (SecurityHardener.IsLockedDown)");
        int svcUnlockCount = CountOccurrences(source, "if (SecurityHardener.IsServiceLockedDown)");

        Assert.True(procLockdownCount >= 2, $"Expected at least 2 occurrences of !SecurityHardener.IsLockedDown in Worker.cs, found {procLockdownCount}");
        Assert.True(svcLockdownCount >= 2, $"Expected at least 2 occurrences of !SecurityHardener.IsServiceLockedDown in Worker.cs, found {svcLockdownCount}");
        Assert.True(procUnlockCount >= 2, $"Expected at least 2 occurrences of SecurityHardener.IsLockedDown unlock checks in Worker.cs, found {procUnlockCount}");
        Assert.True(svcUnlockCount >= 2, $"Expected at least 2 occurrences of SecurityHardener.IsServiceLockedDown unlock checks in Worker.cs, found {svcUnlockCount}");
    }

    [Fact]
    public void DecoupledLockdown_WhenServiceLockdownInitiallyFails_AllowsRetryingServiceLockdown()
    {
        SecurityHardener.ResetStateForTesting();
        var mockRunner = new TestServiceSecurityRunner();
        SecurityHardener.ServiceSecurityRunner = mockRunner;

        try
        {
            // Simulate Cycle 1: anyHardLocked is true.
            // Service runner fails (e.g., transient sc.exe timeout or error).
            mockRunner.ShouldFail = true;
            bool anyHardLocked = true;

            // Worker's decoupled lockdown step (Cycle 1):
            if (anyHardLocked)
            {
                if (!SecurityHardener.IsLockedDown)
                {
                    SecurityHardener.LockdownCurrentProcess(out _);
                }

                if (!SecurityHardener.IsServiceLockedDown)
                {
                    SecurityHardener.LockdownService(out _);
                }
            }

            // Verification after Cycle 1:
            // Process lockdown succeeded and set IsLockedDown = true.
            // Service lockdown failed, so IsServiceLockedDown remained false.
            Assert.True(SecurityHardener.IsLockedDown, "Process DACL should be locked down.");
            Assert.False(SecurityHardener.IsServiceLockedDown, "Service DACL should not be locked down yet due to transient failure.");
            Assert.Equal(1, mockRunner.SdsetCallCount);

            // In the previous coupled implementation (if (anyHardLocked && !SecurityHardener.IsLockedDown)),
            // IsLockedDown == true prevented the entire block from ever running again, silently abandoning service lockdown.
            // In this decoupled implementation, !SecurityHardener.IsServiceLockedDown is true, permitting a retry.

            // Simulate Cycle 2: anyHardLocked is still true. Transient service error is resolved.
            mockRunner.ShouldFail = false;

            if (anyHardLocked)
            {
                if (!SecurityHardener.IsLockedDown)
                {
                    SecurityHardener.LockdownCurrentProcess(out _);
                }

                if (!SecurityHardener.IsServiceLockedDown)
                {
                    SecurityHardener.LockdownService(out _);
                }
            }

            // Verification after Cycle 2:
            // Process lockdown was NOT redundantly re-run (IsLockedDown remained true).
            // Service lockdown WAS retried and succeeded (IsServiceLockedDown is now true).
            Assert.True(SecurityHardener.IsLockedDown, "Process DACL remains locked down.");
            Assert.True(SecurityHardener.IsServiceLockedDown, "Service DACL should now be locked down on retry.");
            Assert.Equal(2, mockRunner.SdsetCallCount);

            // Simulate Cycle 3: anyHardLocked is still true.
            // Both are already locked down, so no redundant calls should occur.
            if (anyHardLocked)
            {
                if (!SecurityHardener.IsLockedDown)
                {
                    SecurityHardener.LockdownCurrentProcess(out _);
                }

                if (!SecurityHardener.IsServiceLockedDown)
                {
                    SecurityHardener.LockdownService(out _);
                }
            }

            Assert.Equal(2, mockRunner.SdsetCallCount);

            // Simulate Cycle 4: Unlock when anyHardLocked is false. Both should be restored.
            anyHardLocked = false;
            if (anyHardLocked)
            {
                // lockdown branch
            }
            else
            {
                if (SecurityHardener.IsLockedDown)
                {
                    SecurityHardener.UnlockCurrentProcess(out _);
                }
                if (SecurityHardener.IsServiceLockedDown)
                {
                    SecurityHardener.UnlockService(out _);
                }
            }

            Assert.False(SecurityHardener.IsLockedDown, "Process DACL should be restored.");
            Assert.False(SecurityHardener.IsServiceLockedDown, "Service DACL should be restored.");
        }
        finally
        {
            SecurityHardener.UnlockCurrentProcess(out _);
            SecurityHardener.ResetStateForTesting();
        }
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    private sealed class TestServiceSecurityRunner : IServiceSecurityRunner
    {
        public bool ShouldFail { get; set; }
        public int SdsetCallCount { get; private set; }
        public int SdshowCallCount { get; private set; }
        private string _currentSddl = SecurityHardener.UnlockedServiceSddl;

        public ServiceProcessResult RunSc(string[] arguments, int timeoutMs = 3000)
        {
            if (arguments.Length > 0 && arguments[0] == "sdset")
            {
                SdsetCallCount++;
                if (ShouldFail)
                {
                    return new ServiceProcessResult(
                        Success: false,
                        ExitCode: 5,
                        StandardOutput: string.Empty,
                        StandardError: "[SC] OpenService FAILED 5: Access is denied."
                    );
                }
                if (arguments.Length > 2)
                {
                    _currentSddl = arguments[2];
                }
                return new ServiceProcessResult(
                    Success: true,
                    ExitCode: 0,
                    StandardOutput: "[SC] SetServiceObjectSecurity SUCCESS",
                    StandardError: string.Empty
                );
            }

            if (arguments.Length > 0 && arguments[0] == "sdshow")
            {
                SdshowCallCount++;
                return new ServiceProcessResult(
                    Success: true,
                    ExitCode: 0,
                    StandardOutput: _currentSddl,
                    StandardError: string.Empty
                );
            }

            return new ServiceProcessResult(
                Success: true,
                ExitCode: 0,
                StandardOutput: string.Empty,
                StandardError: string.Empty
            );
        }
    }
}
