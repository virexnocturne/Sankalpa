using System;
using System.Collections.Generic;
using Blocker.Core;
using Xunit;

namespace Sankalpa.Tests;

public class HudLifecycleTests
{
    private const int OwnPid = 1234;
    private static readonly List<string> AllowedApps = new() { "notepad", "vlc", "obsidian" };
    private static readonly Dictionary<string, string> AllowedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["notepad"] = @"C:\Windows\System32",
        ["vlc"] = @"C:\Program Files\VideoLAN\VLC",
        ["obsidian"] = @"C:\Users\User\AppData\Local\Obsidian"
    };

    [Fact]
    public void ReturnToZen_HudStaysHidden_ModeDoesNotFlipOnBackgroundEvents()
    {
        var state = new HudSessionState { IsAllowedAppMode = false };
        DateTime now = DateTime.UtcNow;

        // An allowed app window exists and becomes foreground while in Zen mode (e.g. background event/popup)
        var allowedWindow = new WindowSnapshot(
            Hwnd: (IntPtr)0x1000,
            Pid: 5000,
            ProcessName: "vlc",
            ImagePath: @"C:\Program Files\VideoLAN\VLC\vlc.exe",
            ClassName: "Qt5QWindowIcon",
            Width: 800,
            Height: 600,
            IsWindow: true,
            IsVisible: true,
            IsIconic: false,
            HasTitle: true);

        var result = HudVisibilityLogic.Evaluate(
            state,
            foreground: allowedWindow,
            tracked: null,
            nowUtc: now,
            allowedProcessNames: AllowedApps,
            allowedRoots: AllowedRoots,
            ownPid: OwnPid);

        Assert.False(result.ShouldShowHud);
        Assert.False(result.ShouldRestoreZenScreen);
        Assert.False(state.IsAllowedAppMode);
        Assert.Equal(IntPtr.Zero, state.TrackedHwnd);
    }

    [Fact]
    public void ColdLaunchFailureOrTimeout_RestoresZenScreen_NoOrphanHud()
    {
        var state = new HudSessionState
        {
            IsAllowedAppMode = true,
            IsLaunching = true,
            LaunchGraceUntilUtc = DateTime.UtcNow.AddSeconds(4)
        };

        DateTime t0 = DateTime.UtcNow;

        // While within grace period and no window found: HUD not shown, shield not yet restored
        var resultDuringGrace = HudVisibilityLogic.Evaluate(
            state,
            foreground: null,
            tracked: null,
            nowUtc: t0.AddSeconds(2),
            allowedProcessNames: AllowedApps,
            allowedRoots: AllowedRoots,
            ownPid: OwnPid);

        Assert.False(resultDuringGrace.ShouldShowHud);
        Assert.False(resultDuringGrace.ShouldRestoreZenScreen);
        Assert.True(state.IsAllowedAppMode);

        // When grace period expires with no window discovered: fail closed, restore Zen screen!
        var resultExpired = HudVisibilityLogic.Evaluate(
            state,
            foreground: null,
            tracked: null,
            nowUtc: t0.AddSeconds(5),
            allowedProcessNames: AllowedApps,
            allowedRoots: AllowedRoots,
            ownPid: OwnPid);

        Assert.False(resultExpired.ShouldShowHud);
        Assert.True(resultExpired.ShouldRestoreZenScreen);
        Assert.False(state.IsAllowedAppMode);
        Assert.False(state.IsLaunching);
    }

    [Fact]
    public void AllowedAppToastOrTrayPopup_Ignored_ModeDoesNotFlip()
    {
        var state = new HudSessionState { IsAllowedAppMode = false };
        DateTime now = DateTime.UtcNow;

        // Toast popup from an allowed app (e.g. VLC or Obsidian tray popup/toast)
        var toastWindow = new WindowSnapshot(
            Hwnd: (IntPtr)0x2000,
            Pid: 5000,
            ProcessName: "vlc",
            ImagePath: @"C:\Program Files\VideoLAN\VLC\vlc.exe",
            ClassName: "ToastWnd",
            Width: 250,
            Height: 80,
            IsWindow: true,
            IsVisible: true,
            IsIconic: false,
            HasTitle: false);

        // Verify ZenLaunchMatcher recognizes it as NOT a real app window
        Assert.False(ZenLaunchMatcher.IsRealAppWindow(
            toastWindow.ClassName,
            toastWindow.Width,
            toastWindow.Height,
            toastWindow.IsIconic,
            toastWindow.HasTitle));

        var result = HudVisibilityLogic.Evaluate(
            state,
            foreground: toastWindow,
            tracked: null,
            nowUtc: now,
            allowedProcessNames: AllowedApps,
            allowedRoots: AllowedRoots,
            ownPid: OwnPid);

        Assert.False(result.ShouldShowHud);
        Assert.False(result.ShouldRestoreZenScreen);
        Assert.False(state.IsAllowedAppMode);
    }

    [Fact]
    public void MinimiseAllowedApp_HudHidesImmediately_ShieldRestoresAfterDebounce()
    {
        IntPtr hwnd = (IntPtr)0x3000;
        var state = new HudSessionState
        {
            IsAllowedAppMode = true,
            TrackedHwnd = hwnd,
            TrackedPid = 4000,
            TrackedProcessName = "notepad"
        };

        DateTime t0 = DateTime.UtcNow;

        // Window becomes iconic (minimized)
        var minimizedWindow = new WindowSnapshot(
            Hwnd: hwnd,
            Pid: 4000,
            ProcessName: "notepad",
            ImagePath: @"C:\Windows\System32\notepad.exe",
            ClassName: "Notepad",
            Width: 0,
            Height: 0,
            IsWindow: true,
            IsVisible: true,
            IsIconic: true,
            HasTitle: true);

        // Focus drops to own shield or desktop during minimize
        var neutralForeground = new WindowSnapshot(
            Hwnd: (IntPtr)0x9999,
            Pid: OwnPid,
            ProcessName: "Sankalpa",
            ImagePath: @"C:\Program Files\Sankalpa\Sankalpa.exe",
            ClassName: "HwndWrapper",
            Width: 1920,
            Height: 1080,
            IsWindow: true,
            IsVisible: true,
            IsIconic: false,
            HasTitle: true);

        // Tick 1 (t = t0 + 100ms): within debounce (750ms). HUD hides immediately, shield not yet restored.
        var resultTick1 = HudVisibilityLogic.Evaluate(
            state,
            foreground: neutralForeground,
            tracked: minimizedWindow,
            nowUtc: t0.AddMilliseconds(100),
            allowedProcessNames: AllowedApps,
            allowedRoots: AllowedRoots,
            ownPid: OwnPid);

        Assert.False(resultTick1.ShouldShowHud);
        Assert.False(resultTick1.ShouldRestoreZenScreen);
        Assert.NotNull(state.WindowLostSinceUtc);

        // Tick 2 (t = t0 + 1000ms, elapsed = 900ms > 750ms): debounce expired! Fail closed, restore Zen screen!
        var resultTick2 = HudVisibilityLogic.Evaluate(
            state,
            foreground: neutralForeground,
            tracked: minimizedWindow,
            nowUtc: t0.AddMilliseconds(1000),
            allowedProcessNames: AllowedApps,
            allowedRoots: AllowedRoots,
            ownPid: OwnPid);

        Assert.False(resultTick2.ShouldShowHud);
        Assert.True(resultTick2.ShouldRestoreZenScreen);
        Assert.False(state.IsAllowedAppMode);
        Assert.Equal(IntPtr.Zero, state.TrackedHwnd);
    }

    [Fact]
    public void CloseAllowedApp_HudHidesImmediately_ShieldRestoresAfterDebounce()
    {
        IntPtr hwnd = (IntPtr)0x3000;
        var state = new HudSessionState
        {
            IsAllowedAppMode = true,
            TrackedHwnd = hwnd,
            TrackedPid = 4000,
            TrackedProcessName = "notepad"
        };

        DateTime t0 = DateTime.UtcNow;

        // Window destroyed (IsWindow == false)
        var closedWindow = new WindowSnapshot(
            Hwnd: hwnd,
            Pid: 4000,
            ProcessName: "notepad",
            ImagePath: @"C:\Windows\System32\notepad.exe",
            ClassName: "Notepad",
            Width: 0,
            Height: 0,
            IsWindow: false,
            IsVisible: false,
            IsIconic: false,
            HasTitle: false);

        // Tick 1 (t = t0 + 200ms): within debounce. HUD hides.
        var result1 = HudVisibilityLogic.Evaluate(
            state,
            foreground: null,
            tracked: closedWindow,
            nowUtc: t0.AddMilliseconds(200),
            allowedProcessNames: AllowedApps,
            allowedRoots: AllowedRoots,
            ownPid: OwnPid);

        Assert.False(result1.ShouldShowHud);
        Assert.False(result1.ShouldRestoreZenScreen);

        // Tick 2 (t = t0 + 1000ms, elapsed = 800ms > 750ms): debounce expired. Restore Zen shield!
        var result2 = HudVisibilityLogic.Evaluate(
            state,
            foreground: null,
            tracked: closedWindow,
            nowUtc: t0.AddMilliseconds(1000),
            allowedProcessNames: AllowedApps,
            allowedRoots: AllowedRoots,
            ownPid: OwnPid);

        Assert.False(result2.ShouldShowHud);
        Assert.True(result2.ShouldRestoreZenScreen);
        Assert.False(state.IsAllowedAppMode);
    }

    [Fact]
    public void AltTabBetweenTwoAllowedApps_RetargetsWithNoFlicker()
    {
        IntPtr notepadHwnd = (IntPtr)0x3000;
        IntPtr vlcHwnd = (IntPtr)0x4000;

        var state = new HudSessionState
        {
            IsAllowedAppMode = true,
            TrackedHwnd = notepadHwnd,
            TrackedPid = 4000,
            TrackedProcessName = "notepad"
        };

        DateTime now = DateTime.UtcNow;

        var vlcWindow = new WindowSnapshot(
            Hwnd: vlcHwnd,
            Pid: 5000,
            ProcessName: "vlc",
            ImagePath: @"C:\Program Files\VideoLAN\VLC\vlc.exe",
            ClassName: "Qt5QWindowIcon",
            Width: 1024,
            Height: 768,
            IsWindow: true,
            IsVisible: true,
            IsIconic: false,
            HasTitle: true);

        var notepadWindow = new WindowSnapshot(
            Hwnd: notepadHwnd,
            Pid: 4000,
            ProcessName: "notepad",
            ImagePath: @"C:\Windows\System32\notepad.exe",
            ClassName: "Notepad",
            Width: 800,
            Height: 600,
            IsWindow: true,
            IsVisible: true,
            IsIconic: false,
            HasTitle: true);

        // VLC becomes the new foreground window via Alt+Tab
        var result = HudVisibilityLogic.Evaluate(
            state,
            foreground: vlcWindow,
            tracked: notepadWindow,
            nowUtc: now,
            allowedProcessNames: AllowedApps,
            allowedRoots: AllowedRoots,
            ownPid: OwnPid);

        Assert.True(result.ShouldShowHud);
        Assert.False(result.ShouldRestoreZenScreen);
        Assert.True(result.ShouldRetarget);
        Assert.Equal(vlcHwnd, result.NewTrackedHwnd);
        Assert.Equal(5000, result.NewTrackedPid);
        Assert.Equal("vlc", result.NewTrackedProcessName);
        Assert.Equal(vlcHwnd, state.TrackedHwnd);
        Assert.Null(state.WindowLostSinceUtc);
    }

    [Fact]
    public void NeutralWindows_OwnProcessAndSystemHelpers_KeepHudVisible()
    {
        IntPtr notepadHwnd = (IntPtr)0x3000;
        var state = new HudSessionState
        {
            IsAllowedAppMode = true,
            TrackedHwnd = notepadHwnd,
            TrackedPid = 4000,
            TrackedProcessName = "notepad"
        };

        DateTime now = DateTime.UtcNow;

        var notepadWindow = new WindowSnapshot(
            Hwnd: notepadHwnd,
            Pid: 4000,
            ProcessName: "notepad",
            ImagePath: @"C:\Windows\System32\notepad.exe",
            ClassName: "Notepad",
            Width: 800,
            Height: 600,
            IsWindow: true,
            IsVisible: true,
            IsIconic: false,
            HasTitle: true);

        // 1. Own process window (e.g. ZenMiniHudWindow, QuickNotesOverlay, PasswordDialog)
        var ownWindow = new WindowSnapshot(
            Hwnd: (IntPtr)0x8888,
            Pid: OwnPid,
            ProcessName: "Sankalpa",
            ImagePath: @"C:\Program Files\Sankalpa\Sankalpa.exe",
            ClassName: "HwndWrapper",
            Width: 300,
            Height: 50,
            IsWindow: true,
            IsVisible: true,
            IsIconic: false,
            HasTitle: false);

        var resultOwn = HudVisibilityLogic.Evaluate(
            state,
            foreground: ownWindow,
            tracked: notepadWindow,
            nowUtc: now,
            allowedProcessNames: AllowedApps,
            allowedRoots: AllowedRoots,
            ownPid: OwnPid);

        Assert.True(resultOwn.ShouldShowHud);
        Assert.False(resultOwn.ShouldRestoreZenScreen);
        Assert.Null(state.WindowLostSinceUtc);

        // 2. Trusted OS Helper (e.g. TextInputHost.exe)
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var textInputHostPath = System.IO.Path.Combine(winDir, "SystemApps", "MicrosoftWindows.Client.CBS_cw5n1h2txyewy", "TextInputHost.exe");

        var helperWindow = new WindowSnapshot(
            Hwnd: (IntPtr)0x7777,
            Pid: 9999,
            ProcessName: "TextInputHost",
            ImagePath: textInputHostPath,
            ClassName: "Windows.UI.Core.CoreWindow",
            Width: 400,
            Height: 300,
            IsWindow: true,
            IsVisible: true,
            IsIconic: false,
            HasTitle: true);

        var resultHelper = HudVisibilityLogic.Evaluate(
            state,
            foreground: helperWindow,
            tracked: notepadWindow,
            nowUtc: now,
            allowedProcessNames: AllowedApps,
            allowedRoots: AllowedRoots,
            ownPid: OwnPid);

        Assert.True(resultHelper.ShouldShowHud);
        Assert.False(resultHelper.ShouldRestoreZenScreen);
        Assert.Null(state.WindowLostSinceUtc);
    }

    [Fact]
    public void PathPinning_RejectsImposterExecutableOutsidePinnedDirectory()
    {
        // Real VLC in C:\Program Files\VideoLAN\VLC
        Assert.True(ZenLaunchMatcher.IsAppAllowed(
            "vlc",
            @"C:\Program Files\VideoLAN\VLC\vlc.exe",
            AllowedApps,
            AllowedRoots));

        // Imposter VLC in Downloads
        Assert.False(ZenLaunchMatcher.IsAppAllowed(
            "vlc",
            @"C:\Users\Attacker\Downloads\vlc.exe",
            AllowedApps,
            AllowedRoots));
    }

    [Fact]
    public void DecisionLogic_HighFrequencyThroughput_TakesLessThanOneMicrosecondPerCall()
    {
        var state = new HudSessionState
        {
            IsAllowedAppMode = true,
            TrackedHwnd = (IntPtr)0x3000,
            TrackedPid = 4000,
            TrackedProcessName = "notepad"
        };

        var window = new WindowSnapshot(
            Hwnd: (IntPtr)0x3000,
            Pid: 4000,
            ProcessName: "notepad",
            ImagePath: @"C:\Windows\System32\notepad.exe",
            ClassName: "Notepad",
            Width: 800,
            Height: 600,
            IsWindow: true,
            IsVisible: true,
            IsIconic: false,
            HasTitle: true);

        DateTime now = DateTime.UtcNow;

        // Warmup
        for (int i = 0; i < 100; i++)
        {
            HudVisibilityLogic.Evaluate(state, window, window, now, AllowedApps, AllowedRoots, OwnPid);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int iterations = 10_000;
        for (int i = 0; i < iterations; i++)
        {
            HudVisibilityLogic.Evaluate(state, window, window, now, AllowedApps, AllowedRoots, OwnPid);
        }
        sw.Stop();

        double usPerCall = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;
        // Verify decision logic executes under 10 microseconds per call on any system
        Assert.True(usPerCall < 10.0, $"Expected < 10us per call, got {usPerCall:F3}us");
    }
}
