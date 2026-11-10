using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    public string SelectedSoundscapeId { get; set; } = SoundscapeCatalog.RainId;
    public SoundscapeType SelectedSoundscape { get; set; } = SoundscapeType.Rain;
    private string? _renamingTrackId;

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
        IEnumerable<string>? blockedApps = null,
        string? soundscapeId = null)
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

        if (!string.IsNullOrWhiteSpace(soundscapeId))
        {
            SelectedSoundscapeId = SoundscapeCatalog.NormalizeId(soundscapeId);
        }
        else if (soundscape != SoundscapeType.Rain && soundscape != SoundscapeType.None)
        {
            SelectedSoundscapeId = SoundscapeCatalog.FromLegacyEnum(soundscape);
        }
        else if (soundscape == SoundscapeType.None)
        {
            SelectedSoundscapeId = SoundscapeCatalog.MuteId;
        }
        else
        {
            var uiSettings = UiSettingsStore.Load();
            SelectedSoundscapeId = SoundscapeCatalog.NormalizeId(uiSettings.MeditationSoundscape);
        }
        SelectedSoundscape = SoundscapeCatalog.ToLegacyEnum(SelectedSoundscapeId);
        PopulateSoundscapes();
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

        SelectedSoundscape = SoundscapeCatalog.ToLegacyEnum(SelectedSoundscapeId);

        // Persist settings to UiSettingsStore
        try
        {
            var uiSettings = UiSettingsStore.Load();
            uiSettings.MeditationDurationMinutes = DurationMinutes;
            uiSettings.MeditationLockMode = LockProtection.ToString();
            uiSettings.MeditationSoundscape = SelectedSoundscapeId;
            UiSettings.LastSelectedCustomSoundscapeId = SelectedSoundscapeId;
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

    private void PopulateSoundscapes()
    {
        if (SoundscapeWrapPanel == null) return;
        SoundscapeWrapPanel.Children.Clear();

        var uiSettings = UiSettingsStore.Load();
        var builtInTracks = SoundscapeCatalog.GetBuiltInTracks();

        // 1. Built-in presets (Rain, Library, Forest, Singing Bowls, Mute)
        foreach (var track in builtInTracks)
        {
            var rb = new RadioButton
            {
                Content = track.DisplayName,
                Style = (Style)FindResource("PillRadioButton"),
                GroupName = "SoundscapePresetGroup",
                Margin = new Thickness(0, 0, 6, 6),
                Tag = track.Id,
                IsChecked = string.Equals(track.Id, SelectedSoundscapeId, StringComparison.OrdinalIgnoreCase)
            };
            rb.Checked += SoundscapePreset_Checked;
            SoundscapeWrapPanel.Children.Add(rb);
        }

        // 2. Custom Tracks list
        if (CustomTracksListPanel == null) return;
        CustomTracksListPanel.Children.Clear();

        int customCount = uiSettings.CustomSoundtracks.Count;
        if (CustomTracksCountLabel != null)
        {
            CustomTracksCountLabel.Text = $"({customCount})";
        }

        if (customCount == 0)
        {
            if (EmptyCustomTracksMessage != null) EmptyCustomTracksMessage.Visibility = Visibility.Visible;
        }
        else
        {
            if (EmptyCustomTracksMessage != null) EmptyCustomTracksMessage.Visibility = Visibility.Collapsed;

            foreach (var meta in uiSettings.CustomSoundtracks)
            {
                var rowBorder = new Border
                {
                    Background = (Brush)FindResource("Surface"),
                    BorderBrush = (Brush)FindResource("Border"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = (CornerRadius)FindResource("RadiusCard"),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 0, 4)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var rb = new RadioButton
                {
                    Content = meta.DisplayName,
                    GroupName = "SoundscapePresetGroup",
                    Tag = meta.Id,
                    IsChecked = string.Equals(meta.Id, SelectedSoundscapeId, StringComparison.OrdinalIgnoreCase),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = (System.Windows.Media.FontFamily)FindResource("FontBody"),
                    FontSize = (double)FindResource("FontSize12"),
                    Foreground = (Brush)FindResource("TextPrimary")
                };
                rb.Checked += SoundscapePreset_Checked;
                Grid.SetColumn(rb, 0);
                grid.Children.Add(rb);

                var renameBtn = new Button
                {
                    Content = "Rename",
                    Style = (Style)FindResource("SecondaryButton"),
                    FontSize = (double)FindResource("FontSize11"),
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(0, 0, 6, 0),
                    Tag = meta.Id
                };
                renameBtn.Click += RenameTrack_Click;
                Grid.SetColumn(renameBtn, 1);
                grid.Children.Add(renameBtn);

                var deleteBtn = new Button
                {
                    Content = "Delete",
                    Style = (Style)FindResource("SecondaryButton"),
                    FontSize = (double)FindResource("FontSize11"),
                    Padding = new Thickness(8, 2, 8, 2),
                    Tag = meta.Id
                };
                deleteBtn.Click += DeleteTrack_Click;
                Grid.SetColumn(deleteBtn, 2);
                grid.Children.Add(deleteBtn);

                rowBorder.Child = grid;
                CustomTracksListPanel.Children.Add(rowBorder);
            }
        }
    }

    private void SoundscapePreset_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string trackId)
        {
            SelectedSoundscapeId = trackId;
            SelectedSoundscape = SoundscapeCatalog.ToLegacyEnum(trackId);
        }
    }

    private async void ImportAudio_Click(object sender, RoutedEventArgs e)
    {
        if (IsMeditationSessionActive())
        {
            ShowSoundscapeError("Importing audio is disabled while a meditation session is active.");
            return;
        }

        HideSoundscapeError();

        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Audio Tracks",
            Filter = "Audio and Playlists (*.mp3;*.wav;*.m4a;*.wma;*.aac;*.flac;*.m3u;*.m3u8)|*.mp3;*.wav;*.m4a;*.wma;*.aac;*.flac;*.m3u;*.m3u8|Audio Files (*.mp3;*.wav;*.m4a;*.wma;*.aac;*.flac)|*.mp3;*.wav;*.m4a;*.wma;*.aac;*.flac|Playlists (*.m3u;*.m3u8)|*.m3u;*.m3u8|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (ofd.ShowDialog(this) != true) return;

        var filesToImport = new List<string>();
        var playlistWarnings = new List<string>();

        foreach (var file in ofd.FileNames)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".m3u" || ext == ".m3u8")
            {
                if (AudioImportService.TryParsePlaylist(file, out var playlistFiles, out var warnings, out var err))
                {
                    filesToImport.AddRange(playlistFiles);
                    playlistWarnings.AddRange(warnings);
                }
                else if (!string.IsNullOrEmpty(err))
                {
                    playlistWarnings.Add($"{Path.GetFileName(file)}: {err}");
                }
            }
            else
            {
                filesToImport.Add(file);
            }
        }

        if (filesToImport.Count == 0)
        {
            ShowSoundscapeError(playlistWarnings.Count > 0 ? string.Join("; ", playlistWarnings) : "No valid audio files selected.");
            return;
        }

        ShowSoundscapeError("Importing and verifying audio files...");

        var currentSettings = UiSettingsStore.Load();
        var result = await AudioImportService.ImportAudioFilesAsync(
            filesToImport,
            currentSettings,
            null,
            async path =>
            {
                var probe = await ProbeAudioDecodableAsync(path, TimeSpan.FromSeconds(4));
                return (probe.Success, probe.Duration?.TotalSeconds, probe.Error);
            });

        if (result.ImportedTracks.Count > 0)
        {
            SelectedSoundscapeId = result.ImportedTracks[^1].Id;
            SelectedSoundscape = SoundscapeType.Custom;
        }

        HideSoundscapeError();
        PopulateSoundscapes();

        if (result.FailureCount > 0 || result.DuplicateCount > 0 || playlistWarnings.Count > 0)
        {
            string summary = result.GetSummaryMessage();
            if (playlistWarnings.Count > 0)
            {
                summary += " " + string.Join("; ", playlistWarnings.Take(3));
            }
            ShowSoundscapeError(summary);
        }
    }

    private async void ImportPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (IsMeditationSessionActive())
        {
            ShowSoundscapeError("Importing playlists is disabled while a meditation session is active.");
            return;
        }

        HideSoundscapeError();

        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Playlist (.m3u, .m3u8)",
            Filter = "Playlists (*.m3u;*.m3u8)|*.m3u;*.m3u8|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (ofd.ShowDialog(this) != true) return;

        if (!AudioImportService.TryParsePlaylist(ofd.FileName, out var resolvedFiles, out var warnings, out var err))
        {
            ShowSoundscapeError(err ?? "Failed to parse playlist.");
            return;
        }

        ShowSoundscapeError("Importing playlist entries...");

        var currentSettings = UiSettingsStore.Load();
        var result = await AudioImportService.ImportAudioFilesAsync(
            resolvedFiles,
            currentSettings,
            null,
            async path =>
            {
                var probe = await ProbeAudioDecodableAsync(path, TimeSpan.FromSeconds(4));
                return (probe.Success, probe.Duration?.TotalSeconds, probe.Error);
            });

        if (result.ImportedTracks.Count > 0)
        {
            SelectedSoundscapeId = result.ImportedTracks[^1].Id;
            SelectedSoundscape = SoundscapeType.Custom;
        }

        HideSoundscapeError();
        PopulateSoundscapes();

        string summary = result.GetSummaryMessage();
        if (warnings.Count > 0)
        {
            summary += " Warnings: " + string.Join("; ", warnings.Take(3));
        }
        ShowSoundscapeError(summary);
    }

    private void RenameTrack_Click(object sender, RoutedEventArgs e)
    {
        if (IsMeditationSessionActive()) return;

        if (sender is Button btn && btn.Tag is string trackId)
        {
            var uiSettings = UiSettingsStore.Load();
            var meta = uiSettings.CustomSoundtracks.FirstOrDefault(t => t.Id == trackId);
            if (meta == null) return;

            _renamingTrackId = trackId;
            if (RenameTrackInput != null && RenameTrackPanel != null)
            {
                RenameTrackInput.Text = meta.DisplayName;
                RenameTrackPanel.Visibility = Visibility.Visible;
                RenameTrackInput.Focus();
                RenameTrackInput.SelectAll();
            }
        }
    }

    private void SaveRenameTrack_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_renamingTrackId) || RenameTrackInput == null) return;

        string clean = SoundscapeCatalog.SanitizeDisplayName(RenameTrackInput.Text);
        if (string.IsNullOrWhiteSpace(clean)) return;

        var uiSettings = UiSettingsStore.Load();
        AudioImportService.RenameCustomTrack(_renamingTrackId, clean, uiSettings);

        if (RenameTrackPanel != null) RenameTrackPanel.Visibility = Visibility.Collapsed;
        _renamingTrackId = null;
        PopulateSoundscapes();
    }

    private void CancelRenameTrack_Click(object sender, RoutedEventArgs e)
    {
        if (RenameTrackPanel != null) RenameTrackPanel.Visibility = Visibility.Collapsed;
        _renamingTrackId = null;
    }

    private void DeleteTrack_Click(object sender, RoutedEventArgs e)
    {
        if (IsMeditationSessionActive()) return;

        if (sender is Button btn && btn.Tag is string trackId)
        {
            var uiSettings = UiSettingsStore.Load();
            var meta = uiSettings.CustomSoundtracks.FirstOrDefault(t => t.Id == trackId);
            if (meta == null) return;

            var result = System.Windows.MessageBox.Show(
                this,
                $"Are you sure you want to remove '{meta.DisplayName}'?",
                "Delete Soundtrack",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            bool wasSelected = string.Equals(SelectedSoundscapeId, trackId, StringComparison.OrdinalIgnoreCase);

            AudioImportService.DeleteCustomTrack(trackId, uiSettings);

            if (wasSelected)
            {
                SelectedSoundscapeId = SoundscapeCatalog.RainId;
                SelectedSoundscape = SoundscapeType.Rain;
            }

            PopulateSoundscapes();
        }
    }

    private void OpenAudioFolder_Click(object sender, RoutedEventArgs e)
    {
        if (IsMeditationSessionActive()) return;

        try
        {
            string dir = SoundscapeCatalog.GetCustomAudioDirectory();
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowSoundscapeError($"Could not open audio directory: {ex.Message}");
        }
    }

    private static async Task<(bool Success, TimeSpan? Duration, string? Error)> ProbeAudioDecodableAsync(string filePath, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<(bool Success, TimeSpan? Duration, string? Error)>();
        var player = new MediaPlayer();

        void Cleanup()
        {
            try
            {
                player.MediaOpened -= OnOpened;
                player.MediaFailed -= OnFailed;
                player.Close();
            }
            catch { }
        }

        void OnOpened(object? sender, EventArgs e)
        {
            TimeSpan? dur = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : null;
            Cleanup();
            tcs.TrySetResult((true, dur, null));
        }

        void OnFailed(object? sender, ExceptionEventArgs e)
        {
            string msg = e.ErrorException?.Message ?? "Decoder failed";
            Cleanup();
            tcs.TrySetResult((false, null, msg));
        }

        player.MediaOpened += OnOpened;
        player.MediaFailed += OnFailed;

        try
        {
            player.Open(new Uri(filePath, UriKind.Absolute));
        }
        catch (Exception ex)
        {
            Cleanup();
            return (false, null, ex.Message);
        }

        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() =>
        {
            Cleanup();
            tcs.TrySetResult((false, null, "Decoder probe timed out."));
        });

        return await tcs.Task;
    }

    public static bool IsMeditationSessionActive()
    {
        try
        {
            if (Application.Current?.MainWindow?.DataContext is MainViewModel mainVm)
            {
                if (mainVm.Meditation?.IsActive == true)
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private void ShowSoundscapeError(string message)
    {
        if (SoundscapeStatusMessage == null) return;
        SoundscapeStatusMessage.Text = message;
        SoundscapeStatusMessage.Visibility = Visibility.Visible;
    }

    private void HideSoundscapeError()
    {
        if (SoundscapeStatusMessage == null) return;
        SoundscapeStatusMessage.Visibility = Visibility.Collapsed;
        SoundscapeStatusMessage.Text = string.Empty;
    }

    public static string ComputeSha256Hash(string rawData)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
