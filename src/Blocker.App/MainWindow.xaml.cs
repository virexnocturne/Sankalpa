using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

        KeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && _viewModel.IsSettingsViewActive)
            {
                _viewModel.IsSettingsViewActive = false;
                e.Handled = true;
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
            Closing += MainWindow_Closing;
            Closed += MainWindow_Closed;
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _viewModel.StartPolling();
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
        _viewModel.StopPolling();
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
            if (profile.IsLockedFromToggling)
            {
                cb.IsChecked = profile.IsToggleChecked;
                return;
            }
            // Keep checkbox visual state in sync with actual profile state
            cb.IsChecked = profile.IsToggleChecked;
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
                cb.IsChecked = profile.IsToggleChecked;
            }
        }
    }

    private async void DnsToggleSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb)
        {
            // Keep checkbox visual state in sync with actual DNS state
            cb.IsChecked = _viewModel.IsDnsEnabled;
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
                cb.IsChecked = _viewModel.IsDnsEnabled;
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
