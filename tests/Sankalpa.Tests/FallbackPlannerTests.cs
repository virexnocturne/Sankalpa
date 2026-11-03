using System;
using System.Collections.Generic;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class FallbackPlannerTests
{
    [Fact]
    public void NoRunningBrowsers_FallbackNotActive()
    {
        var planner = new FallbackPlanner();
        var profiles = new List<Profile>
        {
            new() { WebRules = new List<WebRule> { new() { Pattern = "example.com", Allow = false } } }
        };

        var plan = planner.Plan(profiles, Array.Empty<string>(), DateTime.UtcNow);
        Assert.False(plan.FallbackActive);
        Assert.Empty(plan.FallbackDomains);
    }

    [Fact]
    public void RunningBrowser_HealthyHeartbeat_FallbackNotActive()
    {
        var planner = new FallbackPlanner();
        var now = DateTime.UtcNow;
        planner.RecordHeartbeat("chrome.exe", now);

        var profiles = new List<Profile>
        {
            new() { WebRules = new List<WebRule> { new() { Pattern = "example.com", Allow = false } } }
        };

        var plan = planner.Plan(profiles, new[] { "chrome.exe" }, now.AddSeconds(2));
        Assert.False(plan.FallbackActive);
        Assert.Empty(plan.FallbackDomains);
    }

    [Fact]
    public void NewlyStartedBrowser_WithinGracePeriod_FallbackNotActive()
    {
        var planner = new FallbackPlanner();
        var startTime = DateTime.UtcNow;

        var profiles = new List<Profile>
        {
            new() { WebRules = new List<WebRule> { new() { Pattern = "example.com", Allow = false } } }
        };

        // First seen at startTime
        planner.Plan(profiles, new[] { "chrome.exe" }, startTime);

        // At +5 seconds (still in grace period) without heartbeat
        var plan = planner.Plan(profiles, new[] { "chrome.exe" }, startTime.AddSeconds(5));
        Assert.False(plan.FallbackActive);
    }

    [Fact]
    public void MissingBrowser_AfterGracePeriod_ActivatesFallbackAndPopulatesDomains()
    {
        var planner = new FallbackPlanner();
        var startTime = DateTime.UtcNow;

        var profiles = new List<Profile>
        {
            new()
            {
                Name = "Work",
                WebRules = new List<WebRule>
                {
                    new() { Pattern = "example.com", Allow = false },
                    new() { Pattern = "example.org/path", Allow = false },
                    new() { Pattern = "*.glob.com", Allow = false }, // should be skipped
                    new() { Pattern = "example.com/unblock", Allow = true } // exception ignored
                }
            }
        };

        // First seen
        planner.Plan(profiles, new[] { "chrome.exe" }, startTime);

        // At +65 seconds without heartbeat -> missing (grace period is 60s)!
        var plan = planner.Plan(profiles, new[] { "chrome.exe" }, startTime.AddSeconds(65));
        Assert.True(plan.FallbackActive);
        Assert.Contains("Browser extension not responding in Google Chrome", plan.FallbackReason);

        Assert.Contains("example.com", plan.FallbackDomains);
        Assert.Contains("www.example.com", plan.FallbackDomains);
        Assert.Contains("example.org", plan.FallbackDomains);
        Assert.Contains("www.example.org", plan.FallbackDomains);

        Assert.DoesNotContain("*.glob.com", plan.FallbackDomains);
        Assert.DoesNotContain("glob.com", plan.FallbackDomains);
    }

    [Fact]
    public void MissingBrowser_BlockAllWebsites_SetsBlockAllUncovered()
    {
        var planner = new FallbackPlanner();
        var startTime = DateTime.UtcNow;

        var profiles = new List<Profile>
        {
            new()
            {
                Name = "TotalBlock",
                BlockAllWebsites = true
            }
        };

        planner.Plan(profiles, new[] { "msedge.exe" }, startTime);
        var plan = planner.Plan(profiles, new[] { "msedge.exe" }, startTime.AddSeconds(65));

        Assert.True(plan.FallbackActive);
        Assert.True(plan.BlockAllUncovered);
    }

    [Fact]
    public void FallbackDeactivates_WhenHeartbeatReceived()
    {
        var planner = new FallbackPlanner();
        var startTime = DateTime.UtcNow;

        var profiles = new List<Profile>
        {
            new() { WebRules = new List<WebRule> { new() { Pattern = "example.com", Allow = false } } }
        };

        planner.Plan(profiles, new[] { "chrome.exe" }, startTime);
        var missingPlan = planner.Plan(profiles, new[] { "chrome.exe" }, startTime.AddSeconds(65));
        Assert.True(missingPlan.FallbackActive);

        // Heartbeat received
        var now = startTime.AddSeconds(66);
        planner.RecordHeartbeat("chrome.exe", now);

        var healthyPlan = planner.Plan(profiles, new[] { "chrome.exe" }, now.AddSeconds(1));
        Assert.False(healthyPlan.FallbackActive);
        Assert.Empty(healthyPlan.FallbackDomains);
    }

    [Fact]
    public void MonotonicClock_MovingWallClock_DoesNotAffectGraceOrHeartbeat()
    {
        var clock = new TestMonotonicClock(100_000);
        var planner = new FallbackPlanner(clock);

        var profiles = new List<Profile>
        {
            new() { WebRules = new List<WebRule> { new() { Pattern = "example.com", Allow = false } } }
        };

        // 1. First seen at 100,000 ms
        planner.Plan(profiles, new[] { "chrome.exe" });

        // 2. Advance 30s (still in grace period of 60s)
        clock.AdvanceSeconds(30);
        var inGracePlan = planner.Plan(profiles, new[] { "chrome.exe" });
        Assert.False(inGracePlan.FallbackActive, "Should be in grace period at 30s");

        // 3. Advance to 65s (grace period expired, no heartbeat -> fallback active)
        clock.AdvanceSeconds(35);
        var fallbackPlan = planner.Plan(profiles, new[] { "chrome.exe" });
        Assert.True(fallbackPlan.FallbackActive, "Fallback should activate after 60s without heartbeat");

        // 4. Heartbeat arrives
        planner.RecordHeartbeat("chrome.exe");
        var restoredPlan = planner.Plan(profiles, new[] { "chrome.exe" });
        Assert.False(restoredPlan.FallbackActive, "Heartbeat restores extension status");

        // 5. Advance 12s without heartbeat -> enters 60s grace period (does not immediately kill)
        clock.AdvanceSeconds(12);
        var inGracePlan2 = planner.Plan(profiles, new[] { "chrome.exe" });
        Assert.False(inGracePlan2.FallbackActive, "Should enter grace period when heartbeats stop");
        Assert.Contains("chrome.exe", inGracePlan2.GracePeriodBrowsers);

        // 6. Advance past 60s grace period -> fallback triggers and browser unauthorized
        clock.AdvanceSeconds(61);
        var expiredPlan = planner.Plan(profiles, new[] { "chrome.exe" });
        Assert.True(expiredPlan.FallbackActive, "Fallback triggers after grace period expires");
        Assert.Contains("chrome.exe", expiredPlan.UnauthorizedBrowserProcesses);
    }

    [Fact]
    public void BrowserGracePeriod_WhenExtensionDisabledAfterLongRunningSession_GivesFull60SecondsGracePeriod()
    {
        var clock = new TestMonotonicClock(100_000);
        var planner = new FallbackPlanner(clock);

        var profiles = new List<Profile>
        {
            new() { WebRules = new List<WebRule> { new() { Pattern = "example.com", Allow = false } } }
        };

        // Edge has been running for 10 minutes (600 seconds) with healthy heartbeats
        planner.Plan(profiles, new[] { "msedge.exe" });
        clock.AdvanceSeconds(600);
        planner.RecordHeartbeat("msedge.exe");

        var runningPlan = planner.Plan(profiles, new[] { "msedge.exe" });
        Assert.False(runningPlan.FallbackActive);
        Assert.Empty(runningPlan.UnauthorizedBrowserProcesses);
        Assert.Empty(runningPlan.GracePeriodBrowsers);

        // User turns off extension in Edge: heartbeats stop
        // 12 seconds later (heartbeat expired), Edge must receive 60s grace period and NOT close immediately
        clock.AdvanceSeconds(12);
        var gracePlan = planner.Plan(profiles, new[] { "msedge.exe" });
        Assert.False(gracePlan.FallbackActive);
        Assert.Empty(gracePlan.UnauthorizedBrowserProcesses);
        Assert.Contains("msedge.exe", gracePlan.GracePeriodBrowsers);
        Assert.InRange(gracePlan.GracePeriodRemainingSeconds["msedge.exe"], 1, 60);

        // If user re-enables within 60s, grace period clears
        clock.AdvanceSeconds(20);
        planner.RecordHeartbeat("msedge.exe");
        var restoredPlan = planner.Plan(profiles, new[] { "msedge.exe" });
        Assert.Empty(restoredPlan.GracePeriodBrowsers);
        Assert.Empty(restoredPlan.UnauthorizedBrowserProcesses);

        // If user turns off extension again and 60s completely elapses: Edge is unauthorized
        clock.AdvanceSeconds(12); // heartbeat expires, grace starts
        planner.Plan(profiles, new[] { "msedge.exe" });
        clock.AdvanceSeconds(61); // 60s grace expires
        var expiredPlan = planner.Plan(profiles, new[] { "msedge.exe" });
        Assert.True(expiredPlan.FallbackActive);
        Assert.Contains("msedge.exe", expiredPlan.UnauthorizedBrowserProcesses);
    }

    [Fact]
    public void FilterActiveBrowserProcesses_OnlyBrowsersWithVisibleWindowsCount()
    {
        var processes = new List<ProcessWindowInfo>
        {
            new("chrome.exe", false), // background helper process without window
            new("chrome.exe", false), // another background process
            new("msedge.exe", true),  // Edge with visible window
            new("notepad.exe", true), // Non-browser app with window
            new("brave.exe", false),  // Brave background process
            new("vivaldi.exe", true)  // Vivaldi with window
        };

        var filtered = BrowserCatalog.FilterActiveBrowserProcesses(processes);

        Assert.Equal(2, filtered.Count);
        Assert.Contains("msedge.exe", filtered);
        Assert.Contains("vivaldi.exe", filtered);
        Assert.DoesNotContain("chrome.exe", filtered);
        Assert.DoesNotContain("brave.exe", filtered);
        Assert.DoesNotContain("notepad.exe", filtered);
    }

    [Fact]
    public async System.Threading.Tasks.Task FallbackPlanner_ConcurrencyTest_ThreadSafeAgainstParallelHeartbeatsAndPlan()
    {
        var clock = new TestMonotonicClock(100_000);
        var planner = new FallbackPlanner(clock);
        var profiles = new List<Profile>
        {
            new() { WebRules = new List<WebRule> { new() { Pattern = "example.com", Allow = false } } }
        };

        var cts = new System.Threading.CancellationTokenSource();
        var tasks = new List<System.Threading.Tasks.Task>();

        // Heartbeat recording threads
        string[] browsers = { "chrome.exe", "msedge.exe", "brave.exe", "chromium.exe" };
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            tasks.Add(System.Threading.Tasks.Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    planner.RecordHeartbeat(browsers[idx % browsers.Length]);
                }
            }));
        }

        // Planning loop thread
        tasks.Add(System.Threading.Tasks.Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                var plan = planner.Plan(profiles, new[] { "chrome.exe", "msedge.exe" });
                Assert.NotNull(plan);
            }
        }));

        // Run planning loop to completion
        await tasks.Last();
        cts.Cancel();

        // Ensure all heartbeat threads terminate cleanly without exceptions
        await System.Threading.Tasks.Task.WhenAll(tasks);
    }

    [Fact]
    public void FallbackDomains_HostsNormalizationAndExpansion_NormalizesWwwAndSkipsIpAddresses()
    {
        var clock = new TestMonotonicClock(100_000);
        var planner = new FallbackPlanner(clock);

        var profiles = new List<Profile>
        {
            new()
            {
                Name = "MixedRules",
                WebRules = new List<WebRule>
                {
                    new() { Pattern = "www.example.com", Allow = false },
                    new() { Pattern = "m.example.org/path", Allow = false },
                    new() { Pattern = "192.168.1.1", Allow = false },         // IP -> must skip
                    new() { Pattern = "127.0.0.1/admin", Allow = false },      // IP -> must skip
                    new() { Pattern = "::1", Allow = false },                  // IPv6 -> must skip
                    new() { Pattern = "invalid domain #", Allow = false },     // invalid -> must skip
                    new() { Pattern = "*.glob.com", Allow = false }            // wildcard -> must skip
                }
            }
        };

        // First seen
        planner.Plan(profiles, new[] { "chrome.exe" });

        // Missing browser past grace period to trigger fallback (60s)
        clock.AdvanceSeconds(65);
        var plan = planner.Plan(profiles, new[] { "chrome.exe" });

        Assert.True(plan.FallbackActive);

        // www.example.com normalized to example.com and expanded
        Assert.Contains("example.com", plan.FallbackDomains);
        Assert.Contains("www.example.com", plan.FallbackDomains);
        Assert.Contains("m.example.com", plan.FallbackDomains);

        // m.example.org normalized to example.org and expanded
        Assert.Contains("example.org", plan.FallbackDomains);
        Assert.Contains("www.example.org", plan.FallbackDomains);
        Assert.Contains("m.example.org", plan.FallbackDomains);

        // IP addresses must never be present
        Assert.DoesNotContain("192.168.1.1", plan.FallbackDomains);
        Assert.DoesNotContain("127.0.0.1", plan.FallbackDomains);
        Assert.DoesNotContain("::1", plan.FallbackDomains);
    }

    [Fact]
    public void MissingBrowser_After10sGracePeriod_MarksAsUnauthorizedBrowserToKill()
    {
        var clock = new TestMonotonicClock(100_000);
        var planner = new FallbackPlanner(clock);

        var profiles = new List<Profile>
        {
            new() { WebRules = new List<WebRule> { new() { Pattern = "youtube.com", Allow = false } } }
        };

        // First seen at 100,000ms
        planner.Plan(profiles, new[] { "chrome.exe" });

        // Advance 30s (in grace period) -> not marked to kill
        clock.AdvanceSeconds(30);
        var inGrace = planner.Plan(profiles, new[] { "chrome.exe" });
        Assert.Empty(inGrace.UnauthorizedBrowserProcesses);

        // Advance past 60s grace period (65s total) without heartbeat -> marked to kill!
        clock.AdvanceSeconds(35);
        var expired = planner.Plan(profiles, new[] { "chrome.exe" });
        Assert.Contains("chrome.exe", expired.UnauthorizedBrowserProcesses);
        Assert.True(expired.FallbackActive);
    }

    [Fact]
    public void BrowserRunning_IncognitoDisallowed_After10sGracePeriod_MarksAsUnauthorizedBrowserToKill()
    {
        var clock = new TestMonotonicClock(100_000);
        var planner = new FallbackPlanner(clock);

        var profiles = new List<Profile>
        {
            new() { WebRules = new List<WebRule> { new() { Pattern = "reddit.com", Allow = false } } }
        };

        // First seen at 100,000ms
        planner.Plan(profiles, new[] { "chrome.exe" });

        // Advance past 60s grace period
        clock.AdvanceSeconds(65);

        // Extension heartbeat is actively arriving, but incognito is disabled!
        planner.RecordHeartbeat("chrome.exe", incognitoAllowed: false);

        var plan = planner.Plan(profiles, new[] { "chrome.exe" });

        Assert.Contains("chrome.exe", plan.UnauthorizedBrowserProcesses);
        Assert.True(plan.FallbackActive);
        Assert.Contains("Incognito", plan.FallbackReason);
    }

    [Fact]
    public void UnsupportedBrowser_WhileWebBlocksActive_ImmediatelyMarkedToKill()
    {
        var clock = new TestMonotonicClock(100_000);
        var planner = new FallbackPlanner(clock);

        var profiles = new List<Profile>
        {
            new() { WebRules = new List<WebRule> { new() { Pattern = "netflix.com", Allow = false } } }
        };

        // Tor and Safari are unsupported browsers
        var plan = planner.Plan(profiles, new[] { "tor.exe", "safari.exe" });

        Assert.Contains("tor.exe", plan.UnauthorizedBrowserProcesses);
        Assert.Contains("safari.exe", plan.UnauthorizedBrowserProcesses);
        Assert.True(plan.FallbackActive);
    }

    [Fact]
    public void NewlySupportedBrowsers_ZenAndOpera_EnterGracePeriodBeforeKill()
    {
        var clock = new TestMonotonicClock(100_000);
        var planner = new FallbackPlanner(clock);

        var profiles = new List<Profile>
        {
            new() { WebRules = new List<WebRule> { new() { Pattern = "reddit.com", Allow = false } } }
        };

        // Initially in grace period
        var plan1 = planner.Plan(profiles, new[] { "zen.exe", "opera.exe" });
        Assert.Contains("zen.exe", plan1.GracePeriodBrowsers);
        Assert.Contains("opera.exe", plan1.GracePeriodBrowsers);
        Assert.Empty(plan1.UnauthorizedBrowserProcesses);

        // Advance 61 seconds past grace period
        clock.AdvanceSeconds(61);
        var plan2 = planner.Plan(profiles, new[] { "zen.exe", "opera.exe" });
        Assert.Contains("zen.exe", plan2.UnauthorizedBrowserProcesses);
        Assert.Contains("opera.exe", plan2.UnauthorizedBrowserProcesses);
    }

    [Fact]
    public void NoWebBlocksActive_DoesNotMarkBrowsersToKill()
    {
        var clock = new TestMonotonicClock(100_000);
        var planner = new FallbackPlanner(clock);

        // Only app blocking, no web rules
        var profiles = new List<Profile>
        {
            new() { BlockedProcesses = new List<string> { "game.exe" } }
        };

        // Advance 20 seconds with unextended browser
        planner.Plan(profiles, new[] { "chrome.exe" });
        clock.AdvanceSeconds(20);
        var plan = planner.Plan(profiles, new[] { "chrome.exe" });

        Assert.Empty(plan.UnauthorizedBrowserProcesses);
        Assert.False(plan.FallbackActive);
    }

    [Fact]
    public void MissingBrowser_WithBlockedDomains_PopulatesFallbackDomains()
    {
        var clock = new TestMonotonicClock(100_000);
        var planner = new FallbackPlanner(clock);

        var profiles = new List<Profile>
        {
            new()
            {
                Name = "Default",
                BlockedDomains = new List<string> { "nhentai.com", "nhentai.net" }
            }
        };

        // First seen at 100,000ms
        planner.Plan(profiles, new[] { "msedge.exe" });

        // Advance 65 seconds without heartbeat
        clock.AdvanceSeconds(65);
        var plan = planner.Plan(profiles, new[] { "msedge.exe" });

        Assert.True(plan.FallbackActive);
        Assert.Contains("msedge.exe", plan.UnauthorizedBrowserProcesses);
        Assert.Contains("nhentai.com", plan.FallbackDomains);
        Assert.Contains("www.nhentai.com", plan.FallbackDomains);
        Assert.Contains("nhentai.net", plan.FallbackDomains);
        Assert.Contains("www.nhentai.net", plan.FallbackDomains);
    }

    [Fact]
    public void BrowserCatalog_FilterAllBrowserProcesses_MatchesRegardlessOfWindowOrExtension()
    {
        var rawNames = new[] { "msedge", "CHROME.EXE", "notepad", "tor", "calc.exe" };
        var filtered = BrowserCatalog.FilterAllBrowserProcesses(rawNames);

        Assert.Contains("msedge.exe", filtered);
        Assert.Contains("chrome.exe", filtered);
        Assert.Contains("tor.exe", filtered);
        Assert.DoesNotContain("notepad.exe", filtered);
        Assert.DoesNotContain("calc.exe", filtered);
    }
}
