using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Blocker.Core;
using Xunit;

namespace Sankalpa.Security.Tests;

public class MockServiceSecurityRunner : IServiceSecurityRunner
{
    public Func<string[], int, ServiceProcessResult>? Handler { get; set; }
    public List<(string[] Arguments, int TimeoutMs)> Invocations { get; } = new();

    public ServiceProcessResult RunSc(string[] arguments, int timeoutMs = 3000)
    {
        Invocations.Add((arguments, timeoutMs));
        if (Handler != null)
        {
            return Handler(arguments, timeoutMs);
        }

        return new ServiceProcessResult(
            Success: true,
            ExitCode: 0,
            StandardOutput: string.Empty,
            StandardError: string.Empty
        );
    }
}

public class SecurityHardenerTests : IDisposable
{
    public SecurityHardenerTests()
    {
        SecurityHardener.ResetStateForTesting();
    }

    public void Dispose()
    {
        SecurityHardener.ResetStateForTesting();
    }

    [Fact]
    public void LockdownService_SuccessfulLockdown_ExitCodeZeroAndSddlVerified_SetsStateTrue()
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(false);

        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) =>
            {
                if (args[0] == "sdset")
                {
                    Assert.Equal("SankalpaService", args[1]);
                    Assert.Equal(SecurityHardener.LockedServiceSddl, args[2]);
                    return new ServiceProcessResult(true, 0, "[SC] SetServiceObjectSecurity SUCCESS", "");
                }
                if (args[0] == "sdshow")
                {
                    Assert.Equal("SankalpaService", args[1]);
                    return new ServiceProcessResult(true, 0, $"\r\n\r\n{SecurityHardener.LockedServiceSddl}\r\n", "");
                }
                return new ServiceProcessResult(false, 1, "", "Unknown command");
            }
        };

        bool success = SecurityHardener.LockdownService(out string? error, runner);

        Assert.True(success);
        Assert.Null(error);
        Assert.True(SecurityHardener.IsServiceLockedDown);
        Assert.Equal(2, runner.Invocations.Count);
        Assert.Equal("sdset", runner.Invocations[0].Arguments[0]);
        Assert.Equal("sdshow", runner.Invocations[1].Arguments[0]);
    }

    [Fact]
    public void UnlockService_SuccessfulUnlock_ExitCodeZeroAndSddlVerified_SetsStateFalse()
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(true);

        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) =>
            {
                if (args[0] == "sdset")
                {
                    Assert.Equal("SankalpaService", args[1]);
                    Assert.Equal(SecurityHardener.UnlockedServiceSddl, args[2]);
                    return new ServiceProcessResult(true, 0, "[SC] SetServiceObjectSecurity SUCCESS", "");
                }
                if (args[0] == "sdshow")
                {
                    Assert.Equal("SankalpaService", args[1]);
                    return new ServiceProcessResult(true, 0, $"\r\n{SecurityHardener.UnlockedServiceSddl}\r\n", "");
                }
                return new ServiceProcessResult(false, 1, "", "Unknown command");
            }
        };

        bool success = SecurityHardener.UnlockService(out string? error, runner);

        Assert.True(success);
        Assert.Null(error);
        Assert.False(SecurityHardener.IsServiceLockedDown);
        Assert.Equal(2, runner.Invocations.Count);
        Assert.Equal("sdset", runner.Invocations[0].Arguments[0]);
        Assert.Equal("sdshow", runner.Invocations[1].Arguments[0]);
    }

    [Theory]
    [InlineData(5, "Access is denied.")]
    [InlineData(1060, "The specified service does not exist as an installed service.")]
    public void LockdownService_NonZeroExitCode_FailsAndStateUnchanged(int exitCode, string message)
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(false);

        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) => new ServiceProcessResult(false, exitCode, "", message)
        };

        bool success = SecurityHardener.LockdownService(out string? error, runner);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains(exitCode.ToString(), error);
        Assert.Contains(message, error);
        Assert.False(SecurityHardener.IsServiceLockedDown);
    }

    [Fact]
    public void UnlockService_NonZeroExitCode_FailsAndStateUnchanged()
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(true);

        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) => new ServiceProcessResult(false, 5, "", "Access is denied.")
        };

        bool success = SecurityHardener.UnlockService(out string? error, runner);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("5", error);
        Assert.Contains("Access is denied", error);
        Assert.True(SecurityHardener.IsServiceLockedDown);
    }

    [Fact]
    public void LockdownService_SdshowNonZeroExitCode_FailsAndStateUnchanged()
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(false);

        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) =>
            {
                if (args[0] == "sdset") return new ServiceProcessResult(true, 0, "", "");
                return new ServiceProcessResult(false, 1060, "", "Service does not exist");
            }
        };

        bool success = SecurityHardener.LockdownService(out string? error, runner);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("1060", error);
        Assert.False(SecurityHardener.IsServiceLockedDown);
    }

    [Fact]
    public void LockdownService_ProcessCreationFailure_FailsAndStateUnchanged()
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(false);

        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) => new ServiceProcessResult(
                Success: false,
                ExitCode: -1,
                StandardOutput: string.Empty,
                StandardError: "Failed to create process for sc.exe: File not found",
                ProcessCreationFailed: true
            )
        };

        bool success = SecurityHardener.LockdownService(out string? error, runner);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("Failed to create process", error);
        Assert.False(SecurityHardener.IsServiceLockedDown);
    }

    [Fact]
    public void UnlockService_ProcessCreationFailure_FailsAndStateUnchanged()
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(true);

        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) => new ServiceProcessResult(
                Success: false,
                ExitCode: -1,
                StandardOutput: string.Empty,
                StandardError: "Failed to start sc.exe: Process.Start returned null.",
                ProcessCreationFailed: true
            )
        };

        bool success = SecurityHardener.UnlockService(out string? error, runner);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("Process.Start returned null", error);
        Assert.True(SecurityHardener.IsServiceLockedDown);
    }

    [Fact]
    public void LockdownService_ProcessTimeout_FailsAndStateUnchanged()
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(false);

        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) => new ServiceProcessResult(
                Success: false,
                ExitCode: -1,
                StandardOutput: string.Empty,
                StandardError: "sc.exe sdset timed out after 3000ms",
                TimedOut: true
            )
        };

        bool success = SecurityHardener.LockdownService(out string? error, runner);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("timed out", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(SecurityHardener.IsServiceLockedDown);
    }

    [Fact]
    public void UnlockService_ProcessTimeout_FailsAndStateUnchanged()
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(true);

        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) => new ServiceProcessResult(
                Success: false,
                ExitCode: -1,
                StandardOutput: string.Empty,
                StandardError: "sc.exe sdset timed out after 3000ms",
                TimedOut: true
            )
        };

        bool success = SecurityHardener.UnlockService(out string? error, runner);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("timed out", error, StringComparison.OrdinalIgnoreCase);
        Assert.True(SecurityHardener.IsServiceLockedDown);
    }

    [Fact]
    public void LockdownService_VerificationFailure_ExitCodeZeroWrongSddl_FailsAndStateUnchanged()
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(false);

        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) =>
            {
                if (args[0] == "sdset")
                {
                    return new ServiceProcessResult(true, 0, "[SC] SetServiceObjectSecurity SUCCESS", "");
                }
                if (args[0] == "sdshow")
                {
                    // Exit code is 0, but returned SDDL does not match LockedServiceSddl!
                    return new ServiceProcessResult(true, 0, SecurityHardener.UnlockedServiceSddl, "");
                }
                return new ServiceProcessResult(false, 1, "", "");
            }
        };

        bool success = SecurityHardener.LockdownService(out string? error, runner);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("verification failed", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(SecurityHardener.IsServiceLockedDown);
    }

    [Fact]
    public void UnlockService_VerificationFailure_ExitCodeZeroWrongSddl_FailsAndStateUnchanged()
    {
        SecurityHardener.ResetServiceLockdownStateForTesting(true);

        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) =>
            {
                if (args[0] == "sdset")
                {
                    return new ServiceProcessResult(true, 0, "[SC] SetServiceObjectSecurity SUCCESS", "");
                }
                if (args[0] == "sdshow")
                {
                    // Exit code is 0, but returned SDDL still shows locked SDDL!
                    return new ServiceProcessResult(true, 0, SecurityHardener.LockedServiceSddl, "");
                }
                return new ServiceProcessResult(false, 1, "", "");
            }
        };

        bool success = SecurityHardener.UnlockService(out string? error, runner);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("verification failed", error, StringComparison.OrdinalIgnoreCase);
        Assert.True(SecurityHardener.IsServiceLockedDown);
    }

    [Fact]
    public void StateRemainingUnchanged_AcrossAllFailureModes()
    {
        // 1. Initial state is false. Any failed lockdown preserves false.
        SecurityHardener.ResetServiceLockdownStateForTesting(false);

        var failingRunners = new[]
        {
            new MockServiceSecurityRunner { Handler = (args, _) => new ServiceProcessResult(false, 1, "", "Access denied") },
            new MockServiceSecurityRunner { Handler = (args, _) => new ServiceProcessResult(false, -1, "", "Timeout", TimedOut: true) },
            new MockServiceSecurityRunner { Handler = (args, _) => new ServiceProcessResult(false, -1, "", "Null proc", ProcessCreationFailed: true) },
            new MockServiceSecurityRunner { Handler = (args, _) => args[0] == "sdset" ? new ServiceProcessResult(true, 0, "", "") : new ServiceProcessResult(true, 0, "D:(A;;FA;;;WD)", "") }
        };

        foreach (var runner in failingRunners)
        {
            bool result = SecurityHardener.LockdownService(out string? error, runner);
            Assert.False(result);
            Assert.NotNull(error);
            Assert.False(SecurityHardener.IsServiceLockedDown, "Lockdown failure should not modify IsServiceLockedDown from false");
        }

        // 2. Initial state is true. Any failed unlock preserves true.
        SecurityHardener.ResetServiceLockdownStateForTesting(true);

        foreach (var runner in failingRunners)
        {
            bool result = SecurityHardener.UnlockService(out string? error, runner);
            Assert.False(result);
            Assert.NotNull(error);
            Assert.True(SecurityHardener.IsServiceLockedDown, "Unlock failure should not modify IsServiceLockedDown from true");
        }
    }

    [Fact]
    public void SecurityHardener_UsesConfiguredServiceSecurityRunner()
    {
        SecurityHardener.ResetStateForTesting();

        var runner = new MockServiceSecurityRunner
        {
            Handler = (args, _) =>
            {
                if (args[0] == "sdset") return new ServiceProcessResult(true, 0, "", "");
                if (args[0] == "sdshow") return new ServiceProcessResult(true, 0, SecurityHardener.LockedServiceSddl, "");
                return new ServiceProcessResult(false, 1, "", "");
            }
        };

        SecurityHardener.ServiceSecurityRunner = runner;
        bool success = SecurityHardener.LockdownService(out string? error);

        Assert.True(success);
        Assert.Null(error);
        Assert.True(SecurityHardener.IsServiceLockedDown);
        Assert.NotEmpty(runner.Invocations);
    }

    [Fact]
    public void DefaultServiceSecurityRunner_ProcessTimeout_KillsHangingProcess()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var runner = new DefaultServiceSecurityRunner("powershell.exe");
        var sw = Stopwatch.StartNew();
        var result = runner.RunSc(new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 10" }, timeoutMs: 300);
        sw.Stop();

        Assert.True(result.TimedOut);
        Assert.False(result.Success);
        Assert.Contains("timed out", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Process was not killed in time, elapsed: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void DefaultServiceSecurityRunner_ProcessStartFailure_ReportsFailure()
    {
        var runner = new DefaultServiceSecurityRunner("this_executable_does_not_exist_at_all.exe");
        var result = runner.RunSc(new[] { "arg1" });

        Assert.False(result.Success);
        Assert.True(result.ProcessCreationFailed);
        Assert.Contains("Failed to start", result.StandardError);
    }

    [Theory]
    [InlineData("D:(D;;0x0020;;;BA)(D;;0x0020;;;WD)", "D:(D;;0x0020;;;BA)(D;;0x0020;;;WD)", true)]
    [InlineData("  D:(D;;0x0020;;;BA)(D;;0x0020;;;WD)\r\n", "D:(D;;0x0020;;;BA)(D;;0x0020;;;WD)", true)]
    [InlineData("O:BAG:BAD:(D;;0x0020;;;BA)(D;;0x0020;;;WD)", "D:(D;;0x0020;;;BA)(D;;0x0020;;;WD)", true)]
    [InlineData("D:(D;;WP;;;BA)(D;;WP;;;WD)(D;;SD;;;BA)(D;;SD;;;WD)S:(AU;FA;CCDCLCSWRPWPDTLOSDRCWDWO;;;WD)", "D:(D;;0x0020;;;BA)(D;;0x0020;;;WD)(D;;0x00010000;;;BA)(D;;0x00010000;;;WD)", true)]
    [InlineData("D:(A;;FA;;;WD)", "D:(D;;0x0020;;;BA)(D;;0x0020;;;WD)", false)]
    [InlineData("", "D:(D;;0x0020;;;BA)(D;;0x0020;;;WD)", false)]
    public void VerifySddlMatch_MatchesCorrectly(string actual, string expected, bool shouldMatch)
    {
        bool matches = SecurityHardener.VerifySddlMatch(actual, expected);
        Assert.Equal(shouldMatch, matches);
    }
}
