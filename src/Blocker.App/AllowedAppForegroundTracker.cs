using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Blocker.Core;

namespace Blocker.App;

/// <summary>
/// Event-driven foreground and window lifecycle tracker for allowed applications in Zen mode.
/// Uses SetWinEventHook (WINEVENT_OUTOFCONTEXT) to monitor foreground switches, window minimization,
/// restoration, hiding, and destruction with zero polling overhead.
/// </summary>
public sealed class AllowedAppForegroundTracker : IDisposable
{
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const int OBJID_WINDOW = 0;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    private readonly WinEventDelegate _winEventProc;
    private readonly List<IntPtr> _hookHandles = new(5);

    private readonly Dispatcher _dispatcher;
    private readonly TrailingEdgeCoalescer _coalescer;
    private readonly HudSessionState _state = new();
    private readonly DispatcherTimer _debounceTimer;

    private readonly Func<IReadOnlyCollection<string>> _getAllowedApps;
    private readonly Func<IReadOnlyDictionary<string, string>?> _getAllowedRoots;
    private readonly int _ownPid;

    /// <summary>
    /// When true, skips tracker WinEvent scheduling and evaluations while a modal unlock dialog is open.
    /// Watchdog security enforcement continues to run normally.
    /// </summary>
    public bool IsModalUnlockDialogOpen { get; set; }

    /// <summary>
    /// Raised whenever the active, foreground allowed application window changes or goes away.
    /// Passes (0, IntPtr.Zero) when no allowed application window is in the foreground.
    /// </summary>
    public event Action<int, IntPtr>? ForegroundAllowedAppChanged;

    /// <summary>
    /// Raised when HUD visibility should change (true to show, false to hide).
    /// </summary>
    public event Action<bool>? HudVisibilityChanged;

    /// <summary>
    /// Raised when fail-closed security dictates that the Zen shield kiosk must be restored.
    /// </summary>
    public event Action? ZenScreenRestoreRequested;

    public HudSessionState State => _state;

    public AllowedAppForegroundTracker(
        Func<IReadOnlyCollection<string>> getAllowedApps,
        Func<IReadOnlyDictionary<string, string>?> getAllowedRoots)
    {
        _getAllowedApps = getAllowedApps ?? throw new ArgumentNullException(nameof(getAllowedApps));
        _getAllowedRoots = getAllowedRoots ?? throw new ArgumentNullException(nameof(getAllowedRoots));
        _ownPid = Process.GetCurrentProcess().Id;

        // Keep delegate rooted in a field so it is never garbage-collected
        _winEventProc = new WinEventDelegate(OnWinEvent);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _coalescer = new TrailingEdgeCoalescer(
            EvaluateCore,
            action => _dispatcher.BeginInvoke(DispatcherPriority.Normal, action));

        _debounceTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(ZenLaunchConstants.DefaultDebounceMs)
        };
        _debounceTimer.Tick += (s, e) =>
        {
            _debounceTimer.Stop();
            EvaluateNow();
        };

        InstallHooks();
    }

    private void InstallHooks()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            foreach (uint eventType in AllowedWinEventFilter.HookedEventTypes)
            {
                IntPtr hook = SetWinEventHook(
                    eventType,
                    eventType,
                    IntPtr.Zero,
                    _winEventProc,
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT);
                if (hook != IntPtr.Zero)
                {
                    _hookHandles.Add(hook);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AllowedAppForegroundTracker] Failed to install WinEventHook: {ex.Message}");
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        // Check event type first and ignore everything outside the 5 intended event IDs.
        if (!AllowedWinEventFilter.ShouldProcess(eventType, idObject, hwnd, _state.TrackedHwnd))
        {
            return;
        }

        if (!_state.IsAllowedAppMode || IsModalUnlockDialogOpen)
        {
            return;
        }

        _coalescer.Request();
    }

    /// <summary>
    /// Begins tracking a newly discovered allowed application window.
    /// </summary>
    public void SetTrackedWindow(IntPtr hwnd, int pid, string processName)
    {
        _coalescer.CancelPending();
        _state.IsAllowedAppMode = true;
        _state.IsLaunching = false;
        _state.TrackedHwnd = hwnd;
        _state.TrackedPid = pid;
        _state.TrackedProcessName = processName;
        _state.WindowLostSinceUtc = null;
        _debounceTimer.Stop();

        ForegroundAllowedAppChanged?.Invoke(pid, hwnd);
        HudVisibilityChanged?.Invoke(true);
    }

    /// <summary>
    /// Signals that an allowed app cold launch is starting and sets grace period.
    /// </summary>
    public void StartColdLaunch(string processName, int graceMs = ZenLaunchConstants.LaunchGracePeriodMs)
    {
        _coalescer.CancelPending();
        _state.IsAllowedAppMode = true;
        _state.IsLaunching = true;
        _state.LaunchGraceUntilUtc = DateTime.UtcNow.AddMilliseconds(graceMs);
        _state.TrackedHwnd = IntPtr.Zero;
        _state.TrackedPid = 0;
        _state.TrackedProcessName = processName;
        _state.WindowLostSinceUtc = null;
        _debounceTimer.Stop();

        // During cold launch before window exists, HUD remains hidden
        HudVisibilityChanged?.Invoke(false);
    }

    /// <summary>
    /// Resets the tracker back to Zen mode.
    /// </summary>
    public void ResetToZen()
    {
        _coalescer.CancelPending();
        _state.ResetToZen();
        _debounceTimer.Stop();

        ForegroundAllowedAppChanged?.Invoke(0, IntPtr.Zero);
        HudVisibilityChanged?.Invoke(false);
    }

    /// <summary>
    /// Evaluates current foreground and tracked window state against HUD visibility rules.
    /// </summary>
    public void EvaluateNow()
    {
        if (IsModalUnlockDialogOpen)
        {
            return;
        }

        _coalescer.RunDirect();
    }

    private void EvaluateCore()
    {
        if (IsModalUnlockDialogOpen)
        {
            return;
        }

        IntPtr fgHwnd = GetForegroundWindow();
        WindowSnapshot? fgSnapshot = fgHwnd != IntPtr.Zero ? CreateSnapshot(fgHwnd) : null;
        WindowSnapshot? trackedSnapshot = _state.TrackedHwnd != IntPtr.Zero ? CreateSnapshot(_state.TrackedHwnd) : null;

        var result = HudVisibilityLogic.Evaluate(
            _state,
            fgSnapshot,
            trackedSnapshot,
            DateTime.UtcNow,
            _getAllowedApps(),
            _getAllowedRoots(),
            _ownPid,
            ZenLaunchConstants.DefaultDebounceMs);

        ApplyResult(result);
    }

    private void ApplyResult(HudEvaluationResult result)
    {
        if (result.ShouldRetarget)
        {
            ForegroundAllowedAppChanged?.Invoke(result.NewTrackedPid, result.NewTrackedHwnd);
        }

        if (result.ShouldRestoreZenScreen)
        {
            _debounceTimer.Stop();
            ForegroundAllowedAppChanged?.Invoke(0, IntPtr.Zero);
            ZenScreenRestoreRequested?.Invoke();
            return;
        }

        if (result.ShouldShowHud)
        {
            _debounceTimer.Stop();
            HudVisibilityChanged?.Invoke(true);
        }
        else
        {
            HudVisibilityChanged?.Invoke(false);
            if (_state.WindowLostSinceUtc != null && !_debounceTimer.IsEnabled)
            {
                _debounceTimer.Start();
            }
        }
    }

    private static WindowSnapshot CreateSnapshot(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return new WindowSnapshot(hwnd, 0, null, null, null, 0, 0, false, false, false, false);
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        string? procName = ZenShieldWindow.GetProcessNameCached(pid);
        string? imagePath = ZenShieldWindow.GetProcessImagePath((int)pid);

        var sbClass = new StringBuilder(128);
        GetClassName(hwnd, sbClass, sbClass.Capacity);
        string className = sbClass.ToString();

        bool isVisible = IsWindowVisible(hwnd);
        bool isIconic = IsIconic(hwnd);

        int w = 0, h = 0;
        if (isIconic)
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(hwnd, ref wp))
            {
                w = wp.rcNormalPosition.Right - wp.rcNormalPosition.Left;
                h = wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top;
            }
        }
        else if (GetWindowRect(hwnd, out RECT r))
        {
            w = r.Right - r.Left;
            h = r.Bottom - r.Top;
        }

        var sbTitle = new StringBuilder(256);
        GetWindowText(hwnd, sbTitle, sbTitle.Capacity);
        bool hasTitle = sbTitle.Length > 0;

        return new WindowSnapshot(
            Hwnd: hwnd,
            Pid: (int)pid,
            ProcessName: procName,
            ImagePath: imagePath,
            ClassName: className,
            Width: w,
            Height: h,
            IsWindow: true,
            IsVisible: isVisible,
            IsIconic: isIconic,
            HasTitle: hasTitle);
    }

    public void Dispose()
    {
        _debounceTimer.Stop();
        _coalescer.Dispose();

        foreach (IntPtr hook in _hookHandles)
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWinEvent(hook);
            }
        }
        _hookHandles.Clear();
    }
}
