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

namespace Blocker.App;

public partial class MeditationEditDialog : Window
{
    private readonly List<string> _allowedApps = new();
    private bool _suppressPresetSync;

    public int DurationMinutes { get; set; } = 20;
    public bool CompleteLockout => RbCompleteLockout.IsChecked == true;
    public List<string> AllowedApps => new(_allowedApps);
    public LockMode LockProtection { get; set; } = LockMode.Locked;
    public bool ScheduleEnabled => ScheduleToggle.IsChecked == true;
    public string ScheduleTime => ScheduleTimeInput.Text.Trim();
    public List<DayOfWeek> ScheduleDays { get; set; } = new();
    public SoundscapeType SelectedSoundscape { get; set; } = SoundscapeType.Rain;

    public MeditationEditDialog(
        int durationMinutes = 20,
        LockMode lockMode = LockMode.Locked,
        IEnumerable<string>? allowedApps = null,
        bool scheduleEnabled = false,
        string scheduleTime = "15:00",
        IEnumerable<DayOfWeek>? scheduleDays = null,
        SoundscapeType soundscape = SoundscapeType.Rain)
    {
        InitializeComponent();
        WindowHelper.EnableDarkMode(this);

        DurationMinutes = durationMinutes > 0 ? durationMinutes : 20;
        DurationInput.Text = DurationMinutes.ToString();
        SyncPresetRadio(DurationMinutes);

        LockProtection = lockMode;
        if (lockMode == LockMode.TypingChallenge)
        {
            RbLockChallenge.IsChecked = true;
        }
        else if (lockMode == LockMode.Password)
        {
            RbLockPassword.IsChecked = true;
        }
        else
        {
            RbLockStrict.IsChecked = true;
        }

        if (allowedApps != null && allowedApps.Any())
        {
            _allowedApps.AddRange(allowedApps);
            RbAllowApps.IsChecked = true;
            AllowedAppsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            RbCompleteLockout.IsChecked = true;
            AllowedAppsPanel.Visibility = Visibility.Collapsed;
        }
        RebuildAllowedAppsUi();

        ScheduleToggle.IsChecked = scheduleEnabled;
        ScheduleOptionsPanel.Visibility = scheduleEnabled ? Visibility.Visible : Visibility.Collapsed;
        ScheduleTimeInput.Text = string.IsNullOrWhiteSpace(scheduleTime) ? "15:00" : scheduleTime;

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

    private void SyncPresetRadio(int minutes)
    {
        _suppressPresetSync = true;
        try
        {
            Preset5.IsChecked = (minutes == 5);
            Preset10.IsChecked = (minutes == 10);
            Preset20.IsChecked = (minutes == 20);
            Preset30.IsChecked = (minutes == 30);
            Preset45.IsChecked = (minutes == 45);
            Preset60.IsChecked = (minutes == 60);
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
            DurationInput.Text = mins.ToString();
        }
    }

    private void DurationInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(DurationInput.Text.Trim(), out int mins) && mins > 0 && mins <= 1440)
        {
            DurationMinutes = mins;
            SyncPresetRadio(mins);
        }
    }

    private void DecrementMinutes_Click(object sender, RoutedEventArgs e)
    {
        if (DurationMinutes > 1)
        {
            DurationMinutes--;
            DurationInput.Text = DurationMinutes.ToString();
        }
    }

    private void IncrementMinutes_Click(object sender, RoutedEventArgs e)
    {
        if (DurationMinutes < 1440)
        {
            DurationMinutes++;
            DurationInput.Text = DurationMinutes.ToString();
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
                FontSize = 12,
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
                FontSize = 12,
                Foreground = (Brush)FindResource("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });

            var removeBtn = new Button
            {
                Content = "×",
                Style = (Style)FindResource("SummaryBadgeButtonStyle"),
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

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(DurationInput.Text.Trim(), out int mins) && mins > 0)
        {
            DurationMinutes = mins;
        }

        if (RbLockChallenge.IsChecked == true)
        {
            LockProtection = LockMode.TypingChallenge;
        }
        else if (RbLockPassword.IsChecked == true)
        {
            LockProtection = LockMode.Password;
        }
        else
        {
            LockProtection = LockMode.Locked;
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

        DialogResult = true;
        Close();
    }
}
