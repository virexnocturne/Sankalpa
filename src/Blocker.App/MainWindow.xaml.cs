using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Blocker.Core;
using Application = System.Windows.Application;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using DispatcherPriority = System.Windows.Threading.DispatcherPriority;

namespace Blocker.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow() : this(new MainViewModel(), false)
    {
    }

    public MainWindow(MainViewModel customViewModel, bool disableServiceSync = false)
    {
        InitializeComponent();
        WindowHelper.EnableDarkMode(this);

        _viewModel = customViewModel;
        DataContext = _viewModel;

        _viewModel.RequestOpenExtensionDialog = () =>
        {
            var dlg = new ExtensionDialog(_viewModel.CurrentStatusData);
            if (IsLoaded && IsVisible)
            {
                try { dlg.Owner = this; } catch { }
            }
            dlg.ShowDialog();
        };

        _viewModel.RequestOpenAppearanceDialog = () =>
        {
            _viewModel.IsSettingsViewActive = true;
        };

        _viewModel.RequestTypingChallenge = (challengeText) =>
        {
            var dlg = new TypingChallengeDialog(challengeText, "DNS Protection is locked. Type the 64-character text below to unlock.");
            if (IsLoaded && IsVisible)
            {
                try { dlg.Owner = this; } catch { }
            }
            bool? ok = dlg.ShowDialog();
            return ok == true ? dlg.ResultText : null;
        };

        PreviewKeyDown += (s, e) =>
        {
            // Esc: Close settings, composer, or notes drawer
            if (e.Key == Key.Escape)
            {
                if (_viewModel.IsSettingsViewActive)
                {
                    _viewModel.IsSettingsViewActive = false;
                    e.Handled = true;
                    return;
                }
                if (_viewModel.Meditation != null)
                {
                    if (_viewModel.Meditation.IsComposerOpen)
                    {
                        _viewModel.Meditation.CancelNoteComposerCommand?.Execute(null);
                        e.Handled = true;
                        return;
                    }
                    if (_viewModel.Meditation.IsNotesDrawerOpen)
                    {
                        _viewModel.Meditation.IsNotesDrawerOpen = false;
                        e.Handled = true;
                        return;
                    }
                }
            }

            // Ctrl + Shift + M: Start/Stop Meditation
            if (e.Key == Key.M && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift) && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
            {
                if (_viewModel.Meditation != null)
                {
                    if (_viewModel.Meditation.IsActive)
                    {
                        if (_viewModel.Meditation.CanStop)
                        {
                            _viewModel.Meditation.StopMeditation();
                        }
                    }
                    else
                    {
                        _viewModel.Meditation.StartMeditation();
                    }
                    e.Handled = true;
                    return;
                }
            }

            // Ctrl + Alt + M: Open Meditation Configuration Dialog
            if (e.Key == Key.M && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt) && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                if (_viewModel.Meditation != null)
                {
                    _viewModel.Meditation.OpenEditDialogCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }
            }

            // Ctrl + Shift + U: Toggle Soundscape Mute/Unmute
            if (e.Key == Key.U && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (_viewModel.Meditation != null)
                {
                    _viewModel.Meditation.ToggleSoundscapeCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }
            }

            // Ctrl + N: New Note (Opens drawer and focuses composer)
            if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Alt)) == 0)
            {
                if (_viewModel.Meditation != null)
                {
                    _viewModel.IsSettingsViewActive = false;
                    _viewModel.IsMeditationViewActive = true;
                    _viewModel.Meditation.IsNotesDrawerOpen = true;
                    _viewModel.Meditation.NewNoteCommand?.Execute(null);
                    NotesDrawer?.FocusComposer();
                    e.Handled = true;
                    return;
                }
            }

            // Ctrl + Shift + N: Toggle Notes Drawer
            if (e.Key == Key.N && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift) && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
            {
                if (_viewModel.Meditation != null)
                {
                    if (!_viewModel.Meditation.IsNotesDrawerOpen)
                    {
                        _viewModel.IsSettingsViewActive = false;
                        _viewModel.IsMeditationViewActive = true;
                    }
                    _viewModel.Meditation.ToggleNotesDrawerCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }
            }

            // Ctrl + Shift + S or PrintScreen: Trigger Snip
            if (((e.Key == Key.S && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift)) || e.Key == Key.Snapshot) && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
            {
                if (true)
                {
                    ZenScreenSnipper.StartSnip(this, (filePath, markdownTag) =>
                    {
                        NotesDrawer?.InsertAtCursor($"\n{markdownTag}\n");
                    });
                    e.Handled = true;
                    return;
                }
            }
        };

        ThemeManager.ThemeChanged += () =>
        {
            WindowHelper.UpdateTitleBarTheme(this, ThemeManager.IsDark);
        };

        if (!disableServiceSync)
        {
            _viewModel.RequestOpenEditorDialog = p => _ = OpenEditorDialog(p, 0);
            _viewModel.RequestOpenEditorDialogWithTab = (p, tab) => _ = OpenEditorDialog(p, tab);
            _viewModel.RequestOpenStartSessionDialog = p => _ = OpenStartSessionDialog(p);
            _viewModel.OnAppBlockedWithKind = (appName, profileName, kind) =>
            {
                if (Application.Current is App app)
                {
                    app.Dispatcher.Invoke(() => app.ShowBlockNotification(appName, profileName, kind));
                }
            };

            Loaded += MainWindow_Loaded;
            if (NotesDrawer != null)
            {
                NotesDrawer.OnSnipRequested = () =>
                {
                    ZenScreenSnipper.StartSnip(this, (filePath, markdownTag) =>
                    {
                        NotesDrawer?.InsertAtCursor($"\n{markdownTag}\n");
                    });
                };
            }
            Activated += (s, e) => _viewModel?.CheckWindowsNotificationStatus();
            Closing += MainWindow_Closing;
            Closed += MainWindow_Closed;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int HOTKEY_ID = 0x9001;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_N = 0x4E;
    private const int WM_HOTKEY = 0x0312;

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _viewModel.StartPolling();

        // Load Persistent Notes Vault into Meditation NotesCollection on startup
        try
        {
            if (_viewModel.Meditation != null && _viewModel.Meditation.NotesCollection.Count == 0)
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string vaultPath = System.IO.Path.Combine(localAppData, "Sankalpa", "notes", "active_notes.json");
                if (System.IO.File.Exists(vaultPath))
                {
                    string json = System.IO.File.ReadAllText(vaultPath);
                    var cards = NoteCardCollection.Deserialize(json);
                    foreach (var card in cards)
                    {
                        _viewModel.Meditation.NotesCollection.Add(card);
                    }
                }
            }
        }
        catch { }

        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                RegisterHotKey(hwnd, HOTKEY_ID, MOD_ALT | MOD_NOREPEAT, VK_N);
                HwndSource.FromHwnd(hwnd)?.AddHook(HwndHook);
            }
        }
        catch
        {
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            _viewModel.Meditation?.ToggleQuickNotes();
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            App.TrimWorkingSet();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.OnWindowClosing(e);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        try
        {
            if (_viewModel.Meditation != null && _viewModel.Meditation.NotesCollection.Count > 0)
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string vaultDir = System.IO.Path.Combine(localAppData, "Sankalpa", "notes");
                if (!System.IO.Directory.Exists(vaultDir)) System.IO.Directory.CreateDirectory(vaultDir);
                string vaultPath = System.IO.Path.Combine(vaultDir, "active_notes.json");
                string json = NoteCardCollection.Serialize(_viewModel.Meditation.NotesCollection.ToList());
                System.IO.File.WriteAllText(vaultPath, json);
            }
        }
        catch { }

        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(hwnd, HOTKEY_ID);
            }
        }
        catch
        {
        }
        _viewModel.StopPolling();
        _viewModel.Meditation?.Dispose();
    }

    private void ProfileName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement fe && fe.DataContext is ProfileViewModel profile)
        {
            profile.BeginEditName();
            e.Handled = true;
        }
    }

    private void InlineNameTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && (bool)e.NewValue)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                tb.Focus();
                tb.SelectAll();
            }), DispatcherPriority.Input);
        }
    }

    private async void InlineNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ProfileViewModel profile)
        {
            if (e.Key == Key.Escape)
            {
                profile.CancelEditName();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var binding = tb.GetBindingExpression(TextBox.TextProperty);
                binding?.UpdateSource();
                await _viewModel.CommitRenameProfileAsync(profile, profile.EditingName);
            }
        }
    }

    private async void InlineNameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ProfileViewModel profile && profile.IsEditingName)
        {
            var binding = tb.GetBindingExpression(TextBox.TextProperty);
            binding?.UpdateSource();
            await _viewModel.CommitRenameProfileAsync(profile, profile.EditingName);
        }
    }

    private async void ToggleSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is ProfileViewModel profile)
        {
            // Keep checkbox visual state in sync without clobbering WPF OneWay binding expression
            cb.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, (bool?)profile.IsToggleChecked);

            if (profile.IsLockedFromToggling)
            {
                return;
            }

            try
            {
                await _viewModel.ToggleBlockAsync(profile, this);
            }
            catch (Exception ex)
            {
                _viewModel.ErrorMessage = $"Error toggling block '{profile.Name}': {ex.Message}";
            }
            finally
            {
                cb.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, (bool?)profile.IsToggleChecked);
                cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                    new System.Windows.Data.Binding(nameof(ProfileViewModel.IsToggleChecked)) { Mode = System.Windows.Data.BindingMode.OneWay });
            }
        }
    }

    private async void DnsToggleSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb)
        {
            // Keep checkbox visual state in sync without clobbering WPF OneWay binding expression
            cb.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, (bool?)_viewModel.IsDnsEnabled);
            try
            {
                await _viewModel.ToggleDnsProtectionAsync();
            }
            catch (Exception ex)
            {
                _viewModel.ErrorMessage = $"Error toggling DNS protection: {ex.Message}";
            }
            finally
            {
                cb.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, (bool?)_viewModel.IsDnsEnabled);
                cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                    new System.Windows.Data.Binding(nameof(MainViewModel.IsDnsEnabled)) { Mode = System.Windows.Data.BindingMode.OneWay });
            }
        }
    }

    private async Task OpenEditorDialog(ProfileViewModel? profile, int initialTab = 0)
    {
        try
        {
            bool isSessionLocked = _viewModel.IsSessionActive && _viewModel.ActiveSessionLocked &&
                profile != null && profile.Name.Equals(_viewModel.ActiveSessionProfile, StringComparison.OrdinalIgnoreCase);
            bool isProfileLocked = (profile != null && profile.LockedNow);

            var existingNames = _viewModel.Profiles.Select(p => p.Name).ToList();
            var dlg = new BlockEditorDialog(profile, isSessionLocked, isProfileLocked, existingNames, _viewModel.IsServiceElevated, initialTab);
            if (IsLoaded && IsVisible)
            {
                try { dlg.Owner = this; } catch { }
            }

            if (dlg.ShowDialog() == true)
            {
                if (dlg.Deleted && profile != null)
                {
                    await _viewModel.DeleteProfileAsync(profile.Name);
                }
                else if (dlg.Saved)
                {
                    if (profile != null && dlg.IsLockedMode && !string.Equals(profile.Name, dlg.ResultName, StringComparison.Ordinal))
                    {
                        await _viewModel.RenameProfileAsync(profile.Id, dlg.ResultName);
                    }

                    await _viewModel.SaveProfileAsync(
                        dlg.ResultName,
                        dlg.ResultApps,
                        dlg.ResultSites,
                        dlg.ResultSchedule,
                        dlg.ResultLockMode,
                        dlg.ResultPassword,
                        dlg.ResultCurrentPassword,
                        profile?.Name,
                        webRules: dlg.ResultWebRules,
                        blockAllWebsites: dlg.ResultBlockAllWebsites,
                        systemWide: dlg.ResultSystemWide,
                        allowance: dlg.ResultAllowance,
                        additiveOnly: dlg.IsLockedMode,
                        delayedUnlockMinutes: dlg.ResultDelayedUnlockMinutes);

                    if (dlg.StartTimedLockMinutes > 0 && string.IsNullOrEmpty(_viewModel.ErrorMessage))
                    {
                        await _viewModel.StartSessionAsync(dlg.ResultName, dlg.StartTimedLockMinutes, locked: true);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = $"Error in block editor: {ex.Message}";
        }
    }

    private async Task OpenStartSessionDialog(ProfileViewModel profile)
    {
        try
        {
            string profileKey = profile.Id > 0 ? profile.Id.ToString() : profile.Name;
            var dlg = new StartSessionDialog(profileKey, profile.Name);
            if (IsLoaded && IsVisible)
            {
                try { dlg.Owner = this; } catch { }
            }

            if (dlg.ShowDialog() == true)
            {
                await _viewModel.StartSessionAsync(profile.Name, dlg.DurationMinutes, dlg.IsLocked);
            }
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = $"Error starting session: {ex.Message}";
        }
    }

    public void OpenSettings()
    {
        _viewModel.IsSettingsViewActive = true;
    }

    public void OpenExtensionDialog()
    {
        _viewModel.RequestOpenExtensionDialog?.Invoke();
    }
}
