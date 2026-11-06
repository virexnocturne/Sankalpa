using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Blocker.Core;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using Orientation = System.Windows.Controls.Orientation;
using Brush = System.Windows.Media.Brush;
using Application = System.Windows.Application;

namespace Blocker.App;

public partial class MeditationEditDialog : Window
{
    private static readonly (string Name, string Exe)[] QuickAppCandidates = new[]
    {
        ("Spotify", "spotify.exe"),
        ("Notepad", "notepad.exe"),
        ("Obsidian", "Obsidian.exe"),
        ("VLC", "vlc.exe"),
        ("VS Code", "Code.exe"),
        ("Chrome", "chrome.exe")
    };

    private readonly List<string> _allowedApps = new();
    private readonly HashSet<string> _blockedApps = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressPresetSync;

    public int DurationMinutes { get; set; } = 20;
    public bool CompleteLockout => RbCompleteLockout.IsChecked == true;
    public List<string> AllowedApps => new(_allowedApps);
    public LockMode LockProtection { get; set; } = LockMode.Locked;
    public string? PasswordHash { get; set; }
    public bool ScheduleEnabled => ScheduleToggle.IsChecked == true;
    public string ScheduleTime => ScheduleTimeInput.Text.Trim();
    public string ScheduleEndTime => ScheduleEndTimeInput.Text.Trim();
    public int ScheduleDurationMinutes { get; set; } = 60;
    public List<DayOfWeek> ScheduleDays { get; set; } = new();
    public SoundscapeType SelectedSoundscape { get; set; } = SoundscapeType.Rain;

    public MeditationEditDialog(
        int durationMinutes = 20,
        LockMode lockMode = LockMode.Locked,
        IEnumerable<string>? allowedApps = null,
        bool scheduleEnabled = false,
        string scheduleTime = "15:00",
        IEnumerable<DayOfWeek>? scheduleDays = null,
        SoundscapeType soundscape = SoundscapeType.Rain,
        string? scheduleEndTime = null,
        string? passwordHash = null,
        IEnumerable<string>? blockedApps = null)
    {
        InitializeComponent();
        WindowHelper.EnableDarkMode(this);

        PasswordHash = passwordHash;

        if (blockedApps != null)
        {
            foreach (var b in blockedApps)
            {
                if (!string.IsNullOrWhiteSpace(b)) _blockedApps.Add(b.Trim());
            }
        }
        else if (Application.Current?.MainWindow?.DataContext is MainViewModel mainVm)
        {
            foreach (var b in mainVm.GetActiveBlockedProcesses())
            {
                if (!string.IsNullOrWhiteSpace(b)) _blockedApps.Add(b.Trim());
            }
        }

        DurationMinutes = durationMinutes > 0 ? durationMinutes : 20;
        int hours = DurationMinutes / 60;
        int mins = DurationMinutes % 60;
        HoursInput.Text = hours.ToString();
        MinutesInput.Text = mins.ToString();
        SyncPresetRadio(DurationMinutes);

        LockProtection = lockMode;
        if (lockMode == LockMode.TypingChallenge)
        {
            RbLockChallenge.IsChecked = true;
            if (PasswordProtectionPanel != null) PasswordProtectionPanel.Visibility = Visibility.Collapsed;
        }
        else if (lockMode == LockMode.Password)
        {
            RbLockPassword.IsChecked = true;
            if (PasswordProtectionPanel != null) PasswordProtectionPanel.Visibility = Visibility.Visible;
        }
        else
        {
            RbLockStrict.IsChecked = true;
            if (PasswordProtectionPanel != null) PasswordProtectionPanel.Visibility = Visibility.Collapsed;
        }

        if (allowedApps != null && allowedApps.Any())
        {
            foreach (var app in allowedApps)
            {
                if (!IsAppBlocked(app) && !_allowedApps.Contains(app, StringComparer.OrdinalIgnoreCase))
                {
                    _allowedApps.Add(app);
                }
            }
            if (_allowedApps.Count > 0)
            {
                RbAllowApps.IsChecked = true;
                AllowedAppsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                RbCompleteLockout.IsChecked = true;
                AllowedAppsPanel.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            RbCompleteLockout.IsChecked = true;
            AllowedAppsPanel.Visibility = Visibility.Collapsed;
        }
        RebuildAllowedAppsUi();
        PopulateQuickApps();

        ScheduleToggle.IsChecked = scheduleEnabled;
        ScheduleOptionsPanel.Visibility = scheduleEnabled ? Visibility.Visible : Visibility.Collapsed;
        ScheduleTimeInput.Text = string.IsNullOrWhiteSpace(scheduleTime) ? "15:00" : scheduleTime;
        ScheduleEndTimeInput.Text = string.IsNullOrWhiteSpace(scheduleEndTime)
            ? MeditationSchedule.CalculateEndTime(ScheduleTimeInput.Text, DurationMinutes)
            : scheduleEndTime;
        UpdateScheduleDurationPreview();

        var days = scheduleDays?.ToList() ?? new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        DayMon.IsChecked = days.Contains(DayOfWeek.Monday);
        DayTue.IsChecked = days.Contains(DayOfWeek.Tuesday);
        DayWed.IsChecked = days.Contains(DayOfWeek.Wednesday);
        DayThu.IsChecked = days.Contains(DayOfWeek.Thursday);
        DayFri.IsChecked = days.Contains(DayOfWeek.Friday);
        DaySat.IsChecked = days.Contains(DayOfWeek.Saturday);
        DaySun.IsChecked = days.Contains(DayOfWeek.Sunday);

        SelectedSoundscape = soundscape;
        switch (soundscape)
        {
            case SoundscapeType.Library:
                SoundLibrary.IsChecked = true;
                break;
            case SoundscapeType.Forest:
                SoundForest.IsChecked = true;
                break;
            case SoundscapeType.SingingBowls:
                SoundBowls.IsChecked = true;
                break;
            case SoundscapeType.None:
                SoundMute.IsChecked = true;
                break;
            default:
                SoundRain.IsChecked = true;
                break;
        }
    }

    public bool IsAppBlocked(string app)
    {
        if (string.IsNullOrWhiteSpace(app) || _blockedApps.Count == 0) return false;
        string clean = app.Trim();
        if (_blockedApps.Contains(clean)) return true;
        string withExe = clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? clean : clean + ".exe";
        if (_blockedApps.Contains(withExe)) return true;
        string withoutExe = clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? clean[..^4] : clean;
        if (_blockedApps.Contains(withoutExe)) return true;
        return false;
    }

    private void LockProtectionMode_Changed(object sender, RoutedEventArgs e)
    {
        if (PasswordProtectionPanel == null) return;
        PasswordProtectionPanel.Visibility = (RbLockPassword.IsChecked == true)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (PasswordErrorTextBlock != null)
        {
            PasswordErrorTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    private void PopulateQuickApps()
    {
        QuickAppsWrapPanel.Children.Clear();
        int count = 0;
        foreach (var (name, exe) in QuickAppCandidates)
        {
            if (InstalledAppScanner.IsAppInstalled(exe) && !IsAppBlocked(exe))
            {
                var btn = new Button
                {
                    Content = name,
                    Style = (Style)FindResource("SecondaryButton"),
                    FontSize = (double)FindResource("FontSize12"),
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 4, 4),
                    Tag = exe
                };
                btn.Click += QuickAddApp_Click;
                QuickAppsWrapPanel.Children.Add(btn);
                count++;
            }
        }

        if (count == 0)
        {
            QuickAppsLabel.Visibility = Visibility.Collapsed;
            QuickAppsWrapPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            QuickAppsLabel.Visibility = Visibility.Visible;
            QuickAppsWrapPanel.Visibility = Visibility.Visible;
        }
    }

    private void SyncPresetRadio(int minutes)
    {
        _suppressPresetSync = true;
        try
        {
            if (Preset15 != null) Preset15.IsChecked = (minutes == 15);
            if (Preset30 != null) Preset30.IsChecked = (minutes == 30);
            if (Preset45 != null) Preset45.IsChecked = (minutes == 45);
            if (Preset1h != null) Preset1h.IsChecked = (minutes == 60);
            if (Preset2h != null) Preset2h.IsChecked = (minutes == 120);
            if (Preset3h != null) Preset3h.IsChecked = (minutes == 180);
        }
        finally
        {
            _suppressPresetSync = false;
        }
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressPresetSync) return;
        if (sender is RadioButton rb && rb.Tag is string tagStr && int.TryParse(tagStr, out int mins))
        {
            DurationMinutes = mins;
            _suppressPresetSync = true;
            try
            {
                if (HoursInput != null) HoursInput.Text = (mins / 60).ToString();
                if (MinutesInput != null) MinutesInput.Text = (mins % 60).ToString();
            }
            finally
            {
                _suppressPresetSync = false;
            }
        }
    }

    private void DurationInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressPresetSync) return;
        int.TryParse(HoursInput?.Text?.Trim() ?? "0", out int h);
        int.TryParse(MinutesInput?.Text?.Trim() ?? "0", out int m);
        int total = (h * 60) + m;
        if (total > 0 && total <= 1440)
        {
            DurationMinutes = total;
            SyncPresetRadio(total);
        }
    }

    private void DecrementHours_Click(object sender, RoutedEventArgs e)
    {
        int.TryParse(HoursInput.Text.Trim(), out int h);
        if (h > 0)
        {
            h--;
            HoursInput.Text = h.ToString();
        }
    }

    private void IncrementHours_Click(object sender, RoutedEventArgs e)
    {
        int.TryParse(HoursInput.Text.Trim(), out int h);
        if (h < 24)
        {
            h++;
            HoursInput.Text = h.ToString();
        }
    }

    private void DecrementMinutes_Click(object sender, RoutedEventArgs e)
    {
        int.TryParse(MinutesInput.Text.Trim(), out int m);
        int.TryParse(HoursInput.Text.Trim(), out int h);
        if (m > 0)
        {
            m--;
            MinutesInput.Text = m.ToString();
        }
        else if (h > 0)
        {
            h--;
            m = 59;
            _suppressPresetSync = true;
            HoursInput.Text = h.ToString();
            _suppressPresetSync = false;
            MinutesInput.Text = m.ToString();
        }
    }

    private void IncrementMinutes_Click(object sender, RoutedEventArgs e)
    {
        int.TryParse(MinutesInput.Text.Trim(), out int m);
        int.TryParse(HoursInput.Text.Trim(), out int h);
        if (m < 59)
        {
            m++;
            MinutesInput.Text = m.ToString();
        }
        else if (h < 24)
        {
            h++;
            m = 0;
            _suppressPresetSync = true;
            HoursInput.Text = h.ToString();
            _suppressPresetSync = false;
            MinutesInput.Text = m.ToString();
        }
    }

    private void AllowedAppsMode_Changed(object sender, RoutedEventArgs e)
    {
        if (AllowedAppsPanel == null) return;
        AllowedAppsPanel.Visibility = (RbAllowApps.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PickApp_Click(object sender, RoutedEventArgs e)
    {
        var appPicker = new AppPickerDialog(_allowedApps)
        {
            Owner = this
        };

        if (appPicker.ShowDialog() == true && appPicker.SelectedApps != null)
        {
            foreach (var app in appPicker.SelectedApps)
            {
                if (IsAppBlocked(app)) continue;
                if (!_allowedApps.Contains(app, StringComparer.OrdinalIgnoreCase))
                {
                    _allowedApps.Add(app);
                }
            }
            RebuildAllowedAppsUi();
        }
    }

    private void QuickAddApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string appName)
        {
            if (IsAppBlocked(appName)) return;
            if (!_allowedApps.Contains(appName, StringComparer.OrdinalIgnoreCase))
            {
                _allowedApps.Add(appName);
                RebuildAllowedAppsUi();
            }
        }
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string appName)
        {
            _allowedApps.RemoveAll(a => string.Equals(a, appName, StringComparison.OrdinalIgnoreCase));
            RebuildAllowedAppsUi();
        }
    }

    private void RebuildAllowedAppsUi()
    {
        AllowedAppsWrapPanel.Children.Clear();
        if (_allowedApps.Count == 0)
        {
            var emptyNotice = new TextBlock
            {
                Text = "No apps selected. (All apps locked out)",
                FontSize = (double)FindResource("FontSize12"),
                Foreground = (Brush)FindResource("TextDisabled"),
                Margin = new Thickness(0, 2, 0, 4)
            };
            AllowedAppsWrapPanel.Children.Add(emptyNotice);
            return;
        }

        foreach (var app in _allowedApps)
        {
            var badge = new Border
            {
                Background = (Brush)FindResource("SurfaceRaised"),
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 3, 6, 3),
                Margin = new Thickness(0, 0, 6, 6)
            };

            var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Text = app,
                FontSize = (double)FindResource("FontSize12"),
                Foreground = (Brush)FindResource("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });

            var removeBtn = new Button
            {
                Content = "×",
                Style = (TryFindResource("SummaryBadgeButtonStyle") as Style) ?? (TryFindResource("SecondaryButton") as Style),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("TextSecondary"),
                Tag = app,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            removeBtn.Click += RemoveApp_Click;
            stack.Children.Add(removeBtn);

            badge.Child = stack;
            AllowedAppsWrapPanel.Children.Add(badge);
        }
    }

    private void ScheduleToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (ScheduleOptionsPanel == null) return;
        ScheduleOptionsPanel.Visibility = (ScheduleToggle.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScheduleTime_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateScheduleDurationPreview();
    }

    private void UpdateScheduleDurationPreview()
    {
        if (ScheduleDurationLabel == null || ScheduleTimeInput == null || ScheduleEndTimeInput == null) return;

        string startStr = ScheduleTimeInput.Text.Trim();
        string endStr = ScheduleEndTimeInput.Text.Trim();

        if (MeditationSchedule.TryCalculateDurationMinutes(startStr, endStr, out int durationMins) && durationMins > 0)
        {
            int hours = durationMins / 60;
            int mins = durationMins % 60;
            string durationFormatted = hours > 0
                ? (mins > 0 ? $"{hours}h {mins:D2}m" : $"{hours}h 00m")
                : $"{mins}m";

            bool overnight = false;
            if (TimeSpan.TryParse(startStr, out var s) && TimeSpan.TryParse(endStr, out var e))
            {
                if (e < s) overnight = true;
            }

            ScheduleDurationLabel.Text = overnight
                ? $"Duration: {durationFormatted} (overnight)"
                : $"Duration: {durationFormatted}";

            if (TryFindResource("Accent") is Brush accent)
            {
                ScheduleDurationLabel.Foreground = accent;
            }
        }
        else
        {
            ScheduleDurationLabel.Text = "Duration: -- (invalid time)";
            if (TryFindResource("TextDisabled") is Brush disabled)
            {
                ScheduleDurationLabel.Foreground = disabled;
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        int.TryParse(HoursInput.Text.Trim(), out int hours);
        int.TryParse(MinutesInput.Text.Trim(), out int mins);
        int totalMins = (hours * 60) + mins;
        if (totalMins > 0)
        {
            DurationMinutes = totalMins;
        }

        if (ScheduleToggle.IsChecked == true)
        {
            string startStr = ScheduleTimeInput.Text.Trim();
            string endStr = ScheduleEndTimeInput.Text.Trim();
            if (!MeditationSchedule.TryCalculateDurationMinutes(startStr, endStr, out int schedMins) || schedMins <= 0)
            {
                System.Windows.MessageBox.Show("Please enter a valid schedule time range (24h format, e.g. 15:00 to 16:00).", "Invalid Schedule Time", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ScheduleDurationMinutes = schedMins;
            DurationMinutes = schedMins;
        }

        if (RbLockChallenge.IsChecked == true)
        {
            LockProtection = LockMode.TypingChallenge;
            PasswordHash = null;
        }
        else if (RbLockPassword.IsChecked == true)
        {
            LockProtection = LockMode.Password;
            string newPwd = NewPasswordBox.Password;
            string confirmPwd = ConfirmPasswordBox.Password;

            if (string.IsNullOrEmpty(newPwd))
            {
                if (string.IsNullOrEmpty(PasswordHash))
                {
                    if (PasswordErrorTextBlock != null)
                    {
                        PasswordErrorTextBlock.Text = "Please enter a password.";
                        PasswordErrorTextBlock.Visibility = Visibility.Visible;
                    }
                    NewPasswordBox.Focus();
                    return;
                }
            }
            else
            {
                if (newPwd != confirmPwd)
                {
                    if (PasswordErrorTextBlock != null)
                    {
                        PasswordErrorTextBlock.Text = "Passwords do not match.";
                        PasswordErrorTextBlock.Visibility = Visibility.Visible;
                    }
                    ConfirmPasswordBox.SelectAll();
                    ConfirmPasswordBox.Focus();
                    return;
                }

                PasswordHash = ComputeSha256Hash(newPwd);
            }
        }
        else
        {
            LockProtection = LockMode.Locked;
            PasswordHash = null;
        }

        ScheduleDays = new List<DayOfWeek>();
        if (DayMon.IsChecked == true) ScheduleDays.Add(DayOfWeek.Monday);
        if (DayTue.IsChecked == true) ScheduleDays.Add(DayOfWeek.Tuesday);
        if (DayWed.IsChecked == true) ScheduleDays.Add(DayOfWeek.Wednesday);
        if (DayThu.IsChecked == true) ScheduleDays.Add(DayOfWeek.Thursday);
        if (DayFri.IsChecked == true) ScheduleDays.Add(DayOfWeek.Friday);
        if (DaySat.IsChecked == true) ScheduleDays.Add(DayOfWeek.Saturday);
        if (DaySun.IsChecked == true) ScheduleDays.Add(DayOfWeek.Sunday);

        if (SoundLibrary.IsChecked == true) SelectedSoundscape = SoundscapeType.Library;
        else if (SoundForest.IsChecked == true) SelectedSoundscape = SoundscapeType.Forest;
        else if (SoundBowls.IsChecked == true) SelectedSoundscape = SoundscapeType.SingingBowls;
        else if (SoundMute.IsChecked == true) SelectedSoundscape = SoundscapeType.None;
        else SelectedSoundscape = SoundscapeType.Rain;

        // Persist settings to UiSettingsStore (Bug 10)
        try
        {
            var uiSettings = UiSettingsStore.Load();
            uiSettings.MeditationDurationMinutes = DurationMinutes;
            uiSettings.MeditationLockMode = LockProtection.ToString();
            uiSettings.MeditationSoundscape = SelectedSoundscape.ToString();
            if (LockProtection == LockMode.Password && !string.IsNullOrEmpty(PasswordHash))
            {
                uiSettings.MeditationPasswordHash = PasswordHash;
            }
            UiSettingsStore.Save(uiSettings);
        }
        catch { }

        DialogResult = true;
        Close();
    }

    public static string ComputeSha256Hash(string rawData)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
