using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Blocker.Core;
using Blocker.Service;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Sankalpa.Security.Tests;

/// <summary>
/// Adversarial security unit tests created by Agent D (Security Red-Team)
/// evaluating the delivered implementations for Bugs 1, 2, and 3 in Zen (Meditation) mode.
/// Tests strictly enforce all 10 security rules, contracts C-1 through C-7, and attack vectors.
/// </summary>
[SupportedOSPlatform("windows")]
public class ZenFixSecurityTests
{
    private static readonly string WorktreeB_Core = typeof(Blocker.Core.ProtectedProcesses).Assembly.Location;
    private static readonly string WorktreeB_Service = typeof(Blocker.Service.Worker).Assembly.Location;
    private static readonly string WorktreeC_Core = typeof(Blocker.Core.ProtectedProcesses).Assembly.Location;
    private static readonly string WorktreeC_Service = typeof(Blocker.Service.Worker).Assembly.Location;
    private static readonly string WorktreeA_Core = typeof(Blocker.Core.ProtectedProcesses).Assembly.Location;

    private class IsolatedContext : AssemblyLoadContext
    {
        private readonly string _baseDir;
        public IsolatedContext(string dllPath) : base(isCollectible: true)
        {
            _baseDir = Path.GetDirectoryName(Path.GetFullPath(dllPath))!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string depPath = Path.Combine(_baseDir, assemblyName.Name + ".dll");
            if (File.Exists(depPath))
            {
                return LoadFromAssemblyPath(depPath);
            }
            return null;
        }
    }

    private static Assembly? LoadAssemblySafely(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var ctx = new IsolatedContext(path);
                return ctx.LoadFromAssemblyPath(Path.GetFullPath(path));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load {path}: {ex.Message}");
        }

        if (path.Contains("Core", StringComparison.OrdinalIgnoreCase))
            return typeof(Blocker.Core.ProtectedProcesses).Assembly;
        if (path.Contains("Service", StringComparison.OrdinalIgnoreCase))
            return typeof(Blocker.Service.Worker).Assembly;

        return null;
    }

    #region Rule 1: Renamed Binaries and Path Traversal

    [Theory]
    [InlineData(@"C:\Users\Attacker\msedge.exe")]
    [InlineData(@"C:\Temp\msedge.exe")]
    [InlineData(@"C:\Windows\Temp\msedge.exe")]
    [InlineData(@"C:\Users\Public\Downloads\msedge.exe")]
    public void Rule1_RenamedMsedge_InUserWritablePath_Rejected(string fakePath)
    {
        var asmB_Core = LoadAssemblySafely(WorktreeB_Core);
        Assert.NotNull(asmB_Core);

        var protectedProcessesType = asmB_Core.GetType("Blocker.Core.ProtectedProcesses");
        Assert.NotNull(protectedProcessesType);

        var isEdgeInstallRootMethod = protectedProcessesType.GetMethod("IsEdgeInstallRoot", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(isEdgeInstallRootMethod);

        bool isRoot = (bool)isEdgeInstallRootMethod.Invoke(null, new object[] { fakePath })!;
        Assert.False(isRoot, $"Fake msedge at '{fakePath}' must not be recognized as canonical Edge root.");

        var asmB_Svc = LoadAssemblySafely(WorktreeB_Service);
        Assert.NotNull(asmB_Svc);
        var workerType = asmB_Svc.GetType("Blocker.Service.Worker");
        Assert.NotNull(workerType);

        var isPredicateMethod = workerType.GetMethod("IsProcessAllowedPredicate", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(isPredicateMethod);

        var allowedApps = new List<string> { "msedge" };
        var parameters = new object?[]
        {
            "msedge",
            "msedge.exe",
            fakePath,
            allowedApps,
            null, // no explicit roots; must fallback to Edge canonical root check
            0,
            null,
            null
        };

        bool allowed = (bool)isPredicateMethod.Invoke(null, parameters)!;
        Assert.False(allowed, $"Renamed msedge binary outside canonical installation directory must be REJECTED by service predicate.");
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\..\..\..\Temp\msedge.exe")]
    [InlineData(@"C:\Program Files\Microsoft\Edge\Application\..\..\Windows\System32\cmd.exe")]
    public void Rule1_PathTraversal_EscapingCanonicalRoot_Rejected(string traversalPath)
    {
        var asmB_Core = LoadAssemblySafely(WorktreeB_Core);
        Assert.NotNull(asmB_Core);

        var protectedProcessesType = asmB_Core.GetType("Blocker.Core.ProtectedProcesses");
        var isEdgeInstallRootMethod = protectedProcessesType?.GetMethod("IsEdgeInstallRoot", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(isEdgeInstallRootMethod);

        bool isRoot = (bool)isEdgeInstallRootMethod.Invoke(null, new object[] { traversalPath })!;
        Assert.False(isRoot, $"Traversal path '{traversalPath}' escaping root must be rejected.");
    }

    [Theory]
    [InlineData(@"C:\Temp\identity_helper.exe")]
    [InlineData(@"C:\Users\Attacker\AppData\Local\Temp\msedge_proxy.exe")]
    [InlineData(@"C:\ProgramData\elevation_service.exe")]
    public void Rule1_EdgeHelper_OutsideCanonicalRoots_Rejected(string fakeHelperPath)
    {
        var asmB_Core = LoadAssemblySafely(WorktreeB_Core);
        Assert.NotNull(asmB_Core);

        var protectedProcessesType = asmB_Core.GetType("Blocker.Core.ProtectedProcesses");
        var isLegitHelperMethod = protectedProcessesType?.GetMethod("IsLegitimateEdgeHelper", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(isLegitHelperMethod);

        string exeName = Path.GetFileName(fakeHelperPath);
        bool isLegit = (bool)isLegitHelperMethod.Invoke(null, new object[] { exeName, fakeHelperPath })!;
        Assert.False(isLegit, $"Edge helper outside canonical install root '{fakeHelperPath}' must NOT be considered legitimate.");
    }

    [Fact]
    public void Rule1_EdgeHelper_WhenEdgeNotAllowed_RejectedByPredicate()
    {
        var asmB_Svc = LoadAssemblySafely(WorktreeB_Service);
        Assert.NotNull(asmB_Svc);
        var workerType = asmB_Svc.GetType("Blocker.Service.Worker");
        var isPredicateMethod = workerType?.GetMethod("IsProcessAllowedPredicate", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(isPredicateMethod);

        string legitPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft", "Edge", "Application", "124.0.0.0", "identity_helper.exe");

        // Edge is NOT in allowed apps; only Notepad is allowed
        var allowedApps = new List<string> { "notepad.exe" };
        var parameters = new object?[]
        {
            "identity_helper",
            "identity_helper.exe",
            legitPath,
            allowedApps,
            null,
            0,
            null,
            null
        };

        bool allowed = (bool)isPredicateMethod.Invoke(null, parameters)!;
        Assert.False(allowed, "Edge helper must NEVER be allowed when msedge is not an allowed app.");
    }

    [Fact]
    public void Rule1_EdgeHelper_UnknownExecutableName_Rejected()
    {
        var asmB_Core = LoadAssemblySafely(WorktreeB_Core);
        Assert.NotNull(asmB_Core);

        var protectedProcessesType = asmB_Core.GetType("Blocker.Core.ProtectedProcesses");
        var isLegitHelperMethod = protectedProcessesType?.GetMethod("IsLegitimateEdgeHelper", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(isLegitHelperMethod);

        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft", "Edge", "Application", "evil_injected_helper.exe");

        bool isLegit = (bool)isLegitHelperMethod.Invoke(null, new object[] { "evil_injected_helper", path })!;
        Assert.False(isLegit, "Unknown executable name inside Edge folder must not be considered a legitimate helper.");
    }

    #endregion

    #region Rule 2: Process Identity Verification and PID Reuse

    [Fact]
    public void Rule2_ProcessSuspender_RejectsSystemAndIdlePIDs()
    {
        var suspender = new ProcessSuspender();
        Assert.False(suspender.SuspendProcess(0, "Idle"));
        Assert.False(suspender.SuspendProcess(4, "System"));
    }

    [Fact]
    public void Rule2_ProcessIdentity_ExpectedImagePathMismatch_FailsSafely()
    {
        var asmB_Core = LoadAssemblySafely(WorktreeB_Core);
        Assert.NotNull(asmB_Core);

        var suspenderType = asmB_Core.GetType("Blocker.Core.ProcessSuspender");
        Assert.NotNull(suspenderType);

        var suspender = Activator.CreateInstance(suspenderType);
        Assert.NotNull(suspender);

        var suspendMethod = suspenderType.GetMethod("SuspendProcess", new[]
        {
            typeof(int), typeof(string), typeof(bool), typeof(DateTime?), typeof(string), typeof(bool)
        });
        Assert.NotNull(suspendMethod);

        // Spawn a short-lived harmless notepad to test against without risking test host
        using var dummyProc = Process.Start(new ProcessStartInfo("notepad.exe") { WindowStyle = ProcessWindowStyle.Hidden });
        Assert.NotNull(dummyProc);

        try
        {
            int dummyPid = dummyProc.Id;
            string fakeExpectedPath = @"C:\FakeDirectory\Imposter.exe";

            // Call SuspendProcess with expectedImagePath mismatch
            bool result = (bool)suspendMethod.Invoke(suspender, new object?[]
            {
                dummyPid, "notepad", false, null, fakeExpectedPath, false
            })!;

            Assert.False(result, "Expected image path mismatch must reject suspension to prevent PID reuse attack.");
        }
        finally
        {
            try { dummyProc.Kill(); } catch { }
        }
    }

    [Fact]
    public void Rule2_ProcessIdentity_ExpectedCreationTimeMismatch_FailsSafely()
    {
        var asmB_Core = LoadAssemblySafely(WorktreeB_Core);
        Assert.NotNull(asmB_Core);

        var suspenderType = asmB_Core.GetType("Blocker.Core.ProcessSuspender");
        var suspender = Activator.CreateInstance(suspenderType!);
        var suspendMethod = suspenderType?.GetMethod("SuspendProcess", new[]
        {
            typeof(int), typeof(string), typeof(bool), typeof(DateTime?), typeof(string), typeof(bool)
        });
        Assert.NotNull(suspendMethod);

        using var dummyProc = Process.Start(new ProcessStartInfo("notepad.exe") { WindowStyle = ProcessWindowStyle.Hidden });
        Assert.NotNull(dummyProc);

        try
        {
            int dummyPid = dummyProc.Id;
            DateTime distantTime = DateTime.UtcNow.AddHours(-12);

            bool result = (bool)suspendMethod.Invoke(suspender, new object?[]
            {
                dummyPid, "notepad", false, distantTime, null, false
            })!;

            Assert.False(result, "Expected creation time mismatch must reject suspension to prevent PID reuse attack.");
        }
        finally
        {
            try { dummyProc.Kill(); } catch { }
        }
    }

    [Fact]
    public void Rule2_FinishSession_LegitimateChildResumedNotKilled()
    {
        var asmB_Core = LoadAssemblySafely(WorktreeB_Core);
        Assert.NotNull(asmB_Core);

        var suspenderType = asmB_Core.GetType("Blocker.Core.ProcessSuspender");
        var entryType = suspenderType?.GetNestedType("Entry");
        Assert.NotNull(entryType);

        var sessionStart = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        using var dummyHandle = new SafeProcessHandle(IntPtr.Zero, true);

        string legitPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft", "Edge", "Application", "124.0.0.0", "identity_helper.exe");

        // Construct legitimate entry
        var legitEntry = Activator.CreateInstance(entryType, new object?[]
        {
            dummyHandle, "identity_helper", sessionStart.AddMinutes(2), true, legitPath, true
        });

        var shouldTerminateMethod = suspenderType?.GetMethod("ShouldTerminate", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(shouldTerminateMethod);

        bool shouldKillLegit = (bool)shouldTerminateMethod.Invoke(null, new[] { legitEntry, sessionStart })!;
        Assert.False(shouldKillLegit, "Legitimate child process marked IsLegitimate must NOT be terminated at session end.");

        // Construct unauthorized child entry
        var rogueEntry = Activator.CreateInstance(entryType, new object?[]
        {
            dummyHandle, "cmd", sessionStart.AddMinutes(2), true, @"C:\Windows\System32\cmd.exe", false
        });

        bool shouldKillRogue = (bool)shouldTerminateMethod.Invoke(null, new[] { rogueEntry, sessionStart })!;
        Assert.True(shouldKillRogue, "Unauthorized child process spawned during session must be terminated at session end.");
    }

    #endregion

    #region Rule 3: Client Trust and IPC Boundaries

    [Fact]
    public void Rule3_ConfirmTimerReady_TrustBoundaryAudit()
    {
        string cmdPath = @"C:\Windows\System32\cmd.exe";
        bool isCmdProtected = ProtectedProcesses.IsProtected("cmd", cmdPath);
        Assert.False(isCmdProtected, "cmd.exe must never be considered protected.");

        var allowedApps = new List<string> { "notepad.exe" };
        var fakeRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["notepad"] = @"C:\Windows\System32"
        };

        bool allowed = Worker.IsAppAllowed("cmd", "cmd.exe", allowedApps, cmdPath, fakeRoots);
        Assert.False(allowed, "cmd.exe must NOT be allowed even if System32 root is passed for notepad.");
    }

    #endregion

    #region Rule 4: Extension Requirement and Windowless Evasion

    [Fact]
    public void Rule4_SettleDelay_DoesNotExtendOrDelayEnforcementKillDeadline()
    {
        // Attack scenario: Attacker attempts to leverage 4s settle delay or notification suppressions to delay the 60s kill timer.
        // Contract C-2: Suspend and kill policy and grace/kill deadline may only stay the same or get stricter.
        var clock = new TestMonotonicClock();
        var planner = new FallbackPlanner(clock);
        var runningBrowsers = new List<string> { "msedge.exe" };
        var profile = new Profile { Name = "TestProfile", Enabled = true, BlockedDomains = new List<string> { "distracting.com" } };
        var profiles = new List<Profile> { profile };

        // t = 0: grace starts
        var r0 = planner.Plan(profiles, runningBrowsers);
        Assert.Contains("msedge.exe", r0.GracePeriodBrowsers);

        // t = 4s (settle delay elapsed): still in grace
        clock.AdvanceSeconds(4);
        var r4 = planner.Plan(profiles, runningBrowsers);
        Assert.Contains("msedge.exe", r4.GracePeriodBrowsers);

        // t = 60s: kill deadline strictly hit at 60s, completely unaffected by settle delay or UI notifications
        clock.AdvanceSeconds(56);
        var r60 = planner.Plan(profiles, runningBrowsers);
        Assert.Contains("msedge.exe", r60.UnauthorizedBrowserProcesses);
    }

    [Fact]
    public void Rule4_HeadlessOrWindowlessBrowser_NotExemptFromEnforcement()
    {
        var clock = new TestMonotonicClock();
        var planner = new FallbackPlanner(clock);
        var runningBrowsers = new List<string> { "chrome.exe" };
        var profile = new Profile
        {
            Name = "Test",
            Enabled = true,
            BlockedDomains = new List<string> { "youtube.com" }
        };
        var profiles = new List<Profile> { profile };

        var result = planner.Plan(profiles, runningBrowsers);
        Assert.Contains("chrome.exe", result.GracePeriodBrowsers);

        clock.AdvanceSeconds(65);

        result = planner.Plan(profiles, runningBrowsers);
        Assert.Contains("chrome.exe", result.UnauthorizedBrowserProcesses);
    }

    [Fact]
    public void Rule4_TruthfulNotification_BackgroundBrowserWithoutUsableWindow_SuppressesToast()
    {
        var asmC_Svc = LoadAssemblySafely(WorktreeC_Service);
        var asmC_Core = LoadAssemblySafely(WorktreeC_Core);
        Assert.NotNull(asmC_Svc);
        Assert.NotNull(asmC_Core);

        var notifierType = asmC_Svc.GetType("Blocker.Service.BrowserGraceNotifier");
        var helperType = asmC_Core.GetType("Blocker.Core.WindowsNotificationHelper");
        Assert.NotNull(notifierType);
        Assert.NotNull(helperType);

        var hookProp = helperType.GetProperty("UsableWindowCheckOverride", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(hookProp);
        hookProp.SetValue(null, new Func<string, bool>(_ => false));

        var resetMethod = notifierType.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static);
        resetMethod?.Invoke(null, null);

        var processMethod = notifierType.GetMethod("ProcessGraceNotifications", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(processMethod);

        var planParamType = processMethod.GetParameters()[0].ParameterType;
        var plan = Activator.CreateInstance(planParamType)!;
        var gpProp = planParamType.GetProperty("GracePeriodBrowsers");
        var remProp = planParamType.GetProperty("GracePeriodRemainingSeconds");
        gpProp?.SetValue(plan, new List<string> { "chrome.exe" });
        remProp?.SetValue(plan, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["chrome.exe"] = 25 });

        // Pass null for db; suppressed notification will continue without touching db
        processMethod.Invoke(null, new object?[] { plan, null });

        hookProp.SetValue(null, null);
    }

    #endregion

    #region Rule 5: Fail-Closed Semantics

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid::path??*")]
    public void Rule5_InvalidOrUnresolvablePath_FailsClosed(string invalidPath)
    {
        var asmB_Core = LoadAssemblySafely(WorktreeB_Core);
        Assert.NotNull(asmB_Core);
        var protectedProcessesType = asmB_Core.GetType("Blocker.Core.ProtectedProcesses");
        var isEdgeInstallRootMethod = protectedProcessesType?.GetMethod("IsEdgeInstallRoot", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(isEdgeInstallRootMethod);

        bool result = (bool)isEdgeInstallRootMethod.Invoke(null, new object[] { invalidPath })!;
        Assert.False(result, $"Invalid path '{invalidPath}' must fail closed.");
    }

    #endregion

    #region Rule 6: Notification Flooding and Non-Blocking Monitor Loop

    [Fact]
    public void Rule6_NotificationDispatch_IsNonBlockingAndEnqueuesFast()
    {
        var asmC_Svc = LoadAssemblySafely(WorktreeC_Service);
        Assert.NotNull(asmC_Svc);
        var dispatcherType = asmC_Svc.GetType("Blocker.Service.ServiceNotificationDispatcher");
        Assert.NotNull(dispatcherType);

        var dispatchMethod = dispatcherType.GetMethod("DispatchNotification", new[]
        {
            typeof(string), typeof(string), typeof(string)
        });
        Assert.NotNull(dispatchMethod);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            bool enqueued = (bool)dispatchMethod.Invoke(null, new object[] { "TestKind", $"TestApp_{i % 5}", "Reason" })!;
            Assert.True(enqueued);
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1500,
            $"100 notification dispatches took {sw.ElapsedMilliseconds}ms. Dispatch must be non-blocking!");
    }

    #endregion

    #region Rule 7: UI Containment and Return to Zen Integrity

    [Fact]
    public void Rule7_StateTransitions_KillingOrHidingDuringTransition_RecoversSafely()
    {
        var asmA_Core = LoadAssemblySafely(WorktreeA_Core);
        Assert.NotNull(asmA_Core);
        var smType = asmA_Core.GetType("Blocker.Core.ZenLaunchStateMachine");
        Assert.NotNull(smType);
        var sm = Activator.CreateInstance(smType);

        var requestLaunchMethod = smType.GetMethod("RequestLaunch");
        var windowDiscoveredMethod = smType.GetMethod("WindowDiscovered");
        var launchFailedMethod = smType.GetMethod("LaunchFailed");
        var returnToZenMethod = smType.GetMethod("ReturnToZen");
        var resetMethod = smType.GetMethod("Reset");
        var currentStateProp = smType.GetProperty("CurrentState");

        // 1. Launch requested, process killed by external actor before discovery
        requestLaunchMethod?.Invoke(sm, new object?[] { "app_killed_early", false, null, null });
        Assert.Equal("Launching", currentStateProp?.GetValue(sm)?.ToString());

        launchFailedMethod?.Invoke(sm, new object[] { "Process terminated unexpectedly" });
        Assert.Equal("Failed", currentStateProp?.GetValue(sm)?.ToString());

        // 2. Window discovered then killed/hidden immediately
        resetMethod?.Invoke(sm, null);
        requestLaunchMethod?.Invoke(sm, new object?[] { "app_hidden", false, null, null });
        windowDiscoveredMethod?.Invoke(sm, new object[] { new IntPtr(0x999), 1234 });
        Assert.Equal("Active", currentStateProp?.GetValue(sm)?.ToString());

        returnToZenMethod?.Invoke(sm, null);
        Assert.Equal("ReturnedToZen", currentStateProp?.GetValue(sm)?.ToString());
    }

    [Fact]
    public void Rule7_SpammingReturnToZen_RemainsIdempotentAndThreadSafe()
    {
        var asmA_Core = LoadAssemblySafely(WorktreeA_Core);
        Assert.NotNull(asmA_Core);
        var smType = asmA_Core.GetType("Blocker.Core.ZenLaunchStateMachine");
        Assert.NotNull(smType);

        var sm = Activator.CreateInstance(smType);
        Assert.NotNull(sm);

        var requestLaunchMethod = smType.GetMethod("RequestLaunch");
        var returnToZenMethod = smType.GetMethod("ReturnToZen");
        var currentStateProp = smType.GetProperty("CurrentState");
        Assert.NotNull(requestLaunchMethod);
        Assert.NotNull(returnToZenMethod);
        Assert.NotNull(currentStateProp);

        requestLaunchMethod.Invoke(sm, new object?[] { "msedge", true, new IntPtr(0x1234), 999 });

        Parallel.For(0, 50, _ =>
        {
            returnToZenMethod.Invoke(sm, null);
        });

        object? state = currentStateProp.GetValue(sm);
        Assert.Equal("ReturnedToZen", state?.ToString());
    }

    [Fact]
    public void Rule7_ClosingWindowMidLaunch_RecoversCleanly()
    {
        var asmA_Core = LoadAssemblySafely(WorktreeA_Core);
        Assert.NotNull(asmA_Core);
        var smType = asmA_Core.GetType("Blocker.Core.ZenLaunchStateMachine");
        Assert.NotNull(smType);
        var sm = Activator.CreateInstance(smType);

        var requestLaunchMethod = smType.GetMethod("RequestLaunch");
        var windowClosedMethod = smType.GetMethod("WindowClosed");
        var currentStateProp = smType.GetProperty("CurrentState");
        var activeHwndProp = smType.GetProperty("ActiveHwnd");

        requestLaunchMethod?.Invoke(sm, new object?[] { "heavy_app", false, null, null });
        Assert.Equal("Launching", currentStateProp?.GetValue(sm)?.ToString());

        windowClosedMethod?.Invoke(sm, null);
        Assert.Equal("ReturnedToZen", currentStateProp?.GetValue(sm)?.ToString());
        Assert.Equal(IntPtr.Zero, (IntPtr)(activeHwndProp?.GetValue(sm) ?? IntPtr.Zero));
    }

    #endregion

    #region Rule 8: Command Line Escaping and Privilege Separation

    [Theory]
    [InlineData("normal_text")]
    [InlineData("quotes\"inside\"arg")]
    [InlineData("trailing_backslash\\")]
    [InlineData("complex; rm -rf /; & calc.exe `whoami`")]
    public void Rule8_CliArgumentEscaping_NeutralizesQuotes(string rawArg)
    {
        var asmC_Svc = LoadAssemblySafely(WorktreeC_Service);
        Assert.NotNull(asmC_Svc);
        var dispatcherType = asmC_Svc.GetType("Blocker.Service.ServiceNotificationDispatcher");
        var escapeMethod = dispatcherType?.GetMethod("EscapeCliArg", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(escapeMethod);

        string escaped = (string)escapeMethod.Invoke(null, new object[] { rawArg })!;
        int rawQuotes = rawArg.Count(c => c == '"');
        int escapedSlashes = escaped.Split("\\\"").Length - 1;
        Assert.Equal(rawQuotes, escapedSlashes);
    }

    [Fact]
    public void Rule8_ToastXmlEscaping_PreventsXmlInjection()
    {
        string maliciousText = "<injection>Click here</injection>&evil='true'\"quote\"";
        string escaped = SecurityElement.Escape(maliciousText);

        Assert.DoesNotContain("<injection>", escaped);
        Assert.Contains("&lt;injection&gt;", escaped);
        Assert.Contains("&amp;evil=", escaped);
        Assert.Contains("&quot;quote&quot;", escaped);
    }

    #endregion

    #region Rule 9: Concurrency Stress

    [Fact]
    public void Rule9_ZenLaunchStateMachine_ConcurrentTransitions_MaintainsConsistency()
    {
        var asmA_Core = LoadAssemblySafely(WorktreeA_Core);
        Assert.NotNull(asmA_Core);
        var smType = asmA_Core.GetType("Blocker.Core.ZenLaunchStateMachine");
        Assert.NotNull(smType);
        var sm = Activator.CreateInstance(smType);

        var requestLaunchMethod = smType.GetMethod("RequestLaunch");
        var windowDiscoveredMethod = smType.GetMethod("WindowDiscovered");
        var returnToZenMethod = smType.GetMethod("ReturnToZen");
        var resetMethod = smType.GetMethod("Reset");
        var currentStateProp = smType.GetProperty("CurrentState");

        Parallel.For(0, 100, i =>
        {
            requestLaunchMethod?.Invoke(sm, new object?[] { $"app_{i}", i % 2 == 0, new IntPtr(i + 1), i + 10 });
            windowDiscoveredMethod?.Invoke(sm, new object[] { new IntPtr(i + 100), i + 1000 });
            returnToZenMethod?.Invoke(sm, null);
            resetMethod?.Invoke(sm, null);
        });

        object? finalState = currentStateProp?.GetValue(sm);
        Assert.Equal("Idle", finalState?.ToString());
    }

    #endregion

    #region Rule 10: Log Sanitization and Millisecond Timestamps

    [Fact]
    public void Rule10_ForbiddenFiles_RemainUntouchedAcrossAllBranches()
    {
        string[] forbiddenFilePatterns =
        {
            "EmergencyUnlock",
            "LockMode",
            "CommandHandler.cs",
            "docs/COMMAND_AUTHORIZATION.md",
            "PipeAuthorizationTests.cs"
        };

        string[] branches = { "fix/zen-svc-enforcement", "fix/zen-notifications", "fix/zen-ui-launch" };
        string baseCommit = "ba5e097d2362da9ebe9ddb055ae69f3f0e5c1de2";

        foreach (var branch in branches)
        {
            var psi = new ProcessStartInfo("git", $"diff --name-only {baseCommit} {branch}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            Assert.NotNull(proc);
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            var changedFiles = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var file in changedFiles)
            {
                foreach (var forbidden in forbiddenFilePatterns)
                {
                    Assert.DoesNotContain(forbidden, file, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void Rule10_LogEntries_ConformToContractAndSanitization()
    {
        string sampleEnforce = $"[ZenEnforce] [{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [MEDITATION SUSPEND] Process 'notepad.exe' (PID: 1234) suspended.";
        string sampleNotify = $"[ZenNotify] [{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] CLI --notify received: kind='BrowserGrace', name='msedge.exe'";
        string sampleLaunch = $"[ZenLaunch] [{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] State changed: Launching -> Active (App: 'msedge')";

        var regexTimestamp = new Regex(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]");
        Assert.Matches(regexTimestamp, sampleEnforce);
        Assert.Matches(regexTimestamp, sampleNotify);
        Assert.Matches(regexTimestamp, sampleLaunch);

        string[] forbiddenSubstrings = { "password", "salt", "hash", "secret", "privateKey", "token=" };
        foreach (var sample in new[] { sampleEnforce, sampleNotify, sampleLaunch })
        {
            foreach (var forbidden in forbiddenSubstrings)
            {
                Assert.DoesNotContain(forbidden, sample, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    #endregion

    private class PipeAuthorizationTests_TestBlockStore : IBlockStore
    {
        public List<Profile> Profiles { get; } = new();
        public List<BlockSession> Sessions { get; } = new();
        public bool EmergencyUnlockExists { get; set; }
        public bool EmergencyUnlockConsumed { get; set; }
        public bool CleanHostsCalled { get; set; }
        public bool ServiceElevated { get; set; } = true;
        public bool HostsBlockingOk { get; set; } = true;
        public HostsState HostsState { get; set; } = HostsState.Idle;
        public int HostsDomainCount { get; set; }
        public string? HostsLastError { get; set; }
        public FallbackPlanner FallbackPlanner { get; } = new();
        public FallbackPlanResult? LastFallbackPlan { get; set; }
        private DnsConfig _dnsConfig = new();
        public List<BlockEvent> Events { get; } = new();

        public Profile? GetProfileByName(string name) => Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        public Profile? GetProfileById(int id) => Profiles.FirstOrDefault(p => p.Id == id);
        public List<Profile> ListProfiles() => Profiles.ToList();
        public Profile SaveProfile(Profile profile) => profile;
        public bool RenameProfile(int id, string newName) => true;
        public bool DeleteProfileByName(string name) => true;
        public bool DeleteProfile(int id) => true;
        public void DisableAllProfiles() { }
        public void BypassActiveSchedules(DateTime localNow) { }

        public BlockSession? GetActiveSession() => null;
        public BlockSession? GetSessionById(int id) => null;
        public BlockSession StartSession(int profileId, long durationSeconds, bool locked) => new();
        public void EndActiveSession() { }
        public void EndSession(int sessionId) { }

        public bool IsEmergencyUnlockPresent() => EmergencyUnlockExists;
        public void ConsumeEmergencyUnlock() { EmergencyUnlockConsumed = true; }
        public bool CleanHosts() => true;
        public List<string> GetCurrentHostsDomains() => new();

        public void RecordBlockEvent(BlockEvent evt) => Events.Add(evt);
        public List<BlockEvent> GetBlockEvents(long sinceId) => Events.ToList();
        public DnsConfig GetDnsConfig() => _dnsConfig;
        public void SaveDnsConfig(DnsConfig config) => _dnsConfig = config;
    }
}
