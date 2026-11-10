using System;
using System.Collections.Generic;
using System.Linq;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Sankalpa.Tests;

public class BrowserGraceNotificationTests : IDisposable
{
    private class MockBlockStore : IBlockStore
    {
        public List<BlockEvent> RecordedEvents { get; } = new();

        public Profile? GetProfileByName(string name) => null;
        public Profile? GetProfileById(int id) => null;
        public List<Profile> ListProfiles() => new();
        public Profile SaveProfile(Profile profile) => profile;
        public bool RenameProfile(int id, string newName) => true;
        public bool DeleteProfileByName(string name) => true;
        public bool DeleteProfile(int id) => true;
        public void DisableAllProfiles() { }

        public BlockSession? GetActiveSession() => null;
        public BlockSession? GetSessionById(int id) => null;
        public BlockSession StartSession(int profileId, long durationSeconds, bool locked) => new();
        public void EndActiveSession() { }
        public void EndSession(int sessionId) { }

        public bool IsEmergencyUnlockPresent() => false;
        public void ConsumeEmergencyUnlock() { }
        public bool CleanHosts() => true;

        public bool ServiceElevated { get; set; }
        public bool HostsBlockingOk { get; set; } = true;
        public HostsState HostsState { get; set; } = HostsState.Idle;
        public int HostsDomainCount { get; set; }
        public string? HostsLastError { get; set; }
        public List<string> GetCurrentHostsDomains() => new();

        public void RecordBlockEvent(BlockEvent evt)
        {
            RecordedEvents.Add(evt);
        }

        public List<BlockEvent> GetBlockEvents(long sinceId) => RecordedEvents.Where(e => e.Id > sinceId).ToList();

        public FallbackPlanner FallbackPlanner { get; } = new();
        public FallbackPlanResult? LastFallbackPlan { get; set; }

        public DnsConfig GetDnsConfig() => new();
        public void SaveDnsConfig(DnsConfig config) { }
    }

    public BrowserGraceNotificationTests()
    {
        BrowserGraceNotifier.Reset();
        WindowsNotificationHelper.UsableWindowCheckOverride = null;
    }

    public void Dispose()
    {
        BrowserGraceNotifier.Reset();
        WindowsNotificationHelper.UsableWindowCheckOverride = null;
    }

    [Fact]
    public void SettleDelay_SuppressesWarning_DuringFirstThreeSeconds()
    {
        var db = new MockBlockStore();
        WindowsNotificationHelper.UsableWindowCheckOverride = exe => true;

        var plan = new FallbackPlanResult
        {
            GracePeriodBrowsers = new List<string> { "msedge.exe" },
            GracePeriodRemainingSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["msedge.exe"] = 60
            }
        };

        // T = 0s: first sighting
        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);
        Assert.Empty(db.RecordedEvents);

        // T = 1s (59s remaining): in settle delay
        plan.GracePeriodRemainingSeconds["msedge.exe"] = 59;
        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);
        Assert.Empty(db.RecordedEvents);

        // T = 2s (58s remaining): in settle delay
        plan.GracePeriodRemainingSeconds["msedge.exe"] = 58;
        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);
        Assert.Empty(db.RecordedEvents);

        // T = 3s (57s remaining): in settle delay
        plan.GracePeriodRemainingSeconds["msedge.exe"] = 57;
        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);
        Assert.Empty(db.RecordedEvents);
    }

    [Fact]
    public void SettleDelay_AllowsWarning_AfterFourSecondsContinuousUnhealthiness()
    {
        var db = new MockBlockStore();
        WindowsNotificationHelper.UsableWindowCheckOverride = exe => true;

        var plan = new FallbackPlanResult
        {
            GracePeriodBrowsers = new List<string> { "msedge.exe" },
            GracePeriodRemainingSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["msedge.exe"] = 56 // 4s elapsed (60 - 56 = 4)
            }
        };

        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);

        // Settle delay (4s) satisfied -> initial warning dispatched
        Assert.Single(db.RecordedEvents);
        var ev = db.RecordedEvents[0];
        Assert.Equal("BrowserGrace", ev.Kind);
        Assert.Equal("msedge.exe", ev.Name);
        Assert.True(ev.ServiceDispatched);
        Assert.Contains("56s", ev.ProfileName);
    }

    [Fact]
    public void KillDeadline_StrictlyAnchoredAtSixtySeconds_DespiteSettleDelay()
    {
        var clock = new TestMonotonicClock(1_000_000);
        var planner = new FallbackPlanner(clock);
        var profiles = new List<Profile>
        {
            new() { Name = "Focus", WebRules = new List<WebRule> { new() { Pattern = "example.com", Allow = false } } }
        };

        // T = 0s: first sighting
        var p0 = planner.Plan(profiles, new[] { "msedge.exe" });
        Assert.Contains("msedge.exe", p0.GracePeriodBrowsers);
        Assert.Empty(p0.UnauthorizedBrowserProcesses);

        // T = 4s: settle delay elapsed
        clock.AdvanceSeconds(4);
        var p4 = planner.Plan(profiles, new[] { "msedge.exe" });
        Assert.Contains("msedge.exe", p4.GracePeriodBrowsers);
        Assert.Equal(56, p4.GracePeriodRemainingSeconds["msedge.exe"]);
        Assert.Empty(p4.UnauthorizedBrowserProcesses);

        // T = 59s: 1s before kill deadline
        clock.AdvanceSeconds(55);
        var p59 = planner.Plan(profiles, new[] { "msedge.exe" });
        Assert.Contains("msedge.exe", p59.GracePeriodBrowsers);
        Assert.Equal(1, p59.GracePeriodRemainingSeconds["msedge.exe"]);
        Assert.Empty(p59.UnauthorizedBrowserProcesses);

        // T = 60s: EXACT expiration time (kill deadline has NOT moved to 64s!)
        clock.AdvanceSeconds(1);
        var p60 = planner.Plan(profiles, new[] { "msedge.exe" });
        Assert.Empty(p60.GracePeriodBrowsers);
        Assert.Contains("msedge.exe", p60.UnauthorizedBrowserProcesses);
        Assert.True(p60.FallbackActive);
    }

    [Fact]
    public void RapidClose_WithinOneSecond_NeverShowsNotification()
    {
        var db = new MockBlockStore();

        // Edge opened at T = 0s with usable window
        WindowsNotificationHelper.UsableWindowCheckOverride = exe => true;
        var plan = new FallbackPlanResult
        {
            GracePeriodBrowsers = new List<string> { "msedge.exe" },
            GracePeriodRemainingSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["msedge.exe"] = 60
            }
        };

        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);
        Assert.Empty(db.RecordedEvents); // Suppressed by settle delay

        // User closes Edge ~1s later: window is gone!
        // Background processes keep "msedge.exe" in running list, but no USABLE window exists
        WindowsNotificationHelper.UsableWindowCheckOverride = exe => false;
        plan.GracePeriodRemainingSeconds["msedge.exe"] = 56; // T = 4s elapsed

        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);

        // Even though settle delay passed, no USABLE window exists -> ZERO notifications
        Assert.Empty(db.RecordedEvents);
    }

    [Fact]
    public void BackgroundProcess_WithoutUsableWindow_SuppressesCountdownToasts()
    {
        var db = new MockBlockStore();

        // Simulate Edge background process (e.g. startup boost / crashpad) with no visible top-level window
        WindowsNotificationHelper.UsableWindowCheckOverride = exe => false;

        var plan = new FallbackPlanResult
        {
            GracePeriodBrowsers = new List<string> { "msedge.exe" },
            GracePeriodRemainingSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["msedge.exe"] = 30 // Intermediate milestone reached
            }
        };

        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);
        Assert.Empty(db.RecordedEvents);

        plan.GracePeriodRemainingSeconds["msedge.exe"] = 10; // Final milestone reached
        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);
        Assert.Empty(db.RecordedEvents);
    }

    [Fact]
    public void Notification_Retracted_WhenBrowserBecomesHealthy()
    {
        var db = new MockBlockStore();
        WindowsNotificationHelper.UsableWindowCheckOverride = exe => true;

        var plan = new FallbackPlanResult
        {
            GracePeriodBrowsers = new List<string> { "msedge.exe" },
            GracePeriodRemainingSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["msedge.exe"] = 56
            }
        };

        // Milestone notification dispatched
        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);
        Assert.Single(db.RecordedEvents);
        Assert.True(BrowserGraceNotifier.IsTrackedInGrace("msedge.exe"));

        // Extension sends heartbeat -> browser becomes healthy -> GracePeriodBrowsers is now empty
        plan.GracePeriodBrowsers.Clear();
        plan.GracePeriodRemainingSeconds.Clear();

        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);

        // Browser removed from tracking and retraction triggered
        Assert.False(BrowserGraceNotifier.IsTrackedInGrace("msedge.exe"));
    }

    [Fact]
    public void MilestoneTransitions_FiresInitialIntermediateAndFinal_ExactlyOnceEach()
    {
        var db = new MockBlockStore();
        WindowsNotificationHelper.UsableWindowCheckOverride = exe => true;

        var plan = new FallbackPlanResult
        {
            GracePeriodBrowsers = new List<string> { "chrome.exe" },
            GracePeriodRemainingSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["chrome.exe"] = 56 // Initial milestone (>= 4s elapsed)
            }
        };

        // 1. Initial milestone fires
        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);
        Assert.Single(db.RecordedEvents);
        Assert.Contains("56s", db.RecordedEvents[0].ProfileName);

        // Repeat call at 50s: still in initial milestone -> does NOT refire
        plan.GracePeriodRemainingSeconds["chrome.exe"] = 50;
        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);
        Assert.Single(db.RecordedEvents);

        // 2. Intermediate milestone (30s) fires
        plan.GracePeriodRemainingSeconds["chrome.exe"] = 30;
        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);
        Assert.Equal(2, db.RecordedEvents.Count);
        Assert.Contains("30s", db.RecordedEvents[1].ProfileName);

        // Repeat call at 20s: still in intermediate -> does NOT refire
        plan.GracePeriodRemainingSeconds["chrome.exe"] = 20;
        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);
        Assert.Equal(2, db.RecordedEvents.Count);

        // 3. Final milestone (10s) fires
        plan.GracePeriodRemainingSeconds["chrome.exe"] = 10;
        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);
        Assert.Equal(3, db.RecordedEvents.Count);
        Assert.Contains("10s", db.RecordedEvents[2].ProfileName);

        // Repeat call at 5s: still in final -> does NOT refire
        plan.GracePeriodRemainingSeconds["chrome.exe"] = 5;
        BrowserGraceNotifier.ProcessGraceNotifications(plan, db);
        Assert.Equal(3, db.RecordedEvents.Count);
    }
}
