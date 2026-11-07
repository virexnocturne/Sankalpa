using System;

using System.Collections.Generic;
using System.Collections.ObjectModel;

using System.Diagnostics;

using System.IO;

using System.Runtime.InteropServices;

using System.Windows;

using System.Windows.Controls;

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

    private readonly List<ZenBackdropWindow> _secondaryBackdrops = new();

    private ZenMiniHudWindow? _miniHudWindow;

    private LowLevelKeyboardHook? _keyboardHook;

    private bool _isAllowedAppMode;
    private bool _isSwitchingAllowedApp;

    private bool _isSnippingActive;

    private IntPtr _lastAllowedAppHwnd = IntPtr.Zero;

    private readonly HashSet<string> _allowedAppProcessNames = new(StringComparer.OrdinalIgnoreCase);

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
                    visibleWindowPids = visiblePids
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



    public async void SwitchToAllowedApp(WhitelistedAppItem app)
    {
        if (_isSwitchingAllowedApp) return;
        _isSwitchingAllowedApp = true;
        _unauthorizedBreachCount = 0;

        try
        {
            string processName = app.ExecutableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? app.ExecutableName[..^4]
                : app.ExecutableName;

            var runningProcesses = Process.GetProcessesByName(processName);
            IntPtr targetHwnd = IntPtr.Zero;

            foreach (var proc in runningProcesses)
            {
                try
                {
                    if (proc.MainWindowHandle != IntPtr.Zero && IsWindow(proc.MainWindowHandle))
                    {
                        targetHwnd = proc.MainWindowHandle;
                        break;
                    }
                }
                catch { }
            }

            if (targetHwnd != IntPtr.Zero)
            {
                _lastAllowedAppHwnd = targetHwnd;
                SetWindowPos(targetHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                ShowWindow(targetHwnd, SW_RESTORE);
                SetForegroundWindow(targetHwnd);
                ShowMiniHud();
                return;
            }

            // Not running or has no main window yet. Resolve full executable path!
            string? resolvedPath = AppPathResolver.ResolveExecutablePath(app.ExecutableName);
            string launchTarget = !string.IsNullOrWhiteSpace(resolvedPath) ? resolvedPath : app.ExecutableName;

            Process? launchedProc = null;
            try
            {
                launchedProc = Process.Start(new ProcessStartInfo(launchTarget)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ZenShield] App launch error for '{launchTarget}': {ex.Message}");
                return;
            }

            // Asynchronous top-level window discovery (up to 5.0 seconds)
            IntPtr discoveredHwnd = IntPtr.Zero;
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(100);

                // 1. Check launched process if handle is valid
                if (launchedProc != null)
                {
                    try
                    {
                        if (launchedProc.MainWindowHandle != IntPtr.Zero && IsWindow(launchedProc.MainWindowHandle))
                        {
                            discoveredHwnd = launchedProc.MainWindowHandle;
                            break;
                        }
                    }
                    catch { }
                }

                // 2. Check running processes by name (handles Electron / launcher worker processes)
                try
                {
                    var procs = Process.GetProcessesByName(processName);
                    foreach (var p in procs)
                    {
                        using (p)
                        {
                            if (p.MainWindowHandle != IntPtr.Zero && IsWindow(p.MainWindowHandle) && IsWindowVisible(p.MainWindowHandle))
                            {
                                discoveredHwnd = p.MainWindowHandle;
                                break;
                            }
                        }
                    }
                    if (discoveredHwnd != IntPtr.Zero) break;
                }
                catch { }
            }

            if (discoveredHwnd != IntPtr.Zero)
            {
                _lastAllowedAppHwnd = discoveredHwnd;
                SetWindowPos(discoveredHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                ShowWindow(discoveredHwnd, SW_RESTORE);
                SetForegroundWindow(discoveredHwnd);
                ShowMiniHud();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZenShield] App switch error: {ex.Message}");
        }
        finally
        {
            _isSwitchingAllowedApp = false;
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
        _miniHudWindow.PositionAtTopCenter();
        _miniHudWindow.Activate();

        // Keep ZenShieldWindow and secondary backdrops topmost
        Topmost = true;
        foreach (var backdrop in _secondaryBackdrops)
        {
            backdrop.Topmost = true;
        }

        // Keep the allowed application window floating above ZenShieldWindow
        if (_lastAllowedAppHwnd != IntPtr.Zero && IsWindow(_lastAllowedAppHwnd))
        {
            SetWindowPos(_lastAllowedAppHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(_lastAllowedAppHwnd);
        }
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

            _quickNotesOverlay.Closed += (s, e) => _quickNotesOverlay = null;

        }



        _quickNotesOverlay.LoadNotes(NotesText);

        _quickNotesOverlay.Show();

        _quickNotesOverlay.Activate();

    }



    public void RestoreZenScreen()
    {
        _isAllowedAppMode = false;
        _unauthorizedBreachCount = 0;

        if (_miniHudWindow != null)
        {
            _quickNotesOverlay?.Hide();
            _miniHudWindow.Hide();
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

            string? procName = null;
            try
            {
                using var fgProc = Process.GetProcessById((int)fgPid);
                procName = fgProc.ProcessName;
            }
            catch { }

            if (!string.IsNullOrEmpty(procName))
            {
                if (string.Equals(procName, "Sankalpa", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(procName, "Sankalpa.exe", StringComparison.OrdinalIgnoreCase))
                {
                    _unauthorizedBreachCount = 0;
                    return;
                }

                bool isClippingProc = string.Equals(procName, "SnippingTool", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(procName, "ScreenClippingHost", StringComparison.OrdinalIgnoreCase);

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

                if (_isAllowedAppMode &&
                    (_allowedAppProcessNames.Contains(procName) || _allowedAppProcessNames.Contains(procName + ".exe")))
                {
                    _lastAllowedAppHwnd = fgHwnd;
                    _unauthorizedBreachCount = 0;
                    return;
                }

                // Whitelist OS text and frame host helpers during allowed app mode so they aren't minimized
                bool isOsInputHelper = string.Equals(procName, "TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(procName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(procName, "dwm", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(procName, "ctfmon", StringComparison.OrdinalIgnoreCase);

                if (_isAllowedAppMode && isOsInputHelper)
                {
                    _unauthorizedBreachCount = 0;
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
            if (IsAllowedAppValid() && _unauthorizedBreachCount < 3)
            {
                SetWindowPos(_lastAllowedAppHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                ShowWindow(_lastAllowedAppHwnd, SW_RESTORE);
                SetForegroundWindow(_lastAllowedAppHwnd);
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

        if (_lastAllowedAppHwnd != IntPtr.Zero && IsWindow(_lastAllowedAppHwnd))
        {
            try
            {
                GetWindowThreadProcessId(_lastAllowedAppHwnd, out uint pid);
                if (pid != 0)
                {
                    using var proc = Process.GetProcessById((int)pid);
                    if (!proc.HasExited)
                    {
                        return true;
                    }
                }
            }
            catch { }
        }

        // Fallback: check if any allowed app has an active top-level window
        foreach (var allowedName in _allowedAppProcessNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(allowedName);
                foreach (var p in procs)
                {
                    using (p)
                    {
                        if (p.MainWindowHandle != IntPtr.Zero && IsWindow(p.MainWindowHandle))
                        {
                            _lastAllowedAppHwnd = p.MainWindowHandle;
                            return true;
                        }
                    }
                }
            }
            catch { }
        }

        return false;
    }

    private void EnforceFocus()
    {
        if (IsAllowedAppValid() && _unauthorizedBreachCount < 3)
        {
            SetWindowPos(_lastAllowedAppHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            ShowWindow(_lastAllowedAppHwnd, SW_RESTORE);
            SetForegroundWindow(_lastAllowedAppHwnd);
        }
        else
        {
            _unauthorizedBreachCount = 0;
            RestoreZenScreen();
        }
    }

    private void EndSession()
    {
        _sessionTimer.Stop();
        _autoSaveTimer.Stop();
        _watchdogTimer.Stop();
        _quickNotesOverlay?.Close();
        _miniHudWindow?.Close();
        SetTaskbarVisibility(true);
        OnSessionEnded?.Invoke();
        Close();
    }

    #endregion



    protected override void OnClosed(EventArgs e)

    {

        base.OnClosed(e);

        _sessionTimer.Stop();
        _autoSaveTimer.Stop();
        _watchdogTimer.Stop();

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

