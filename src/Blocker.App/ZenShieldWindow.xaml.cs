using System.Text;
using System.Threading;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Blocker.Core;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Brush = System.Windows.Media.Brush;



namespace Blocker.App;



/// <summary>

/// Representation of a whitelisted application permitted during a Zen Focus session.

/// </summary>

public class WhitelistedAppItem

{

    public string DisplayName { get; set; } = string.Empty;

    public string ExecutableName { get; set; } = string.Empty;



    public WhitelistedAppItem(string exe)

    {

        ExecutableName = exe;

        var withoutExe = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)

            ? exe[..^4]

            : exe;



        DisplayName = withoutExe.ToLowerInvariant() switch

        {

            "code" => "VS Code",

            "spotify" => "Spotify",

            "notepad" => "Notepad",

            "devenv" => "Visual Studio",

            "rider64" => "Rider",

            "slack" => "Slack",

            "obsidian" => "Obsidian",

            "chrome" => "Google Chrome",

            "msedge" => "Microsoft Edge",

            _ => char.ToUpper(withoutExe[0]) + (withoutExe.Length > 1 ? withoutExe[1..] : "")

        };

    }

}



/// <summary>

/// The primary fullscreen, topmost meditation shield kiosk on the primary display.

/// </summary>

public partial class ZenShieldWindow : Window

{

    #region Win32 Interop

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, [Out] StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint WM_CLOSE = 0x0010;

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const int SW_FORCEMINIMIZE = 11;

    public static void SetTaskbarVisibility(bool visible)
    {
        try
        {
            int cmd = visible ? SW_SHOW : SW_HIDE;

            IntPtr primaryTray = FindWindow("Shell_TrayWnd", null);
            if (primaryTray != IntPtr.Zero)
            {
                ShowWindow(primaryTray, cmd);
            }

            IntPtr secondaryTray = IntPtr.Zero;
            while ((secondaryTray = FindWindowEx(IntPtr.Zero, secondaryTray, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                ShowWindow(secondaryTray, cmd);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZenShield] Error setting taskbar visibility: {ex.Message}");
        }
    }

    #endregion



    private readonly DispatcherTimer _sessionTimer;

    private readonly DispatcherTimer _autoSaveTimer;

    private readonly DispatcherTimer _watchdogTimer;

    private readonly Stopwatch _tickSw = Stopwatch.StartNew();
    private static readonly ConcurrentDictionary<int, (string? Path, DateTime CachedAt)> _pidPathCache = new();

    public static string? GetProcessImagePath(int processId)
    {
        if (processId <= 4) return null;

        var now = DateTime.UtcNow;
        if (_pidPathCache.TryGetValue(processId, out var cached) && (now - cached.CachedAt).TotalSeconds < 10.0)
        {
            return cached.Path;
        }

        string? path = null;
        if (OperatingSystem.IsWindows())
        {
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                    {
                        path = sb.ToString();
                    }
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
        }

        _pidPathCache[processId] = (path, now);
        return path;
    }

    private static readonly ConcurrentDictionary<uint, (string Name, DateTime CachedAt)> _pidNameCache = new();

    public static string? GetProcessNameCached(uint pid)
    {
        var now = DateTime.UtcNow;
        if (_pidNameCache.TryGetValue(pid, out var entry) && (now - entry.CachedAt).TotalSeconds < 5.0)
        {
            return entry.Name;
        }

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;
            _pidNameCache[pid] = (name, now);

            if (_pidNameCache.Count > 128)
            {
                foreach (var kvp in _pidNameCache)
                {
                    if ((now - kvp.Value.CachedAt).TotalSeconds > 10.0)
                    {
                        _pidNameCache.TryRemove(kvp.Key, out _);
                    }
                }
            }

            return name;
        }
        catch
        {
            _pidNameCache.TryRemove(pid, out _);
            return null;
        }
    }

    private readonly List<ZenBackdropWindow> _secondaryBackdrops = new();

    private ZenMiniHudWindow? _miniHudWindow;
    private AllowedAppForegroundTracker? _appTracker;
    public event Action<int, IntPtr>? ForegroundAllowedAppChanged;

    private LowLevelKeyboardHook? _keyboardHook;

    private bool _isAllowedAppMode;
    // Debounce and cancellation protection against rapid clicks; _isSwitchingAllowedApp superseded by _switchCts
    private CancellationTokenSource? _switchCts;
    private string? _switchingTo;
    private DateTime _graceUntilUtc;
    private readonly Dictionary<string, DateTime> _recentLaunchUtc = new(StringComparer.OrdinalIgnoreCase);
    private const int DiscoveryTimeoutMs = ZenLaunchConstants.MaxDiscoveryTimeoutMs;
    private DateTime _lastFallbackScanUtc = DateTime.MinValue;
    private readonly ZenLaunchStateMachine _stateMachine = new();

    public ZenLaunchStateMachine LaunchStateMachine => _stateMachine;

    private bool _isSnippingActive;

    private IntPtr _lastAllowedAppHwnd = IntPtr.Zero;

    private readonly HashSet<string> _allowedAppProcessNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _allowedAppRoots = new(StringComparer.OrdinalIgnoreCase);

    private int _unauthorizedBreachCount;

    public string? PasswordHash { get; set; }

    public string? PasswordSalt { get; set; }

    public string? ExpectedPassword { get; set; }

    public MeditationSession? Session { get; set; }

    private int? _sessionId;
    public int SessionId
    {
        get => _sessionId ?? Session?.Id ?? 0;
        set => _sessionId = value;
    }



    private int _remainingSeconds;

    private bool _completeLock;

    private string _lockType = "None";

    private string _profileName = "Deep Work";



    // Ambient audio state




    public int RemainingSeconds

    {

        get => _remainingSeconds;

        set

        {

            _remainingSeconds = Math.Max(0, value);

            UpdateTimerDisplay();

        }

    }



    public bool CompleteLock

    {

        get => _completeLock;

        set

        {

            _completeLock = value;

            UpdateLockStatusUi();

        }

    }



    public string LockType

    {

        get => _lockType;

        set

        {

            _lockType = value ?? "None";

            UpdateLockStatusUi();

        }

    }



    public string SessionProfileName

    {

        get => _profileName;

        set

        {

            _profileName = value ?? "Zen Session";

            SessionSubtitle.Text = $"{_profileName.ToUpperInvariant()} IN PROGRESS";

        }

    }



    public string? ChallengeText { get; set; }



    private readonly MeditationViewModel _viewModel;

    public MeditationViewModel ViewModel => _viewModel;



    public string NotesText

    {

        get => _viewModel.CurrentNote;

        set => _viewModel.CurrentNote = value ?? string.Empty;

    }



    public ObservableCollection<NoteCard> NotesCollection => _viewModel.NotesCollection;



    public bool IsNotesDrawerOpen

    {

        get => _viewModel.IsNotesDrawerOpen;

        set => _viewModel.IsNotesDrawerOpen = value;

    }



    public Action? OnSessionEnded { get; set; }

    public Action<string>? OnNotesChanged { get; set; }

    public Action? OnToggleNotesRequested { get; set; }

    private ZenQuickNotesOverlay? _quickNotesOverlay;

    public Func<bool>? OnVerifyUnlockRequested { get; set; }

    static ZenShieldWindow()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SetTaskbarVisibility(true);
    }

    public ZenShieldWindow() : this(1500, false, "None", null, "Zen Session", null)

    {

    }



    public ZenShieldWindow(

        int totalSeconds,

        bool completeLock = false,

        string? lockType = "None",

        IEnumerable<string>? whitelistedApps = null,

        string? profileName = "Zen Session")

        : this(totalSeconds, completeLock, lockType, whitelistedApps, profileName, null)

    {

    }



    public ZenShieldWindow(

        int totalSeconds,

        bool completeLock,

        string? lockType,

        IEnumerable<string>? whitelistedApps,

        string? profileName,

        MeditationViewModel? viewModel)

    {

        _viewModel = viewModel ?? new MeditationViewModel();

        DataContext = _viewModel;



        InitializeComponent();



        _remainingSeconds = totalSeconds > 0 ? totalSeconds : 1500;

        _completeLock = completeLock;

        _lockType = lockType ?? "None";

        _profileName = profileName ?? "Zen Session";



        SessionSubtitle.Text = $"{_profileName.ToUpperInvariant()} IN PROGRESS";



        // Setup Countdown Timer

        _sessionTimer = new DispatcherTimer

        {

            Interval = TimeSpan.FromSeconds(1)

        };

        _sessionTimer.Tick += SessionTimer_Tick;



        _autoSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _watchdogTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _watchdogTimer.Tick += WatchdogTimer_Tick;

        _appTracker = new AllowedAppForegroundTracker(
            () => _allowedAppProcessNames,
            () => _allowedAppRoots);
        _appTracker.ForegroundAllowedAppChanged += (pid, hwnd) =>
        {
            _lastAllowedAppHwnd = hwnd;
            ForegroundAllowedAppChanged?.Invoke(pid, hwnd);
        };
        ForegroundAllowedAppChanged += OnForegroundAllowedAppChangedForThrottling;
        _appTracker.HudVisibilityChanged += (visible) =>
        {
            if (visible)
            {
                if (_isAllowedAppMode && _lastAllowedAppHwnd != IntPtr.Zero)
                {
                    ShowMiniHud();
                }
            }
            else
            {
                _miniHudWindow?.Hide();
            }
        };
        _appTracker.ZenScreenRestoreRequested += () =>
        {
            RestoreZenScreen();
        };

        UpdateTimerDisplay();

        UpdateLockStatusUi();

        UpdateAudioUi();

        SetWhitelistedApps(whitelistedApps);

        PreviewKeyDown += ZenShieldWindow_PreviewKeyDown;
        Loaded += ZenShieldWindow_Loaded;
    }

    public ZenShieldWindow(
        int totalSeconds,
        bool completeLock,
        string? lockType,
        IEnumerable<string>? whitelistedApps,
        string? profileName,
        MeditationViewModel? viewModel,
        int? sessionId)
        : this(totalSeconds, completeLock, lockType, whitelistedApps, profileName, viewModel)
    {
        _sessionId = sessionId;
    }

    public ZenShieldWindow(
        int totalSeconds,
        bool completeLock,
        string? lockType,
        IEnumerable<string>? whitelistedApps,
        string? profileName,
        MeditationViewModel? viewModel,
        MeditationSession? session,
        string? passwordHash = null,
        string? passwordSalt = null)
        : this(totalSeconds, completeLock, lockType, whitelistedApps, profileName, viewModel, session?.Id)
    {
        Session = session;
        PasswordHash = passwordHash;
        PasswordSalt = passwordSalt;
    }



    public void TriggerSnip()
    {
        ZenScreenSnipper.StartSnip(this, (filePath, markdownTag) =>
        {
            if (IsNotesDrawerOpen && ZenNotesDrawer != null)
            {
                ZenNotesDrawer.InsertAtCursor($"\n{markdownTag}\n");
            }
            else if (_quickNotesOverlay != null && _quickNotesOverlay.IsVisible)
            {
                _quickNotesOverlay.InsertAtCursor($"\n{markdownTag}\n");
            }
        });
    }

    private void ZenShieldWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        bool ctrlDown = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shiftDown = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool altDown = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        if (e.Key == Key.N && ctrlDown && shiftDown)
        {
            IsNotesDrawerOpen = !IsNotesDrawerOpen;
            e.Handled = true;
        }
        else if ((e.Key == Key.N || e.SystemKey == Key.N) && altDown && !ctrlDown && !shiftDown)
        {
            ToggleQuickNotes();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && IsNotesDrawerOpen)
        {
            IsNotesDrawerOpen = false;
            e.Handled = true;
        }
        else if ((e.Key == Key.S && ctrlDown && shiftDown) || e.Key == Key.Snapshot)
        {
            TriggerSnip();
            e.Handled = true;
        }
    }

    public void CloseSessionAndUnlock()
    {
        _completeLock = false;
        EndSession();
    }

    private void ZenShieldWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 1. Automatically spawn backdrop windows covering all secondary monitors
        try
        {
            var spawned = ZenBackdropWindow.SpawnForSecondaryScreens();
            _secondaryBackdrops.AddRange(spawned);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZenShield] Error spawning backdrops: {ex.Message}");
        }

        // Wire Snip Action to Notes Drawer
        if (ZenNotesDrawer != null)
        {
            ZenNotesDrawer.OnSnipRequested = TriggerSnip;
        }

        // 2. Start countdown timer
        _tickSw.Restart();
        _sessionTimer.Start();

        // 3. Start low-level keyboard hook throughout meditation mode (including when running allowed apps with Mini-HUD)
        if (_keyboardHook == null)
        {
            try
            {
                _keyboardHook = new LowLevelKeyboardHook();
                _keyboardHook.OnSnippingRequested = () =>
                {
                    Dispatcher.InvokeAsync(TriggerSnip);
                };
                _keyboardHook.EmergencyEscapeTriggered += () => Dispatcher.Invoke(CloseSessionAndUnlock);
                _keyboardHook.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ZenShield] Keyboard hook failed to start: {ex.Message}");
            }
        }

        // 4. Start active window and Task View watchdog timer (500ms)
        _watchdogTimer.Start();

        // 5. Two-Phase Handshake: Send ConfirmTimerReady IPC request to Service with visible window PIDs
        _ = Task.Run(async () =>
        {
            try
            {
                var visiblePids = Process.GetProcesses().Where(p =>
                {
                    try { return p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle); }
                    catch { return false; }
                }).Select(p => p.Id).Distinct().ToList();

                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    sessionId = _sessionId ?? Session?.Id ?? SessionId,
                    visibleWindowPids = visiblePids,
                    allowedAppRoots = _allowedAppRoots
                });

                var req = new Request
                {
                    Command = CommandNames.ConfirmTimerReady,
                    Args = new Dictionary<string, string> { ["data"] = payload }
                };

                if (_viewModel?.PipeClient != null)
                {
                    await _viewModel.PipeClient.SendRequestAsync(req);
                }
                else
                {
                    var client = new PipeClient();
                    await client.SendRequestAsync(req);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ZenShield] ConfirmTimerReady IPC error: {ex.Message}");
            }
        });
    }



    #region Countdown Timer

    private void SessionTimer_Tick(object? sender, EventArgs e)
    {
        long gap = _tickSw.ElapsedMilliseconds;
        _tickSw.Restart();
        if (gap > 1300)
        {
            Debug.WriteLine($"[UI-STALL] tick gap {gap} ms");
        }

        if (_remainingSeconds > 0)
        {
            _remainingSeconds--;
            UpdateTimerDisplay();

            if (_remainingSeconds <= 0)
            {
                _sessionTimer.Stop();
                OnSessionTimeElapsed();
            }
        }
    }



    private void UpdateTimerDisplay()

    {

        string formattedTime = FormatAdaptiveTime(_remainingSeconds);

        TimerDisplay.Text = formattedTime;

        _miniHudWindow?.UpdateTimer(formattedTime);

    }



    public static string FormatAdaptiveTime(int totalSeconds)

    {

        if (totalSeconds < 0) totalSeconds = 0;

        int hours = totalSeconds / 3600;

        int minutes = (totalSeconds % 3600) / 60;

        int seconds = totalSeconds % 60;



        return hours > 0

            ? $"{hours:D2}:{minutes:D2}:{seconds:D2}"

            : $"{minutes:D2}:{seconds:D2}";

    }



    private void OnSessionTimeElapsed()

    {

        TimerDisplay.Text = "00:00";

        SessionSubtitle.Text = "ZEN SESSION COMPLETE";

        StopSessionButton.IsEnabled = true;

        StopSessionButton.Content = "Complete Session";

        StopSessionButton.Style = (Style)FindResource("PrimaryButton");

    }

    #endregion



    #region Whitelisted Apps & Mini-HUD

    public void SetWhitelistedApps(IEnumerable<string>? apps)
    {
        var list = new List<WhitelistedAppItem>();
        _allowedAppProcessNames.Clear();
        _allowedAppRoots.Clear();

        if (apps != null)
        {
            foreach (var app in apps)
            {
                if (!string.IsNullOrWhiteSpace(app))
                {
                    string trimmed = app.Trim();
                    list.Add(new WhitelistedAppItem(trimmed));

                    string procName = trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? trimmed[..^4]
                        : trimmed;
                    _allowedAppProcessNames.Add(procName);

                    string normProc = ProcessMatcher.NormalizeProcessName(procName);
                    try
                    {
                        string? resolvedPath = AppPathResolver.ResolveExecutablePath(trimmed);
                        if (!string.IsNullOrWhiteSpace(resolvedPath))
                        {
                            string? dir = Path.GetDirectoryName(resolvedPath);
                            if (!string.IsNullOrWhiteSpace(dir))
                            {
                                var parent = Directory.GetParent(dir);
                                if (parent != null && Path.GetFileName(dir).StartsWith("app-", StringComparison.OrdinalIgnoreCase))
                                {
                                    dir = parent.FullName;
                                }
                                _allowedAppRoots[normProc] = dir;
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        WhitelistedAppsDock.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WhitelistedAppsItemsControl.ItemsSource = list;
    }



    private void WhitelistedAppButton_Click(object sender, RoutedEventArgs e)

    {

        if (sender is FrameworkElement fe && fe.Tag is WhitelistedAppItem item)

        {

            SwitchToAllowedApp(item);

        }

    }



    /// <summary>
    /// Robust, non-caching top-level window locator for processes.
    /// Filters out dummy, UAC, tooltip, and IME windows (e.g. UAC_InputIndicatorOverlayWnd, QTrayIconMessageWindowClass),
    /// correctly returning the primary application window for VLC, Notepad, Obsidian, etc.
    /// Supports minimized windows via GetWindowPlacement rcNormalPosition and iconic window titles.
    /// </summary>
    public static IntPtr FindMainWindowForProcess(string processName, int? targetPid = null)
    {
        return FindMainWindowForProcess(processName, null, targetPid, allowIconic: true);
    }

    public static IntPtr FindMainWindowForProcess(string processName, IReadOnlyDictionary<string, string>? allowedRoots, int? targetPid = null)
    {
        return FindMainWindowForProcess(processName, allowedRoots, targetPid, allowIconic: true);
    }

    public static IntPtr FindMainWindowForProcess(
        string processName,
        IReadOnlyDictionary<string, string>? allowedRoots,
        int? targetPid = null,
        bool allowIconic = true)
    {
        string normTarget = ProcessMatcher.NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(normTarget)) return IntPtr.Zero;

        IntPtr bestHwnd = IntPtr.Zero;
        long bestArea = 0;

        var candidatePids = new HashSet<uint>();

        try
        {
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindow(hWnd) || !IsWindowVisible(hWnd))
                    return true;

                // Exclude known dummy / indicator / tool / IME classes
                var sbClass = new System.Text.StringBuilder(128);
                GetClassName(hWnd, sbClass, sbClass.Capacity);
                string cls = sbClass.ToString();

                int w = 0, h = 0;
                bool isIconic = IsIconic(hWnd);
                if (isIconic)
                {
                    if (!allowIconic)
                    {
                        return true;
                    }

                    var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                    if (GetWindowPlacement(hWnd, ref wp))
                    {
                        w = wp.rcNormalPosition.Right - wp.rcNormalPosition.Left;
                        h = wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top;
                    }
                }
                else if (GetWindowRect(hWnd, out RECT r))
                {
                    w = r.Right - r.Left;
                    h = r.Bottom - r.Top;
                }

                var sbTitle = new System.Text.StringBuilder(256);
                GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
                bool hasTitle = sbTitle.Length > 0;

                if (!ZenLaunchMatcher.IsRealAppWindow(cls, w, h, isIconic, hasTitle))
                {
                    return true;
                }

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid <= 4)
                    return true;

                string? procName = GetProcessNameCached(pid);
                if (string.IsNullOrEmpty(procName))
                    return true;

                string normProc = ProcessMatcher.NormalizeProcessName(procName);
                if (!string.Equals(normProc, normTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (allowedRoots != null && allowedRoots.TryGetValue(normTarget, out var rootDir) && !string.IsNullOrWhiteSpace(rootDir))
                {
                    string? imagePath = GetProcessImagePath((int)pid);
                    if (string.IsNullOrWhiteSpace(imagePath) || !ProtectedProcesses.IsPathInsideDirectory(imagePath, rootDir))
                    {
                        return true;
                    }
                }

                candidatePids.Add(pid);

                long area = (long)Math.Max(w, ZenLaunchConstants.MinWindowDimension) * Math.Max(h, ZenLaunchConstants.MinWindowDimension);

                // Prefer windows with actual titles, or largest area
                if (hasTitle && area > bestArea)
                {
                    bestArea = area;
                    bestHwnd = hWnd;
                }
                else if (bestHwnd == IntPtr.Zero && area > bestArea)
                {
                    bestArea = area;
                    bestHwnd = hWnd;
                }

                return true;
            }, IntPtr.Zero);
        }
        catch { }

        if (candidatePids.Count == 0) return IntPtr.Zero; // nothing running -> no window can exist

        return bestHwnd;
    }

    private static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }

        SetForegroundWindow(hwnd);
    }

    public void SwitchToAllowedApp(WhitelistedAppItem app)
    {
        string name = app.ExecutableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? app.ExecutableName[..^4]
            : app.ExecutableName;

        // Debounce rapid accidental double-clicks within SettleDelayMs (300ms) for the same app
        if (_switchCts != null && name.Equals(_switchingTo, StringComparison.OrdinalIgnoreCase))
        {
            if (_stateMachine.CurrentState == ZenLaunchState.Launching &&
                (DateTime.UtcNow - _stateMachine.StateEnteredUtc).TotalMilliseconds < ZenLaunchConstants.SettleDelayMs)
            {
                return;
            }
        }

        _switchCts?.Cancel();

        var cts = _switchCts = new CancellationTokenSource();
        _switchingTo = name;
        _graceUntilUtc = DateTime.UtcNow.Add(ZenLaunchConstants.LaunchGracePeriod);
        _isAllowedAppMode = true;
        _unauthorizedBreachCount = 0;

        _appTracker?.StartColdLaunch(name, ZenLaunchConstants.LaunchGracePeriodMs);

        // 1. Check if a USABLE window already exists (Warm switch)
        IntPtr existingHwnd = FindMainWindowForProcess(name, _allowedAppRoots, null);
        if (existingHwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(existingHwnd, out uint winPid);
            _stateMachine.RequestLaunch(name, hasUsableWindow: true, existingHwnd, (int)winPid);

            _lastAllowedAppHwnd = existingHwnd;
            _appTracker?.SetTrackedWindow(existingHwnd, (int)winPid, name);
            BringToFront(existingHwnd);
            ShowMiniHud();
            _miniHudWindow?.ClearStatus();

            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            Debug.WriteLine($"[ZenLaunch] [{timestamp}] Warm switch completed for '{name}' (HWND: 0x{existingHwnd:X}).");

            _switchCts = null;
            _switchingTo = null;
            return;
        }

        // 2. Cold launch required
        _stateMachine.RequestLaunch(name, hasUsableWindow: false);
        _miniHudWindow?.SetStatus($"Opening {app.DisplayName}…", false, null);

        _ = RunSwitchAsync(app, name, cts);
    }

    private async Task RunSwitchAsync(WhitelistedAppItem app, string name, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var sw = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();

            string startTimestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            Debug.WriteLine($"[ZenLaunch] [{startTimestamp}] Initiating cold launch for '{name}'...");

            // Never skip launch just because background processes exist!
            var r = await Task.Run(() => TryLaunch(app), ct);
            if (r.Error != null)
            {
                _stateMachine.LaunchFailed(r.Error);
                _appTracker?.ResetToZen();
                _isAllowedAppMode = false;
                RestoreZenScreen();
                string errTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
                Debug.WriteLine($"[ZenLaunch] [{errTime}] Cold launch failed for '{name}': {r.Error}");
                _miniHudWindow?.SetStatus(r.Error + " — click to retry", true, () => SwitchToAllowedApp(app));
                return;
            }

            int? pid = r.Pid;
            _recentLaunchUtc[name] = DateTime.UtcNow;

            IntPtr hwnd = IntPtr.Zero;
            while (hwnd == IntPtr.Zero && sw.ElapsedMilliseconds < DiscoveryTimeoutMs)
            {
                await Task.Delay(ZenLaunchConstants.PollIntervalMs, ct);
                hwnd = await Task.Run(() => FindMainWindowForProcess(name, _allowedAppRoots, pid), ct);
            }

            if (hwnd == IntPtr.Zero)
            {
                _stateMachine.Timeout();
                _appTracker?.ResetToZen();
                _isAllowedAppMode = false;
                RestoreZenScreen();
                string timeoutTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
                Debug.WriteLine($"[ZenLaunch] [{timeoutTime}] Cold launch discovery timed out after {sw.ElapsedMilliseconds}ms for '{name}'.");
                _miniHudWindow?.SetStatus($"{app.DisplayName} is taking longer than usual — click to retry", true, () => SwitchToAllowedApp(app));
                return;
            }

            // Discovered allowed app window (discoveredHwnd)
            _lastAllowedAppHwnd = hwnd;
            GetWindowThreadProcessId(hwnd, out uint winPid);
            _stateMachine.WindowDiscovered(hwnd, (int)winPid);
            _appTracker?.SetTrackedWindow(hwnd, (int)winPid, name);
            BringToFront(hwnd);
            ShowMiniHud();
            _miniHudWindow?.ClearStatus();

            string successTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            Debug.WriteLine($"[ZenLaunch] [{successTime}] Cold launch completed for '{name}' in {sw.ElapsedMilliseconds}ms (HWND: 0x{hwnd:X}).");
        }
        catch (OperationCanceledException)
        {
            string cancelTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            Debug.WriteLine($"[ZenLaunch] [{cancelTime}] Launch/switch to '{name}' was cancelled.");
        }
        catch (Exception ex)
        {
            string exTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            Debug.WriteLine($"[ZenLaunch] [{exTime}] RunSwitchAsync error for '{name}': {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_switchCts, cts))
            {
                _switchCts = null;
                _switchingTo = null;
            }
            cts.Dispose();
        }
    }

    public static (int? Pid, string? Error) TryLaunch(WhitelistedAppItem app)
    {
        try
        {
            string? resolvedPath = AppPathResolver.ResolveExecutablePath(app.ExecutableName);
            string target = !string.IsNullOrWhiteSpace(resolvedPath) ? resolvedPath : app.ExecutableName;

            string workingDir;
            if (Path.IsPathRooted(target) && !string.IsNullOrWhiteSpace(Path.GetDirectoryName(target)))
            {
                workingDir = Path.GetDirectoryName(target)!;
            }
            else
            {
                workingDir = Environment.SystemDirectory;
            }

            var psi = new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = workingDir
            };

            Process? proc = Process.Start(psi);
            int? pid = null;
            try
            {
                if (proc != null && !proc.HasExited)
                {
                    pid = proc.Id;
                }
            }
            catch { }

            return (pid, null);
        }
        catch (System.ComponentModel.Win32Exception wEx)
        {
            Debug.WriteLine($"[ZenShield] TryLaunch Win32Exception for '{app.ExecutableName}': {wEx}");
            string error = (wEx.NativeErrorCode is 2 or 3)
                ? $"Couldn't find {app.DisplayName}"
                : $"Couldn't open {app.DisplayName}";
            return (null, error);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZenShield] TryLaunch Exception for '{app.ExecutableName}': {ex}");
            return (null, $"Couldn't open {app.DisplayName}");
        }
    }

    private void ShowMiniHud()
    {
        _isAllowedAppMode = true;

        if (_miniHudWindow == null)
        {
            _miniHudWindow = new ZenMiniHudWindow
            {
                OnReturnToZenRequested = RestoreZenScreen,
                OnToggleAudioRequested = ToggleAudioPlayback,
                OnToggleNotesRequested = ToggleQuickNotes,
                OnSnipRequested = TriggerSnip
            };
        }

        _miniHudWindow.UpdateTimer(FormatAdaptiveTime(_remainingSeconds));
        _miniHudWindow.UpdateAudioLabel(GetAudioHudLabel());
        _miniHudWindow.Topmost = true;
        _miniHudWindow.Show();

        if (!_miniHudWindow.HasBeenPositionedInitially)
        {
            _miniHudWindow.PositionAtTopCenter();
        }
        else
        {
            _miniHudWindow.ApplyClampingAndSnapping();
        }

        // While an allowed application is active, the primary ZenShieldWindow yields topmost
        // so the allowed app floats comfortably without flickering or focus battles.
        Topmost = false;

        // Keep secondary backdrops topmost
        foreach (var backdrop in _secondaryBackdrops)
        {
            backdrop.Topmost = true;
        }

        // Keep the allowed application window non-topmost and focused, while Mini-HUD floats above it in topmost band
        if (_lastAllowedAppHwnd != IntPtr.Zero && IsWindow(_lastAllowedAppHwnd))
        {
            SetWindowPos(_lastAllowedAppHwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetForegroundWindow(_lastAllowedAppHwnd);
        }

        _miniHudWindow.EnsureTopmost();
    }



    public void ToggleQuickNotes()

    {

        if (OnToggleNotesRequested != null)

        {

            OnToggleNotesRequested.Invoke();

            return;

        }



        if (_quickNotesOverlay != null && _quickNotesOverlay.IsVisible)
        {
            NotesVaultStore.FlushSync();
            _quickNotesOverlay.Hide();
            return;
        }

        if (_quickNotesOverlay == null)
        {
            _quickNotesOverlay = new ZenQuickNotesOverlay();
            _quickNotesOverlay.OnNotesChanged = text =>
            {
                NotesText = text;
                OnNotesChanged?.Invoke(text);
            };
            _quickNotesOverlay.Closed += (s, e) =>
            {
                NotesVaultStore.FlushSync();
                _quickNotesOverlay = null;
            };
        }



        _quickNotesOverlay.LoadNotes(NotesText);

        _quickNotesOverlay.Show();

        _quickNotesOverlay.Activate();

    }



    public void RestoreZenScreen()
    {
        _switchCts?.Cancel();
        _switchCts = null;
        _switchingTo = null;
        _recentLaunchUtc.Clear();
        _stateMachine.ReturnToZen();

        _isAllowedAppMode = false;
        _unauthorizedBreachCount = 0;
        _appTracker?.ResetToZen();

        if (_lastAllowedAppHwnd != IntPtr.Zero && IsWindow(_lastAllowedAppHwnd))
        {
            SetWindowPos(_lastAllowedAppHwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            ShowWindow(_lastAllowedAppHwnd, SW_MINIMIZE);
        }
        _lastAllowedAppHwnd = IntPtr.Zero;

        if (_miniHudWindow != null)
        {
            _quickNotesOverlay?.Hide();
            _miniHudWindow.Hide();
            _miniHudWindow.ClearStatus();
        }

        WindowState = WindowState.Maximized;
        Topmost = true;
        Show();
        Activate();

        var mainHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (mainHandle != IntPtr.Zero)
        {
            SetWindowPos(mainHandle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(mainHandle);
        }

        foreach (var backdrop in _secondaryBackdrops)
        {
            backdrop.WindowState = WindowState.Maximized;
            backdrop.Topmost = true;
            backdrop.Show();
            var secHandle = new System.Windows.Interop.WindowInteropHelper(backdrop).Handle;
            if (secHandle != IntPtr.Zero)
            {
                SetWindowPos(secHandle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            }
        }

        ForegroundAllowedAppChanged?.Invoke(0, IntPtr.Zero);
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Debug.WriteLine($"[ZenLaunch] [{timestamp}] RestoreZenScreen completed cleanly.");
    }

    #endregion



    #region Ambient Audio Controls

    public void ToggleAudioPlayback()

    {

        _viewModel.ToggleSoundscape();

        UpdateAudioUi();

    }



    private void UpdateAudioUi()

    {

        _miniHudWindow?.UpdateAudioLabel(GetAudioHudLabel());

    }



    private string GetAudioHudLabel()

    {

        return _viewModel.IsSoundscapePlaying

            ? $"{_viewModel.SelectedSoundscapeDisplayName} [⏸]"

            : $"{_viewModel.SelectedSoundscapeDisplayName} [▶]";

    }

    #endregion



    #region Lock Verification & Stop Session

    private void UpdateLockStatusUi()

    {

        if (StopSessionButton == null || LockStatusNotice == null)

        {

            return;

        }



        if (_completeLock)

        {

            StopSessionButton.IsEnabled = false;

            StopSessionButton.Content = "🔒 Unbreakable Lock Active (Exit Disabled)";

            StopSessionButton.Opacity = 0.5;

            LockStatusNotice.Visibility = Visibility.Collapsed;

        }

        else if (string.Equals(_lockType, "Password", StringComparison.OrdinalIgnoreCase))

        {

            StopSessionButton.IsEnabled = true;

            StopSessionButton.Content = "Stop Session";

            StopSessionButton.Opacity = 1.0;

            LockStatusNotice.Text = "Requires password verification to exit early";

            LockStatusNotice.Visibility = Visibility.Visible;

        }

        else if (string.Equals(_lockType, "TypingChallenge", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(_lockType, "Challenge", StringComparison.OrdinalIgnoreCase))
        {
            StopSessionButton.IsEnabled = true;
            StopSessionButton.Content = "Stop Session";
            StopSessionButton.Opacity = 1.0;
            LockStatusNotice.Text = "Requires typing challenge verification to exit early";
            LockStatusNotice.Visibility = Visibility.Visible;
        }
        else
        {
            StopSessionButton.IsEnabled = true;
            StopSessionButton.Content = "Stop Session";
            StopSessionButton.Opacity = 1.0;
            LockStatusNotice.Visibility = Visibility.Collapsed;
        }
    }

    private void StopSession_Click(object sender, RoutedEventArgs e)
    {
        if (_completeLock)
        {
            return;
        }

        // If session finished, immediately close
        if (_remainingSeconds <= 0)
        {
            EndSession();
            return;
        }

        // If custom verification callback provided
        if (OnVerifyUnlockRequested != null)
        {
            if (OnVerifyUnlockRequested())
            {
                EndSession();
            }
            return;
        }

        // Verification based on LockType
        if (string.Equals(_lockType, "Password", StringComparison.OrdinalIgnoreCase))
        {
            var pwdDlg = new PasswordUnlockDialog(_profileName)
            {
                Owner = this,
                ValidatePassword = VerifyPassword
            };

            pwdDlg.Closing += (s, ev) =>
            {
                if (pwdDlg.DialogResult == true)
                {
                    if (!VerifyPassword(pwdDlg.ResultPassword))
                    {
                        ev.Cancel = true;
                        pwdDlg.SetError("Incorrect password. Access denied.");
                    }
                }
            };

            if (pwdDlg.ShowDialog() == true)
            {
                if (VerifyPassword(pwdDlg.ResultPassword))
                {
                    EndSession();
                }
                else
                {
                    pwdDlg.SetError("Incorrect password. Access denied.");
                }
            }
            return;
        }

        if (string.Equals(_lockType, "TypingChallenge", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_lockType, "Challenge", StringComparison.OrdinalIgnoreCase))
        {
            string challenge = !string.IsNullOrWhiteSpace(ChallengeText)
                ? ChallengeText
                : TypingChallengeManager.GenerateChallenge(_profileName, 48);

            var challengeDlg = new TypingChallengeDialog(challenge, "Exit Zen Focus Session")
            {
                Owner = this
            };

            if (challengeDlg.ShowDialog() == true)
            {
                EndSession();
            }
            return;
        }

        // Standard exit without locks
        EndSession();
    }

    public bool VerifyPassword(string inputPassword)
    {
        if (string.IsNullOrEmpty(inputPassword)) return false;

        // 1. ExpectedPassword plain string match
        if (!string.IsNullOrEmpty(ExpectedPassword) && string.Equals(inputPassword, ExpectedPassword, StringComparison.Ordinal))
        {
            return true;
        }

        // 2. PBKDF2 hash & salt verification
        if (!string.IsNullOrEmpty(PasswordHash) && !string.IsNullOrEmpty(PasswordSalt) &&
            PasswordSecurity.VerifyPassword(inputPassword, PasswordSalt, PasswordHash))
        {
            return true;
        }

        // 2b. PasswordHash as plain string
        if (!string.IsNullOrEmpty(PasswordHash) && string.Equals(inputPassword, PasswordHash, StringComparison.Ordinal))
        {
            return true;
        }

        // 2c. SHA-256 hash comparison
        string inputSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inputPassword))).ToLowerInvariant();
        if (!string.IsNullOrEmpty(PasswordHash) && string.Equals(inputSha, PasswordHash, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 3. Check Session object via reflection or properties
        if (Session != null)
        {
            var sType = Session.GetType();
            var sPwd = sType.GetProperty("Password")?.GetValue(Session) as string;
            if (!string.IsNullOrEmpty(sPwd) && string.Equals(inputPassword, sPwd, StringComparison.Ordinal)) return true;

            var sHash = sType.GetProperty("PasswordHash")?.GetValue(Session) as string;
            var sSalt = sType.GetProperty("PasswordSalt")?.GetValue(Session) as string;
            if (!string.IsNullOrEmpty(sHash))
            {
                if (!string.IsNullOrEmpty(sSalt) && PasswordSecurity.VerifyPassword(inputPassword, sSalt, sHash)) return true;
                if (string.Equals(inputSha, sHash, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(inputPassword, sHash, StringComparison.Ordinal)) return true;
            }
        }

        // 4. Check _viewModel via reflection
        if (_viewModel != null)
        {
            var vmType = _viewModel.GetType();
            var vmPwd = (vmType.GetProperty("Password") ?? vmType.GetProperty("StoredPassword") ?? vmType.GetProperty("UnlockPassword"))?.GetValue(_viewModel) as string;
            if (!string.IsNullOrEmpty(vmPwd) && string.Equals(inputPassword, vmPwd, StringComparison.Ordinal)) return true;

            var vmHash = (vmType.GetProperty("PasswordHash") ?? vmType.GetProperty("StoredPasswordHash"))?.GetValue(_viewModel) as string;
            var vmSalt = (vmType.GetProperty("PasswordSalt") ?? vmType.GetProperty("StoredPasswordSalt"))?.GetValue(_viewModel) as string;
            if (!string.IsNullOrEmpty(vmHash))
            {
                if (!string.IsNullOrEmpty(vmSalt) && PasswordSecurity.VerifyPassword(inputPassword, vmSalt, vmHash)) return true;
                if (string.Equals(inputSha, vmHash, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(inputPassword, vmHash, StringComparison.Ordinal)) return true;
            }
        }

        // 5. OnVerifyUnlockRequested callback
        if (OnVerifyUnlockRequested != null)
        {
            return OnVerifyUnlockRequested();
        }

        return false;
    }

    private void WatchdogTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            // 1. Suppress MultitaskingViewFrame (Windows Task View handles) continuously
            SuppressTaskView();

            // 3. Active window watchdog
            IntPtr fgHwnd = GetForegroundWindow();
            if (fgHwnd == IntPtr.Zero) return;

            uint currentPid = (uint)Process.GetCurrentProcess().Id;
            GetWindowThreadProcessId(fgHwnd, out uint fgPid);

            if (fgPid == currentPid)
            {
                _unauthorizedBreachCount = 0;
                if (_isAllowedAppMode && _lastAllowedAppHwnd != IntPtr.Zero)
                {
                    if (!IsWindow(_lastAllowedAppHwnd) || !IsWindowVisible(_lastAllowedAppHwnd) || IsIconic(_lastAllowedAppHwnd))
                    {
                        _appTracker?.EvaluateNow();
                    }
                }
                return; // Whitelisted self / dialogs / Mini-HUD
            }

            var sbClass = new System.Text.StringBuilder(256);
            GetClassName(fgHwnd, sbClass, sbClass.Capacity);
            string className = sbClass.ToString();
            if (string.Equals(className, "MultitaskingViewFrame", StringComparison.OrdinalIgnoreCase))
            {
                ShowWindow(fgHwnd, SW_HIDE);
                PostMessage(fgHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                EnforceFocus();
                return;
            }

            string? procName = GetProcessNameCached(fgPid);

            if (!string.IsNullOrEmpty(procName))
            {
                string? helperPath = GetProcessImagePath((int)fgPid);

                if (ProtectedProcesses.IsOwnProcess(procName, helperPath))
                {
                    _unauthorizedBreachCount = 0;
                    return;
                }

                bool isClippingProc = (string.Equals(procName, "SnippingTool", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(procName, "ScreenClippingHost", StringComparison.OrdinalIgnoreCase)) &&
                                      ProtectedProcesses.IsProtected(procName, helperPath);

                if (isClippingProc)
                {
                    if (!_isSnippingActive || Topmost)
                    {
                        YieldTopmostForSnipping();
                    }
                    return;
                }
                else if (_isSnippingActive)
                {
                    RestoreTopmostAfterSnipping();
                }

                bool isAllowedApp = ZenLaunchMatcher.IsAppAllowed(procName, procName, _allowedAppProcessNames, helperPath, _allowedAppRoots);

                if (isAllowedApp)
                {
                    GetWindowRect(fgHwnd, out RECT r);
                    int w = r.Right - r.Left;
                    int h = r.Bottom - r.Top;
                    bool isIconic = IsIconic(fgHwnd);
                    var sbTitle = new System.Text.StringBuilder(256);
                    GetWindowText(fgHwnd, sbTitle, sbTitle.Capacity);
                    bool isRealApp = ZenLaunchMatcher.IsRealAppWindow(className, w, h, isIconic, sbTitle.Length > 0);

                    if (isRealApp)
                    {
                        _unauthorizedBreachCount = 0;
                        if (_isAllowedAppMode)
                        {
                            if (fgHwnd != _lastAllowedAppHwnd)
                            {
                                _lastAllowedAppHwnd = fgHwnd;
                                _appTracker?.SetTrackedWindow(fgHwnd, (int)fgPid, procName ?? "");
                            }
                            _miniHudWindow?.EnsureTopmost();
                        }
                        // If !_isAllowedAppMode: do NOT flip mode! Mode only changes via explicit launch/switch
                        return;
                    }
                    else if (_isAllowedAppMode)
                    {
                        // Helper / toast / tooltip window of allowed app during allowed app mode
                        _unauthorizedBreachCount = 0;
                        _miniHudWindow?.EnsureTopmost();
                        return;
                    }
                }

                // Whitelist OS text and frame host helpers (TextInputHost, ApplicationFrameHost, ctfmon, etc.) via SystemHelperGuard
                bool isTrustedHelper = SystemHelperGuard.IsTrusted(helperPath);

                if ((_isAllowedAppMode || _switchCts != null) && isTrustedHelper)
                {
                    _unauthorizedBreachCount = 0;
                    return;
                }

                if ((_switchCts != null || _stateMachine.CurrentState == ZenLaunchState.Launching) && DateTime.UtcNow < _graceUntilUtc)
                {
                    // While actively launching or discovering an allowed app within bounded grace period, do not suppress foreground window
                    return;
                }
            }

            // fgHwnd is neither Sankalpa.exe nor in _allowedAppProcessNames (and not SnippingTool/ScreenClippingHost)
            // Actively suppress the unauthorized foreground window immediately:
            ShowWindow(fgHwnd, SW_FORCEMINIMIZE);
            ShowWindow(fgHwnd, SW_HIDE);

            _unauthorizedBreachCount++;

            // If the allowed app is no longer valid or if unauthorized apps repeatedly breach,
            // re-assert ZenShieldWindow as Topmost = true (RestoreZenScreen()) to immediately cover the screen with the Zen Shield!
            if (IsAllowedAppValid() && _unauthorizedBreachCount < ZenLaunchConstants.MaxUnauthorizedBreaches)
            {
                if (IsIconic(_lastAllowedAppHwnd))
                {
                    ShowWindow(_lastAllowedAppHwnd, SW_RESTORE);
                }
                SetForegroundWindow(_lastAllowedAppHwnd);
                _miniHudWindow?.EnsureTopmost();
            }
            else if (_unauthorizedBreachCount >= ZenLaunchConstants.MaxUnauthorizedBreaches)
            {
                _unauthorizedBreachCount = 0;
                RestoreZenScreen();
            }
            else if (_isAllowedAppMode)
            {
                // Tracked app is minimized or lost: delegate lifecycle transition to tracker's 750ms debounce
                _appTracker?.EvaluateNow();
            }
            else
            {
                _unauthorizedBreachCount = 0;
                RestoreZenScreen();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZenShield] Watchdog error: {ex.Message}");
        }
    }

    private void YieldTopmostForSnipping()
    {
        _isSnippingActive = true;
        if (Topmost)
        {
            Topmost = false;
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                SetWindowPos(handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            foreach (var backdrop in _secondaryBackdrops)
            {
                backdrop.Topmost = false;
                var secH = new System.Windows.Interop.WindowInteropHelper(backdrop).Handle;
                if (secH != IntPtr.Zero)
                {
                    SetWindowPos(secH, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }
        }
    }

    private void RestoreTopmostAfterSnipping()
    {
        _isSnippingActive = false;
        if (!_isAllowedAppMode && !Topmost)
        {
            Topmost = true;
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                SetForegroundWindow(handle);
            }
            foreach (var backdrop in _secondaryBackdrops)
            {
                backdrop.Topmost = true;
                var secH = new System.Windows.Interop.WindowInteropHelper(backdrop).Handle;
                if (secH != IntPtr.Zero)
                {
                    SetWindowPos(secH, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                }
            }
        }
    }

    private static void SuppressTaskView()
    {
        try
        {
            IntPtr taskViewHwnd = IntPtr.Zero;
            while ((taskViewHwnd = FindWindowEx(IntPtr.Zero, taskViewHwnd, "MultitaskingViewFrame", null)) != IntPtr.Zero)
            {
                ShowWindow(taskViewHwnd, SW_HIDE);
                PostMessage(taskViewHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch { }
    }

    private bool IsAllowedAppValid()
    {
        if (!_isAllowedAppMode)
        {
            return false;
        }

        if (_lastAllowedAppHwnd != IntPtr.Zero &&
            IsWindow(_lastAllowedAppHwnd) &&
            IsWindowVisible(_lastAllowedAppHwnd) &&
            !IsIconic(_lastAllowedAppHwnd))
        {
            try
            {
                GetWindowThreadProcessId(_lastAllowedAppHwnd, out uint pid);
                if (pid != 0)
                {
                    string? procName = GetProcessNameCached(pid);
                    string? imgPath = GetProcessImagePath((int)pid);
                    return ZenLaunchMatcher.IsAppAllowed(procName, imgPath, _allowedAppProcessNames, _allowedAppRoots);
                }
            }
            catch { }
        }

        var now = DateTime.UtcNow;
        if (now - _lastFallbackScanUtc < TimeSpan.FromSeconds(2))
        {
            return false;
        }

        _lastFallbackScanUtc = now;
        foreach (var allowedName in _allowedAppProcessNames)
        {
            IntPtr found = FindMainWindowForProcess(allowedName, _allowedAppRoots, null, allowIconic: false);
            if (found != IntPtr.Zero)
            {
                _lastAllowedAppHwnd = found;
                return true;
            }
        }
        return false;
    }

    private void EnforceFocus()
    {
        if (IsAllowedAppValid() && _unauthorizedBreachCount < ZenLaunchConstants.MaxUnauthorizedBreaches)
        {
            if (IsIconic(_lastAllowedAppHwnd))
            {
                ShowWindow(_lastAllowedAppHwnd, SW_RESTORE);
            }
            SetForegroundWindow(_lastAllowedAppHwnd);
            _miniHudWindow?.EnsureTopmost();
        }
        else if (_unauthorizedBreachCount >= ZenLaunchConstants.MaxUnauthorizedBreaches)
        {
            _unauthorizedBreachCount = 0;
            RestoreZenScreen();
        }
        else if (_isAllowedAppMode)
        {
            _appTracker?.EvaluateNow();
        }
        else
        {
            _unauthorizedBreachCount = 0;
            RestoreZenScreen();
        }
    }

    private void EndSession()
    {
        _stateMachine.StartClosing();
        _sessionTimer.Stop();
        _autoSaveTimer.Stop();
        _watchdogTimer.Stop();
        _appTracker?.Dispose();
        _appTracker = null;
        _quickNotesOverlay?.Close();
        _miniHudWindow?.Close();

        if (_lastAllowedAppHwnd != IntPtr.Zero && IsWindow(_lastAllowedAppHwnd))
        {
            SetWindowPos(_lastAllowedAppHwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            ShowWindow(_lastAllowedAppHwnd, SW_MINIMIZE);
        }

        SetTaskbarVisibility(true);

        if (System.Windows.Application.Current?.MainWindow is MainWindow mw)
        {
            mw.WindowState = WindowState.Normal;
            mw.Show();
            mw.Activate();
            var mwHandle = new WindowInteropHelper(mw).Handle;
            if (mwHandle != IntPtr.Zero)
            {
                SetForegroundWindow(mwHandle);
            }
        }

        OnSessionEnded?.Invoke();
        Close();
    }

    #endregion



    protected override void OnClosed(EventArgs e)

    {

        base.OnClosed(e);

        _stateMachine.StartClosing();
        _sessionTimer.Stop();
        _autoSaveTimer.Stop();
        _watchdogTimer.Stop();
        _appTracker?.Dispose();
        _appTracker = null;

        if (_lastAllowedAppHwnd != IntPtr.Zero && IsWindow(_lastAllowedAppHwnd))
        {
            SetWindowPos(_lastAllowedAppHwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            ShowWindow(_lastAllowedAppHwnd, SW_MINIMIZE);
        }

        SetTaskbarVisibility(true);



        // Close floating mini-HUD

        if (_miniHudWindow != null)

        {

            try

            {

                _miniHudWindow.Close();

            }

            catch

            {

            }

            _miniHudWindow = null;

        }



        // Dispose low-level keyboard hook

        if (_keyboardHook != null)

        {

            try

            {

                _keyboardHook.Stop();

                _keyboardHook.Dispose();

            }

            catch { }

            _keyboardHook = null;

        }



        // Close all secondary backdrop windows

        foreach (var backdrop in _secondaryBackdrops)

        {

            try

            {

                backdrop.Close();

            }

            catch

            {

            }

        }

        _secondaryBackdrops.Clear();

    }

}

