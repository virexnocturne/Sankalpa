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

    [DllImport("user32.dll")]

    [return: MarshalAs(UnmanagedType.Bool)]

    private static extern bool SetForegroundWindow(IntPtr hWnd);



    [DllImport("user32.dll")]

    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    #endregion



    private readonly DispatcherTimer _sessionTimer;

    private readonly DispatcherTimer _autoSaveTimer;

    private readonly List<ZenBackdropWindow> _secondaryBackdrops = new();

    private ZenMiniHudWindow? _miniHudWindow;

    private LowLevelKeyboardHook? _keyboardHook;



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



        UpdateTimerDisplay();

        UpdateLockStatusUi();

        UpdateAudioUi();

        SetWhitelistedApps(whitelistedApps);



        Loaded += ZenShieldWindow_Loaded;

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



        // 2. Start countdown timer

        _sessionTimer.Start();



        // 3. Focus reflections notes



        // 4. If CompleteLock is active, start low-level keyboard hook to block Alt+Tab / WinKey

        if (_completeLock)

        {

            try

            {

                _keyboardHook = new LowLevelKeyboardHook();

                _keyboardHook.Start();

            }

            catch (Exception ex)

            {

                Debug.WriteLine($"[ZenShield] Keyboard hook failed to start: {ex.Message}");

            }

        }

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

        if (apps != null)

        {

            foreach (var app in apps)

            {

                if (!string.IsNullOrWhiteSpace(app))

                {

                    list.Add(new WhitelistedAppItem(app.Trim()));

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



    public void SwitchToAllowedApp(WhitelistedAppItem app)

    {

        // 1. Try to activate existing process window or launch executable

        try

        {

            string processName = app.ExecutableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)

                ? app.ExecutableName[..^4]

                : app.ExecutableName;



            var runningProcesses = Process.GetProcessesByName(processName);

            IntPtr targetHwnd = IntPtr.Zero;

            foreach (var proc in runningProcesses)

            {

                if (proc.MainWindowHandle != IntPtr.Zero)

                {

                    targetHwnd = proc.MainWindowHandle;

                    break;

                }

            }



            if (targetHwnd != IntPtr.Zero)

            {

                ShowWindow(targetHwnd, SW_RESTORE);

                SetForegroundWindow(targetHwnd);

            }

            else

            {

                try

                {

                    Process.Start(new ProcessStartInfo(app.ExecutableName)

                    {

                        UseShellExecute = true

                    });

                }

                catch

                {

                    // Fallback if full path is not directly executable by simple name

                }

            }

        }

        catch (Exception ex)

        {

            Debug.WriteLine($"[ZenShield] App switch error: {ex.Message}");

        }



        // 2. Hide Zen Shield and show floating topmost Mini-HUD

        ShowMiniHud();

    }



    private void ShowMiniHud()

    {

        if (_miniHudWindow == null)

        {

            _miniHudWindow = new ZenMiniHudWindow

            {

                OnReturnToZenRequested = RestoreZenScreen,

                OnToggleAudioRequested = ToggleAudioPlayback,

                OnToggleNotesRequested = ToggleQuickNotes

            };

        }



        _miniHudWindow.UpdateTimer(FormatAdaptiveTime(_remainingSeconds));

        _miniHudWindow.UpdateAudioLabel(GetAudioHudLabel());

        _miniHudWindow.Show();

        _miniHudWindow.Activate();



        // Hide Zen kiosk on primary and secondary screens

        Hide();

        foreach (var backdrop in _secondaryBackdrops)

        {

            backdrop.Hide();

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

        if (_miniHudWindow != null)

        {

            _quickNotesOverlay?.Hide();

            _miniHudWindow.Hide();

        }



        Show();

        WindowState = WindowState.Maximized;

        Topmost = true;

        Activate();



        foreach (var backdrop in _secondaryBackdrops)

        {

            backdrop.Show();

            backdrop.Topmost = true;

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

        else if (string.Equals(_lockType, "Challenge", StringComparison.OrdinalIgnoreCase))

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

                Owner = this

            };



            if (pwdDlg.ShowDialog() == true)

            {

                EndSession();

            }

            return;

        }



        if (string.Equals(_lockType, "Challenge", StringComparison.OrdinalIgnoreCase))

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



    private void EndSession()

    {

        _sessionTimer.Stop();

        _autoSaveTimer.Stop();

        _quickNotesOverlay?.Close();

        _miniHudWindow?.Close();

        OnSessionEnded?.Invoke();

        Close();

    }

    #endregion



    protected override void OnClosed(EventArgs e)

    {

        base.OnClosed(e);



        _sessionTimer.Stop();

        _autoSaveTimer.Stop();



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

