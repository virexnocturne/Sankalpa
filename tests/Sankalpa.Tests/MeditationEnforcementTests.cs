using System;
using System.Collections.Generic;
using System.IO;
using Blocker.Core;
using Blocker.Service;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Sankalpa.Tests;

public class MeditationEnforcementTests
{
    private static readonly string PfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static readonly string Pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string LocalApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    [Fact]
    public void RenamedMsedge_InUserWritableFolder_IsSuspendedAndRejectedByPredicate()
    {
        var allowedApps = new List<string> { "msedge.exe" };

        // 1. Fake msedge in Temp folder
        string tempMsedge = @"C:\Temp\msedge.exe";
        bool allowedTemp = Worker.IsProcessAllowedPredicate("msedge", "msedge.exe", tempMsedge, allowedApps, null);
        Assert.False(allowedTemp, "msedge in C:\\Temp must be rejected and suspended");

        // 2. Fake msedge in user Downloads folder
        string downloadsMsedge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "msedge.exe");
        bool allowedDownloads = Worker.IsProcessAllowedPredicate("msedge", "msedge.exe", downloadsMsedge, allowedApps, null);
        Assert.False(allowedDownloads, "msedge in Downloads must be rejected");

        // 3. Path traversal attack attempting to escape from Edge directory
        string traversalMsedge = Path.Combine(PfX86, "Microsoft", "Edge", "..", "..", "Temp", "msedge.exe");
        bool allowedTraversal = Worker.IsProcessAllowedPredicate("msedge", "msedge.exe", traversalMsedge, allowedApps, null);
        Assert.False(allowedTraversal, "Path traversal escaping Edge root must be rejected");

        // 4. Authentic msedge in Program Files (x86)
        string authenticX86 = Path.Combine(PfX86, "Microsoft", "Edge", "Application", "msedge.exe");
        bool allowedX86 = Worker.IsProcessAllowedPredicate("msedge", "msedge.exe", authenticX86, allowedApps, null);
        Assert.True(allowedX86, "Authentic msedge in Program Files (x86) must be allowed");

        // 5. Authentic msedge in Program Files (64-bit)
        string authentic64 = Path.Combine(Pf, "Microsoft", "Edge", "Application", "msedge.exe");
        bool allowed64 = Worker.IsProcessAllowedPredicate("msedge", "msedge.exe", authentic64, allowedApps, null);
        Assert.True(allowed64, "Authentic msedge in Program Files must be allowed");

        // 6. Authentic msedge in LocalAppData
        string authenticLocal = Path.Combine(LocalApp, "Microsoft", "Edge", "Application", "msedge.exe");
        bool allowedLocal = Worker.IsProcessAllowedPredicate("msedge", "msedge.exe", authenticLocal, allowedApps, null);
        Assert.True(allowedLocal, "Authentic msedge in LocalAppData must be allowed");
    }

    [Fact]
    public void LegitimateEdgeHelpers_InsideInstallRoots_WithEdgeParent_AreExempt()
    {
        var allowedApps = new List<string> { "msedge" };

        string edgeAppPath = Path.Combine(PfX86, "Microsoft", "Edge", "Application", "msedge.exe");
        string identityHelperPath = Path.Combine(PfX86, "Microsoft", "Edge", "Application", "124.0.2478.80", "identity_helper.exe");
        string notifHelperPath = Path.Combine(PfX86, "Microsoft", "Edge", "Application", "124.0.2478.80", "notification_helper.exe");
        string edgeUpdatePath = Path.Combine(PfX86, "Microsoft", "EdgeUpdate", "MicrosoftEdgeUpdate.exe");
        string webViewPath = Path.Combine(PfX86, "Microsoft", "EdgeWebView", "Application", "124.0.2478.80", "msedgewebview2.exe");
        string edgeCoreProxyPath = Path.Combine(PfX86, "Microsoft", "EdgeCore", "124.0.2478.80", "msedge_proxy.exe");

        var processTable = new Dictionary<int, FastProcessEntry>
        {
            [100] = new FastProcessEntry(100, "msedge", "msedge.exe", 0),
            [101] = new FastProcessEntry(101, "identity_helper", "identity_helper.exe", 100),
            [102] = new FastProcessEntry(102, "notification_helper", "notification_helper.exe", 101), // Grandchild
            [103] = new FastProcessEntry(103, "MicrosoftEdgeUpdate", "MicrosoftEdgeUpdate.exe", 100),
            [104] = new FastProcessEntry(104, "msedgewebview2", "msedgewebview2.exe", 100),
            [105] = new FastProcessEntry(105, "msedge_proxy", "msedge_proxy.exe", 100)
        };

        var pathMap = new Dictionary<int, string>
        {
            [100] = edgeAppPath,
            [101] = identityHelperPath,
            [102] = notifHelperPath,
            [103] = edgeUpdatePath,
            [104] = webViewPath,
            [105] = edgeCoreProxyPath
        };

        string? GetPath(int pid) => pathMap.TryGetValue(pid, out var p) ? p : null;

        // Direct child identity_helper
        bool allowedIdentity = Worker.IsProcessAllowedPredicate(
            "identity_helper", "identity_helper.exe", identityHelperPath, allowedApps, null, 100, processTable, GetPath);
        Assert.True(allowedIdentity, "Direct child identity_helper inside Edge tree must be exempt");

        // Grandchild notification_helper (spawned by identity_helper)
        bool allowedNotif = Worker.IsProcessAllowedPredicate(
            "notification_helper", "notification_helper.exe", notifHelperPath, allowedApps, null, 101, processTable, GetPath);
        Assert.True(allowedNotif, "Multi-hop grandchild helper must be exempt through ancestor chain");

        // Sibling EdgeUpdate helper
        bool allowedUpdate = Worker.IsProcessAllowedPredicate(
            "MicrosoftEdgeUpdate", "MicrosoftEdgeUpdate.exe", edgeUpdatePath, allowedApps, null, 100, processTable, GetPath);
        Assert.True(allowedUpdate, "EdgeUpdate in canonical directory must be exempt");

        // WebView2 helper
        bool allowedWebView = Worker.IsProcessAllowedPredicate(
            "msedgewebview2", "msedgewebview2.exe", webViewPath, allowedApps, null, 100, processTable, GetPath);
        Assert.True(allowedWebView, "EdgeWebView in canonical directory must be exempt");

        // EdgeCore proxy
        bool allowedProxy = Worker.IsProcessAllowedPredicate(
            "msedge_proxy", "msedge_proxy.exe", edgeCoreProxyPath, allowedApps, null, 100, processTable, GetPath);
        Assert.True(allowedProxy, "EdgeCore proxy in canonical directory must be exempt");
    }

    [Fact]
    public void EdgeHelper_WithExitedParent_InsideInstallRoot_IsExempt()
    {
        var allowedApps = new List<string> { "msedge.exe" };

        string proxyPath = Path.Combine(PfX86, "Microsoft", "Edge", "Application", "msedge_proxy.exe");
        string updatePath = Path.Combine(PfX86, "Microsoft", "EdgeUpdate", "MicrosoftEdgeUpdate.exe");
        string fakeHelperPath = @"C:\Temp\identity_helper.exe";

        // Process table does NOT contain parent 9999 (parent already exited)
        var processTable = new Dictionary<int, FastProcessEntry>();

        // 1. msedge_proxy with exited parent inside canonical root
        bool allowedProxy = Worker.IsProcessAllowedPredicate(
            "msedge_proxy", "msedge_proxy.exe", proxyPath, allowedApps, null, 9999, processTable, null);
        Assert.True(allowedProxy, "Authentic Edge proxy with exited parent must be exempt");

        // 2. MicrosoftEdgeUpdate with exited parent inside canonical root
        bool allowedUpdate = Worker.IsProcessAllowedPredicate(
            "MicrosoftEdgeUpdate", "MicrosoftEdgeUpdate.exe", updatePath, allowedApps, null, 9999, processTable, null);
        Assert.True(allowedUpdate, "Authentic EdgeUpdate with exited parent must be exempt");

        // 3. Fake helper in C:\Temp with exited parent -> MUST BE REJECTED
        bool allowedFake = Worker.IsProcessAllowedPredicate(
            "identity_helper", "identity_helper.exe", fakeHelperPath, allowedApps, null, 9999, processTable, null);
        Assert.False(allowedFake, "Fake helper in C:\\Temp must be rejected and suspended");
    }

    [Fact]
    public void NonEdge_UnauthorizedProcesses_AreSuspendedOnSameSchedule()
    {
        var allowedApps = new List<string> { "notepad.exe", "msedge.exe" };

        // 1. Calculator
        bool allowedCalc = Worker.IsProcessAllowedPredicate("calc", "calc.exe", @"C:\Windows\System32\calc.exe", allowedApps, null);
        Assert.False(allowedCalc, "Calculator must not be allowed");

        // 2. Discord
        bool allowedDiscord = Worker.IsProcessAllowedPredicate("Discord", "Discord.exe", @"C:\Users\User\AppData\Local\Discord\app.exe", allowedApps, null);
        Assert.False(allowedDiscord, "Discord must not be allowed");

        // 3. Command Prompt
        bool allowedCmd = Worker.IsProcessAllowedPredicate("cmd", "cmd.exe", @"C:\Windows\System32\cmd.exe", allowedApps, null);
        Assert.False(allowedCmd, "cmd.exe must not be allowed");

        // 4. Chrome (not in allowed list)
        bool allowedChrome = Worker.IsProcessAllowedPredicate("chrome", "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", allowedApps, null);
        Assert.False(allowedChrome, "chrome.exe must not be allowed");

        // 5. Edge helper when Edge is NOT allowed
        var nonEdgeAllowed = new List<string> { "notepad.exe" };
        string identityPath = Path.Combine(PfX86, "Microsoft", "Edge", "Application", "124.0.2478.80", "identity_helper.exe");
        bool allowedEdgeHelperWithoutEdge = Worker.IsProcessAllowedPredicate(
            "identity_helper", "identity_helper.exe", identityPath, nonEdgeAllowed, null);
        Assert.False(allowedEdgeHelperWithoutEdge, "Edge helper must NOT be exempt when msedge is not an allowed app");
    }

    [Fact]
    public void ProcessIdentityMismatch_PIDReuse_FailsSafely()
    {
        var suspender = new ProcessSuspender();

        // 1. PID <= 4 rejected
        Assert.False(suspender.SuspendProcess(0, "system"));
        Assert.False(suspender.SuspendProcess(4, "system"));

        // 2. Expected image path mismatch
        int currentPid = Environment.ProcessId;
        string fakeExpectedPath = @"C:\NonExistent\FakeProcess.exe";
        bool resultPathMismatch = suspender.SuspendProcess(currentPid, "Blocker", expectedImagePath: fakeExpectedPath);
        Assert.False(resultPathMismatch, "Expected path mismatch must reject suspension immediately");

        // 3. Expected creation time mismatch
        DateTime distantTime = DateTime.UtcNow.AddHours(-10);
        bool resultTimeMismatch = suspender.SuspendProcess(currentPid, "Blocker", expectedCreationTimeUtc: distantTime);
        Assert.False(resultTimeMismatch, "Expected creation time mismatch must reject suspension immediately");
    }

    [Fact]
    public void ContractC3_PredicateCrossCheckConsistency()
    {
        var allowedApps = new List<string> { "notepad.exe", "msedge" };
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["notepad"] = @"C:\Windows\System32"
        };

        // Pinned notepad in System32
        bool allowed1 = Worker.IsAppAllowed("notepad", "notepad.exe", allowedApps, @"C:\Windows\System32\notepad.exe", roots);
        bool allowed2 = Worker.IsProcessAllowedPredicate("notepad", "notepad.exe", @"C:\Windows\System32\notepad.exe", allowedApps, roots);
        Assert.True(allowed1);
        Assert.Equal(allowed1, allowed2);

        // Imposter notepad outside System32
        bool rejected1 = Worker.IsAppAllowed("notepad", "notepad.exe", allowedApps, @"C:\Temp\notepad.exe", roots);
        bool rejected2 = Worker.IsProcessAllowedPredicate("notepad", "notepad.exe", @"C:\Temp\notepad.exe", allowedApps, roots);
        Assert.False(rejected1);
        Assert.Equal(rejected1, rejected2);

        // Snipping tool built-in
        bool snip1 = Worker.IsAppAllowed("snippingtool", "SnippingTool.exe", allowedApps);
        bool snip2 = Worker.IsProcessAllowedPredicate("snippingtool", "SnippingTool.exe", null, allowedApps, roots);
        Assert.True(snip1);
        Assert.Equal(snip1, snip2);
    }

    [Fact]
    public void FinishSession_LegitimateChild_ResumedNotTerminated()
    {
        var sessionStart = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        using var dummyHandle = new SafeProcessHandle(IntPtr.Zero, true);

        // 1. Entry marked legitimate child: ShouldTerminate MUST be false
        var legitEntry = new ProcessSuspender.Entry(
            dummyHandle, "identity_helper", sessionStart.AddMinutes(2), FromAllowedApp: true,
            ImagePath: Path.Combine(PfX86, "Microsoft", "Edge", "Application", "124.0.0.0", "identity_helper.exe"),
            IsLegitimate: true);
        Assert.False(ProcessSuspender.ShouldTerminate(legitEntry, sessionStart));

        // 2. Entry from non-allowed app: ShouldTerminate is false
        var unallowed = new ProcessSuspender.Entry(
            dummyHandle, "game", sessionStart.AddMinutes(2), FromAllowedApp: false);
        Assert.False(ProcessSuspender.ShouldTerminate(unallowed, sessionStart));

        // 3. Unauthorized child process from allowed app (spawned during session): ShouldTerminate is true (per contract)
        var rogueChild = new ProcessSuspender.Entry(
            dummyHandle, "miner", sessionStart.AddMinutes(2), FromAllowedApp: true,
            ImagePath: @"C:\Temp\miner.exe", IsLegitimate: false);
        Assert.True(ProcessSuspender.ShouldTerminate(rogueChild, sessionStart));
    }
}
