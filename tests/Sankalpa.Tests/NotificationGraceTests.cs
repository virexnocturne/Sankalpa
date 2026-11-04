using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class NotificationGraceTests
{
    private static List<Profile> CreateWebBlockedProfile(string domain = "example.com")
    {
        return new List<Profile>
        {
            new()
            {
                Name = "FocusProfile",
                WebRules = new List<WebRule>
                {
                    new() { Pattern = domain, Allow = false }
                }
            }
        };
    }

    [Fact]
    public void GracePeriod_DefaultIsSixtySeconds()
    {
        Assert.Equal(60_000, FallbackPlanner.DefaultGracePeriodMs);

        var planner = new FallbackPlanner();
        var profiles = CreateWebBlockedProfile();
        var startTime = DateTime.UtcNow;

        // T = 0: Browser launched without extension
        var plan = planner.Plan(profiles, new[] { "chrome.exe" }, startTime);

        Assert.Contains("chrome.exe", plan.GracePeriodBrowsers);
        Assert.True(plan.GracePeriodRemainingSeconds.ContainsKey("chrome.exe"));
        Assert.Equal(60, plan.GracePeriodRemainingSeconds["chrome.exe"]);
        Assert.Empty(plan.UnauthorizedBrowserProcesses);
        Assert.False(plan.FallbackActive);
    }

    [Fact]
    public void GracePeriod_FullSixtySecondsLifespan_TicksDownAndExpires()
    {
        var clock = new TestMonotonicClock(1_000_000);
        var planner = new FallbackPlanner(clock);
        var profiles = CreateWebBlockedProfile();

        // T = 0: Launch
        var p0 = planner.Plan(profiles, new[] { "msedge.exe" });
        Assert.Contains("msedge.exe", p0.GracePeriodBrowsers);
        Assert.Equal(60, p0.GracePeriodRemainingSeconds["msedge.exe"]);
        Assert.Empty(p0.UnauthorizedBrowserProcesses);
        Assert.False(p0.FallbackActive);

        // T = 30s: Intermediate (30s remaining)
        clock.AdvanceSeconds(30);
        var p30 = planner.Plan(profiles, new[] { "msedge.exe" });
        Assert.Contains("msedge.exe", p30.GracePeriodBrowsers);
        Assert.Equal(30, p30.GracePeriodRemainingSeconds["msedge.exe"]);
        Assert.Empty(p30.UnauthorizedBrowserProcesses);

        // T = 50s: Final warning threshold (10s remaining)
        clock.AdvanceSeconds(20);
        var p50 = planner.Plan(profiles, new[] { "msedge.exe" });
        Assert.Contains("msedge.exe", p50.GracePeriodBrowsers);
        Assert.Equal(10, p50.GracePeriodRemainingSeconds["msedge.exe"]);
        Assert.Empty(p50.UnauthorizedBrowserProcesses);

        // T = 59s: 1s remaining
        clock.AdvanceSeconds(9);
        var p59 = planner.Plan(profiles, new[] { "msedge.exe" });
        Assert.Contains("msedge.exe", p59.GracePeriodBrowsers);
        Assert.Equal(1, p59.GracePeriodRemainingSeconds["msedge.exe"]);
        Assert.Empty(p59.UnauthorizedBrowserProcesses);

        // T = 61s: Expiration! Must be unauthorized and marked for termination
        clock.AdvanceSeconds(2);
        var p61 = planner.Plan(profiles, new[] { "msedge.exe" });
        Assert.Empty(p61.GracePeriodBrowsers);
        Assert.Contains("msedge.exe", p61.UnauthorizedBrowserProcesses);
        Assert.True(p61.FallbackActive);
        Assert.Contains("msedge.exe", p61.UnauthorizedReasons.Keys);
    }

    [Fact]
    public void ImmediateNotification_OnLaunchWithoutExtension()
    {
        var clock = new TestMonotonicClock(500_000);
        var planner = new FallbackPlanner(clock);
        var profiles = CreateWebBlockedProfile("reddit.com");

        // The very first second Chrome is opened without extension
        var plan = planner.Plan(profiles, new[] { "chrome.exe" });

        // Immediately captured into GracePeriodBrowsers with 60s remaining
        Assert.Single(plan.GracePeriodBrowsers);
        Assert.Equal("chrome.exe", plan.GracePeriodBrowsers[0]);
        Assert.Equal(60, plan.GracePeriodRemainingSeconds["chrome.exe"]);

        // Verify notification log key logic matches Worker.cs
        int remSec = plan.GracePeriodRemainingSeconds["chrome.exe"];
        string logKey = remSec <= 10 ? "chrome.exe:final" : (remSec <= 30 ? "chrome.exe:intermediate" : "chrome.exe:initial");
        Assert.Equal("chrome.exe:initial", logKey);
    }

    [Fact]
    public void Notification_WhenExtensionDisabled_HeartbeatLost()
    {
        var clock = new TestMonotonicClock(100_000);
        var planner = new FallbackPlanner(clock);
        var profiles = CreateWebBlockedProfile("youtube.com");

        // Chrome runs normally for 10 minutes with extension sending heartbeats
        for (int i = 0; i < 300; i++)
        {
            clock.AdvanceSeconds(2);
            planner.RecordHeartbeat("chrome.exe");
        }

        var healthyPlan = planner.Plan(profiles, new[] { "chrome.exe" });
        Assert.Empty(healthyPlan.GracePeriodBrowsers);
        Assert.Empty(healthyPlan.UnauthorizedBrowserProcesses);

        // User turns off / disables extension in Chrome: heartbeats cease
        // 5s later: heartbeat timeout has not elapsed (<= 10s grace before detection)
        clock.AdvanceSeconds(5);
        var p5 = planner.Plan(profiles, new[] { "chrome.exe" });
        Assert.Empty(p5.GracePeriodBrowsers);

        // 11s later (total 11s without heartbeat): Extension is detected missing/disabled!
        clock.AdvanceSeconds(6);
        var p11 = planner.Plan(profiles, new[] { "chrome.exe" });
        Assert.Contains("chrome.exe", p11.GracePeriodBrowsers);
        Assert.Equal(60, p11.GracePeriodRemainingSeconds["chrome.exe"]);

        // Simulate Worker notification cycle over the 60s grace period
        var simulatedLoggedNotifications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Initial notification upon entering grace
        int rem = p11.GracePeriodRemainingSeconds["chrome.exe"];
        string key1 = rem <= 10 ? "chrome.exe:final" : (rem <= 30 ? "chrome.exe:intermediate" : "chrome.exe:initial");
        Assert.True(simulatedLoggedNotifications.Add(key1));
        Assert.Equal("chrome.exe:initial", key1);

        // 2. Advance to 30s remaining
        clock.AdvanceSeconds(30);
        var p41 = planner.Plan(profiles, new[] { "chrome.exe" });
        rem = p41.GracePeriodRemainingSeconds["chrome.exe"];
        string key2 = rem <= 10 ? "chrome.exe:final" : (rem <= 30 ? "chrome.exe:intermediate" : "chrome.exe:initial");
        Assert.True(simulatedLoggedNotifications.Add(key2));
        Assert.Equal("chrome.exe:intermediate", key2);

        // 3. Advance to 10s remaining
        clock.AdvanceSeconds(20);
        var p61 = planner.Plan(profiles, new[] { "chrome.exe" });
        rem = p61.GracePeriodRemainingSeconds["chrome.exe"];
        string key3 = rem <= 10 ? "chrome.exe:final" : (rem <= 30 ? "chrome.exe:intermediate" : "chrome.exe:initial");
        Assert.True(simulatedLoggedNotifications.Add(key3));
        Assert.Equal("chrome.exe:final", key3);

        // Verify exactly 3 distinct warning levels were triggered
        Assert.Equal(3, simulatedLoggedNotifications.Count);
        Assert.Contains("chrome.exe:initial", simulatedLoggedNotifications);
        Assert.Contains("chrome.exe:intermediate", simulatedLoggedNotifications);
        Assert.Contains("chrome.exe:final", simulatedLoggedNotifications);

        // User re-enables extension before expiry: grace cleared immediately!
        planner.RecordHeartbeat("chrome.exe");
        var reEnabledPlan = planner.Plan(profiles, new[] { "chrome.exe" });
        Assert.Empty(reEnabledPlan.GracePeriodBrowsers);
        Assert.Empty(reEnabledPlan.UnauthorizedBrowserProcesses);
    }

    [Fact]
    public void Notification_WhenIncognitoDisallowed()
    {
        var clock = new TestMonotonicClock(100_000);
        var planner = new FallbackPlanner(clock);
        var profiles = CreateWebBlockedProfile("blocked.com");

        // Chrome is sending heartbeats, but incognitoAllowed is false!
        planner.RecordHeartbeat("chrome.exe", incognitoAllowed: false);
        var plan = planner.Plan(profiles, new[] { "chrome.exe" });

        // Enters grace immediately even though heartbeat is active
        Assert.Contains("chrome.exe", plan.GracePeriodBrowsers);
        Assert.Equal(60, plan.GracePeriodRemainingSeconds["chrome.exe"]);

        // Advance 61s past grace with incognito still disallowed
        clock.AdvanceSeconds(61);
        planner.RecordHeartbeat("chrome.exe", incognitoAllowed: false);
        var expiredPlan = planner.Plan(profiles, new[] { "chrome.exe" });

        Assert.Contains("chrome.exe", expiredPlan.UnauthorizedBrowserProcesses);
        Assert.Contains("Allow in Incognito/Private", expiredPlan.UnauthorizedReasons["chrome.exe"]);
    }

    [Fact]
    public void KillNotification_UponExpiry_AndResetTracking()
    {
        var clock = new TestMonotonicClock(200_000);
        var planner = new FallbackPlanner(clock);
        var profiles = CreateWebBlockedProfile("gaming.com");

        planner.Plan(profiles, new[] { "chrome.exe" });
        clock.AdvanceSeconds(65);

        var plan = planner.Plan(profiles, new[] { "chrome.exe" });
        Assert.Contains("chrome.exe", plan.UnauthorizedBrowserProcesses);
        Assert.True(plan.FallbackActive);

        // Service terminates process and resets tracking
        planner.ResetBrowserTracking("chrome.exe");

        // Process is closed/killed, no longer running
        var planAfterKill = planner.Plan(profiles, Array.Empty<string>());
        Assert.Empty(planAfterKill.UnauthorizedBrowserProcesses);
        Assert.Empty(planAfterKill.GracePeriodBrowsers);
        Assert.False(planAfterKill.FallbackActive);

        // Relaunching starts fresh 60s grace
        clock.AdvanceSeconds(5);
        var planRelaunch = planner.Plan(profiles, new[] { "chrome.exe" });
        Assert.Contains("chrome.exe", planRelaunch.GracePeriodBrowsers);
        Assert.Equal(60, planRelaunch.GracePeriodRemainingSeconds["chrome.exe"]);
    }

    [Fact]
    public void MultipleBrowsers_IndependentGraceTracking()
    {
        var clock = new TestMonotonicClock(100_000);
        var planner = new FallbackPlanner(clock);
        var profiles = CreateWebBlockedProfile("social.com");

        // Browser 1: msedge.exe starts at T=0 without extension
        planner.Plan(profiles, new[] { "msedge.exe" });

        // T=20s: Browser 2: chrome.exe starts with active extension
        clock.AdvanceSeconds(20);
        planner.RecordHeartbeat("chrome.exe");
        planner.Plan(profiles, new[] { "msedge.exe", "chrome.exe" });

        // T=30s: Browser 3: brave.exe starts without extension
        clock.AdvanceSeconds(10);
        var plan30 = planner.Plan(profiles, new[] { "msedge.exe", "chrome.exe", "brave.exe" });

        // Edge elapsed 30s -> 30s remaining
        Assert.Equal(30, plan30.GracePeriodRemainingSeconds["msedge.exe"]);
        // Brave elapsed 0s -> 60s remaining
        Assert.Equal(60, plan30.GracePeriodRemainingSeconds["brave.exe"]);
        // Chrome is healthy -> not in grace
        Assert.DoesNotContain("chrome.exe", plan30.GracePeriodBrowsers);

        // Advance to T=65s: Edge (65s total) expired, Brave (35s total) has 25s remaining
        clock.AdvanceSeconds(35);
        planner.RecordHeartbeat("chrome.exe"); // keep Chrome healthy
        var plan65 = planner.Plan(profiles, new[] { "msedge.exe", "chrome.exe", "brave.exe" });

        Assert.Contains("msedge.exe", plan65.UnauthorizedBrowserProcesses);
        Assert.DoesNotContain("msedge.exe", plan65.GracePeriodBrowsers);

        Assert.Contains("brave.exe", plan65.GracePeriodBrowsers);
        Assert.Equal(25, plan65.GracePeriodRemainingSeconds["brave.exe"]);

        Assert.DoesNotContain("chrome.exe", plan65.UnauthorizedBrowserProcesses);
        Assert.DoesNotContain("chrome.exe", plan65.GracePeriodBrowsers);
    }

    [Fact]
    public void NotificationThrottler_PermitsImmediateIntermediateFinalAndKill()
    {
        var throttler = new NotificationThrottler(TimeSpan.FromSeconds(4));
        var now = DateTime.UtcNow;

        // 1. Initial warning at 60s
        Assert.True(throttler.ShouldNotify("chrome.exe", now, "BrowserGrace"));
        // Immediate repeat within 4s is throttled
        Assert.False(throttler.ShouldNotify("chrome.exe", now.AddSeconds(1), "BrowserGrace"));

        // 2. Intermediate warning at 30s (30s later) -> allowed
        Assert.True(throttler.ShouldNotify("chrome.exe", now.AddSeconds(30), "BrowserGrace"));

        // 3. Final warning at 10s (20s later) -> allowed
        Assert.True(throttler.ShouldNotify("chrome.exe", now.AddSeconds(50), "BrowserGrace"));

        // 4. Kill notification upon expiry at 0s (10s later, kind "BrowserKill") -> allowed
        Assert.True(throttler.ShouldNotify("chrome.exe", now.AddSeconds(60), "BrowserKill"));

        // Different browser -> independent throttle
        Assert.True(throttler.ShouldNotify("msedge.exe", now.AddSeconds(60), "BrowserGrace"));
    }

    [Fact]
    public void BlockEvent_ServiceDispatched_SerializesAndRetainsValue()
    {
        var evTrue = new BlockEvent
        {
            Id = 1,
            TimeUtc = DateTime.UtcNow,
            Kind = "BrowserGrace",
            Name = "chrome.exe",
            ProfileName = "Extension required",
            ServiceDispatched = true
        };

        var evFalse = new BlockEvent
        {
            Id = 2,
            TimeUtc = DateTime.UtcNow,
            Kind = "BrowserKill",
            Name = "msedge.exe",
            ProfileName = "Terminated",
            ServiceDispatched = false
        };

        string jsonTrue = System.Text.Json.JsonSerializer.Serialize(evTrue);
        string jsonFalse = System.Text.Json.JsonSerializer.Serialize(evFalse);

        var restoredTrue = System.Text.Json.JsonSerializer.Deserialize<BlockEvent>(jsonTrue);
        var restoredFalse = System.Text.Json.JsonSerializer.Deserialize<BlockEvent>(jsonFalse);

        Assert.NotNull(restoredTrue);
        Assert.True(restoredTrue.ServiceDispatched);

        Assert.NotNull(restoredFalse);
        Assert.False(restoredFalse.ServiceDispatched);
    }

    [Fact]
    public async Task StressTest_ConcurrentHeartbeatsAndGracePlanning()
    {
        var clock = new TestMonotonicClock(100_000);
        var planner = new FallbackPlanner(clock);
        var profiles = CreateWebBlockedProfile("stress-test.org");
        string[] browsers = { "chrome.exe", "msedge.exe", "brave.exe", "opera.exe" };

        var cts = new System.Threading.CancellationTokenSource();

        // 4 worker threads generating heartbeats
        var heartbeatTasks = browsers.Select(b => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                planner.RecordHeartbeat(b);
            }
        })).ToList();

        // Planning thread simulating rapid 1s checks
        var planTask = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                var plan = planner.Plan(profiles, browsers);
                Assert.NotNull(plan);
            }
        });

        await planTask;
        cts.Cancel();
        await Task.WhenAll(heartbeatTasks);
    }

    [Fact]
    public void ServiceNotification_Win32EntryPoints_AreResolvableWithoutEntryPointNotFoundException()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Verify advapi32.dll exports
        IntPtr hAdv = System.Runtime.InteropServices.NativeLibrary.Load("advapi32.dll");
        Assert.NotEqual(IntPtr.Zero, hAdv);
        Assert.True(System.Runtime.InteropServices.NativeLibrary.TryGetExport(hAdv, "DuplicateTokenEx", out _));
        Assert.True(System.Runtime.InteropServices.NativeLibrary.TryGetExport(hAdv, "CreateProcessAsUserW", out _));
        Assert.True(System.Runtime.InteropServices.NativeLibrary.TryGetExport(hAdv, "OpenProcessToken", out _));
        System.Runtime.InteropServices.NativeLibrary.Free(hAdv);

        // Verify kernel32.dll exports
        IntPtr hKernel = System.Runtime.InteropServices.NativeLibrary.Load("kernel32.dll");
        Assert.NotEqual(IntPtr.Zero, hKernel);
        Assert.True(System.Runtime.InteropServices.NativeLibrary.TryGetExport(hKernel, "WTSGetActiveConsoleSessionId", out _));
        Assert.True(System.Runtime.InteropServices.NativeLibrary.TryGetExport(hKernel, "OpenProcess", out _));
        Assert.True(System.Runtime.InteropServices.NativeLibrary.TryGetExport(hKernel, "CloseHandle", out _));
        System.Runtime.InteropServices.NativeLibrary.Free(hKernel);

        // Verify wtsapi32.dll exports
        IntPtr hWts = System.Runtime.InteropServices.NativeLibrary.Load("wtsapi32.dll");
        Assert.NotEqual(IntPtr.Zero, hWts);
        Assert.True(System.Runtime.InteropServices.NativeLibrary.TryGetExport(hWts, "WTSQueryUserToken", out _));
        System.Runtime.InteropServices.NativeLibrary.Free(hWts);

        // Verify userenv.dll exports
        IntPtr hUserenv = System.Runtime.InteropServices.NativeLibrary.Load("userenv.dll");
        Assert.NotEqual(IntPtr.Zero, hUserenv);
        Assert.True(System.Runtime.InteropServices.NativeLibrary.TryGetExport(hUserenv, "CreateEnvironmentBlock", out _));
        Assert.True(System.Runtime.InteropServices.NativeLibrary.TryGetExport(hUserenv, "DestroyEnvironmentBlock", out _));
        System.Runtime.InteropServices.NativeLibrary.Free(hUserenv);
    }

    [Fact]
    public void UnsupportedBrowsers_IncludesNewlyCoveredBrowsers()
    {
        Assert.True(BrowserCatalog.IsUnsupportedBrowser("chromium.exe"));
        Assert.True(BrowserCatalog.IsUnsupportedBrowser("thorium.exe"));
        Assert.True(BrowserCatalog.IsUnsupportedBrowser("mullvadbrowser.exe"));
        Assert.True(BrowserCatalog.IsUnsupportedBrowser("iridium.exe"));

        var filtered = BrowserCatalog.FilterAllBrowserProcesses(new[] { "chromium", "thorium", "notepad" });
        Assert.Contains("chromium.exe", filtered);
        Assert.Contains("thorium.exe", filtered);
        Assert.DoesNotContain("notepad.exe", filtered);
    }

    [Fact]
    public void TaskkillPath_ResolvesSystemDirectoryExecutable()
    {
        string systemDir = Environment.SystemDirectory;
        string taskkillPath = System.IO.Path.Combine(systemDir, "taskkill.exe");
        if (OperatingSystem.IsWindows())
        {
            Assert.True(System.IO.File.Exists(taskkillPath), $"taskkill.exe should exist at {taskkillPath}");
        }
    }
}