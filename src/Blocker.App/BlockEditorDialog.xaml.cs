using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Blocker.Core;
using Color = System.Windows.Media.Color;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace Blocker.App;

public class ScheduleRowItem : ViewModelBase
{
    private bool _mon = true;
    private bool _tue = true;
    private bool _wed = true;
    private bool _thu = true;
    private bool _fri = true;
    private bool _sat = false;
    private bool _sun = false;
    private string _fromText = "08:00";
    private string _toText = "17:00";
    private string? _error;

    public bool Mon { get => _mon; set => SetProperty(ref _mon, value); }
    public bool Tue { get => _tue; set => SetProperty(ref _tue, value); }
    public bool Wed { get => _wed; set => SetProperty(ref _wed, value); }
    public bool Thu { get => _thu; set => SetProperty(ref _thu, value); }
    public bool Fri { get => _fri; set => SetProperty(ref _fri, value); }
    public bool Sat { get => _sat; set => SetProperty(ref _sat, value); }
    public bool Sun { get => _sun; set => SetProperty(ref _sun, value); }

    public string FromText
    {
        get => _fromText;
        set
        {
            if (SetProperty(ref _fromText, value)) Validate();
        }
    }

    public string ToText
    {
        get => _toText;
        set
        {
            if (SetProperty(ref _toText, value)) Validate();
        }
    }

    public string? Error { get => _error; set => SetProperty(ref _error, value); }

    public DaysOfWeek GetDays()
    {
        DaysOfWeek days = DaysOfWeek.None;
        if (Mon) days |= DaysOfWeek.Mon;
        if (Tue) days |= DaysOfWeek.Tue;
        if (Wed) days |= DaysOfWeek.Wed;
        if (Thu) days |= DaysOfWeek.Thu;
        if (Fri) days |= DaysOfWeek.Fri;
        if (Sat) days |= DaysOfWeek.Sat;
        if (Sun) days |= DaysOfWeek.Sun;
        return days;
    }

    public void SetDays(DaysOfWeek days)
    {
        Mon = days.HasFlag(DaysOfWeek.Mon);
        Tue = days.HasFlag(DaysOfWeek.Tue);
        Wed = days.HasFlag(DaysOfWeek.Wed);
        Thu = days.HasFlag(DaysOfWeek.Thu);
        Fri = days.HasFlag(DaysOfWeek.Fri);
        Sat = days.HasFlag(DaysOfWeek.Sat);
        Sun = days.HasFlag(DaysOfWeek.Sun);
    }

    public bool TryGetRule(out ScheduleRule rule, out string error)
    {
        rule = new ScheduleRule();
        var days = GetDays();
        if (days == DaysOfWeek.None)
        {
            error = "Select at least one day for the schedule rule.";
            return false;
        }

        if (!TryParseMinute(FromText, out int startMin) || !TryParseMinute(ToText, out int endMin))
        {
            error = "Invalid time format. Use HH:mm (e.g. 08:00).";
            return false;
        }

        if (startMin == endMin)
        {
            error = "Start and End times cannot be equal.";
            return false;
        }

        rule.Days = days;
        rule.StartMinute = startMin;
        rule.EndMinute = endMin;
        error = string.Empty;
        return true;
    }

    private void Validate()
    {
        if (TryParseMinute(FromText, out int startMin) && TryParseMinute(ToText, out int endMin))
        {
            if (startMin == endMin)
            {
                Error = "Start and End times cannot be equal.";
            }
            else
            {
                Error = null;
            }
        }
        else
        {
            Error = null;
        }
    }

    private static bool TryParseMinute(string s, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.NoCurrentDateDefault, out DateTime dt) ||
            DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.NoCurrentDateDefault, out dt))
        {
            minutes = dt.Hour * 60 + dt.Minute;
            return true;
        }
        var parts = s.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m))
        {
            if (h >= 0 && h < 24 && m >= 0 && m < 60)
            {
                minutes = h * 60 + m;
                return true;
            }
        }
        return false;
    }
}

public partial class BlockEditorDialog : Window
{
    public static string FormatScheduleChipText(bool is24Hour)
    {
        return is24Hour ? "Weekdays 08:00-17:00" : "Weekdays 8:00 AM-5:00 PM";
    }

    private readonly ProfileViewModel? _originalProfile;
    private readonly IEnumerable<string>? _existingNames;
    private readonly bool _isElevated;
    private readonly bool _hasExistingPassword;

    public ObservableCollection<string> Apps { get; } = new();
    public ObservableCollection<string> BlockedRules { get; } = new();
    public ObservableCollection<string> ExceptionRules { get; } = new();
    public ObservableCollection<ScheduleRowItem> ScheduleRows { get; } = new();

    public bool IsNew => _originalProfile == null;
    public bool Saved { get; private set; }
    public bool Deleted { get; private set; }

    public string ResultName { get; private set; } = string.Empty;
    public List<string> ResultApps => Apps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public List<string> ResultSites => WebRuleMatcher.DeriveBlockedDomains(ResultWebRules, ResultBlockAllWebsites);
    public List<WebRule> ResultWebRules { get; private set; } = new();
    public bool ResultBlockAllWebsites { get; private set; }
    public List<ScheduleRule> ResultSchedule { get; private set; } = new();
    public LockMode ResultLockMode { get; private set; } = LockMode.None;
    public int ResultDelayedUnlockMinutes { get; private set; } = 15;
    public AllowanceConfig ResultAllowance { get; private set; } = new();
    public bool ResultSystemWide { get; private set; }
    public string? ResultPassword { get; private set; }
    public string? ResultCurrentPassword { get; private set; }
    public int StartTimedLockMinutes { get; private set; } = 0;

    public bool IsLockedMode { get; private set; }
    public Visibility RemoveButtonVisibility => IsLockedMode ? Visibility.Collapsed : Visibility.Visible;
    private readonly bool _isLockedMode;

    public int SelectedTabIndex
    {
        get
        {
            if (TabLockRadio?.IsChecked == true) return 3;
            if (TabScheduleRadio?.IsChecked == true) return 2;
            if (TabWebsitesRadio?.IsChecked == true) return 1;
            return 0;
        }
        set => SelectTabByIndex(value);
    }

    public Visibility AppsVisibility => AppsSectionBorder?.Visibility ?? Visibility.Collapsed;
    public Visibility WebsitesVisibility => WebsitesSectionBorder?.Visibility ?? Visibility.Collapsed;
    public Visibility ScheduleVisibility => ScheduleSectionBorder?.Visibility ?? Visibility.Collapsed;
    public Visibility LockVisibility => LockSectionBorder?.Visibility ?? Visibility.Collapsed;

    public BlockEditorDialog(ProfileViewModel? profile, int initialTab)
        : this(profile, false, null, true, initialTab)
    {
    }

    public BlockEditorDialog(
        ProfileViewModel? profile = null,
        bool isLockedInActiveSession = false,
        IEnumerable<string>? existingNames = null,
        bool isElevated = true,
        int initialTab = 0)
    {
        InitializeComponent();
        WindowHelper.EnableDarkMode(this);

        _originalProfile = profile;
        _existingNames = existingNames;
        _isElevated = isElevated;
        _hasExistingPassword = profile?.LockMode == LockMode.Password;
        _isLockedMode = isLockedInActiveSession || (profile != null && profile.LockedNow);
        IsLockedMode = _isLockedMode;

        AppsListBox.ItemsSource = Apps;
        BlockedPatternsListBox.ItemsSource = BlockedRules;
        ExceptionPatternsListBox.ItemsSource = ExceptionRules;
        ScheduleRowsControl.ItemsSource = ScheduleRows;

        Apps.CollectionChanged += (s, e) => UpdateTabBadges();
        BlockedRules.CollectionChanged += (s, e) => UpdateTabBadges();
        ExceptionRules.CollectionChanged += (s, e) => UpdateTabBadges();
        ScheduleRows.CollectionChanged += (s, e) =>
        {
            UpdateScheduleEmptyState();
            UpdateTabBadges();
        };

        SelectTabByIndex(initialTab);
        Loaded += (s, e) => SelectTabByIndex(initialTab);

        if (profile != null)
        {
            DialogHeader.Text = $"Edit Block: {profile.Name}";
            if (HeaderLockIcon != null)
            {
                HeaderLockIcon.Visibility = _isLockedMode ? Visibility.Visible : Visibility.Collapsed;
            }
            NameInput.Text = profile.Name;

            if (_isLockedMode)
            {
                LockedBanner.Visibility = Visibility.Visible;
                NameInput.IsEnabled = true;
                ExceptionPatternInput.IsEnabled = false;
                AddExceptionButton.IsEnabled = false;
                ImportExceptionsButton.IsEnabled = false;
                if (ExceptionLockedHintPanel != null)
                {
                    ExceptionLockedHintPanel.Visibility = Visibility.Visible;
                }
                ExceptionPatternErrorTextBlock.Visibility = Visibility.Collapsed;
                rbUnlocked.IsEnabled = false;
                rbTimedLock.IsEnabled = false;
                rbLocked.IsEnabled = false;
                rbTyping.IsEnabled = false;
                rbPassword.IsEnabled = false;
                if (rbDelayedUnlock != null) rbDelayedUnlock.IsEnabled = false;
                if (DelayedUnlockBorder != null) DelayedUnlockBorder.IsEnabled = false;
                if (rbPomodoro != null) rbPomodoro.IsEnabled = false;
                TimedLockBorder.IsEnabled = false;
                if (PomodoroBorder != null) PomodoroBorder.IsEnabled = false;
                if (ActiveLockedNoticeBorder != null) ActiveLockedNoticeBorder.Visibility = Visibility.Visible;
                if (TimedLockDurationInput != null) TimedLockDurationInput.IsEnabled = false;
                if (TimedLockUnitComboBox != null) TimedLockUnitComboBox.IsEnabled = false;
                if (StartTimedLockNowButton != null)
                {
                    StartTimedLockNowButton.IsEnabled = false;
                    StartTimedLockNowButton.Visibility = Visibility.Collapsed;
                }
                if (StartPomodoroLockNowButton != null)
                {
                    StartPomodoroLockNowButton.IsEnabled = false;
                    StartPomodoroLockNowButton.Visibility = Visibility.Collapsed;
                }
                if (profile.BlockAllWebsites)
                {
                    BlockAllWebsitesCheckBox.IsEnabled = false;
                }

                if (isLockedInActiveSession)
                {
                    if (CompleteLockScheduleNoticeBorder != null)
                    {
                        CompleteLockScheduleNoticeBorder.Visibility = Visibility.Visible;
                    }
                    if (ScheduleContentPanel != null)
                    {
                        ScheduleContentPanel.IsEnabled = false;
                        ScheduleContentPanel.Opacity = 0.6;
                    }
                }
            }

            foreach (var app in profile.BlockedProcesses)
            {
                Apps.Add(app);
            }

            if (profile.WebRules != null && profile.WebRules.Count > 0)
            {
                foreach (var rule in profile.WebRules)
                {
                    if (rule.Allow)
                    {
                        ExceptionRules.Add(rule.Pattern);
                    }
                    else
                    {
                        BlockedRules.Add(rule.Pattern);
                    }
                }
            }
            else
            {
                foreach (var domain in profile.BlockedDomains)
                {
                    BlockedRules.Add(domain);
                }
            }

            BlockAllWebsitesCheckBox.IsChecked = profile.BlockAllWebsites;
            SystemWideCheckBox.IsChecked = profile.SystemWide;

            foreach (var rule in profile.Schedule)
            {
                var row = new ScheduleRowItem
                {
                    FromText = $"{rule.StartMinute / 60:D2}:{rule.StartMinute % 60:D2}",
                    ToText = $"{rule.EndMinute / 60:D2}:{rule.EndMinute % 60:D2}"
                };
                row.SetDays(rule.Days);
                ScheduleRows.Add(row);
            }

            // Daily Allowance selection
            if (profile.Allowance != null && profile.Allowance.Enabled)
            {
                AllowanceCheckBox.IsChecked = true;
                AllowanceMinutesInput.Text = Math.Max(1, profile.Allowance.DailyAllowanceMinutes).ToString();
                if (profile.Allowance.OnlyWithinSchedule)
                {
                    rbAllowanceInSchedule.IsChecked = true;
                }
                else
                {
                    rbAllowance24x7.IsChecked = true;
                }
                AllowanceConfigPanel.Visibility = Visibility.Visible;
            }

            // Lock mode selection
            if (isLockedInActiveSession)
            {
                rbTimedLock.IsChecked = true;
                if (TimedLockBorder != null) TimedLockBorder.Visibility = Visibility.Visible;
            }
            else
            {
                switch (profile.LockMode)
                {
                    case LockMode.Locked:
                        rbLocked.IsChecked = true;
                        break;
                    case LockMode.TypingChallenge:
                        rbTyping.IsChecked = true;
                        break;
                    case LockMode.Password:
                        rbPassword.IsChecked = true;
                        CurrentPasswordPanel.Visibility = Visibility.Visible;
                        break;
                    case LockMode.DelayedUnlock:
                        if (rbDelayedUnlock != null) rbDelayedUnlock.IsChecked = true;
                        SetDelayedUnlockPreset(profile.DelayedUnlockMinutes > 0 ? profile.DelayedUnlockMinutes : 15);
                        break;
                    default:
                        rbUnlocked.IsChecked = true;
                        break;
                }
            }

            DeleteButton.Visibility = Visibility.Visible;
            if (isLockedInActiveSession || profile.LockedNow)
            {
                DeleteButton.IsEnabled = false;
                DeleteButton.ToolTip = "Can't delete while the block is locked.";
            }
        }
        else
        {
            DialogHeader.Text = "New Block";
            NameInput.Text = "New Block";
            DeleteButton.Visibility = Visibility.Collapsed;
            rbUnlocked.IsChecked = true;
        }

        UpdateScheduleEmptyState();
        UpdateExtensionNote();
        UpdateLiveTestUrl();
        UpdateSystemWideState();
        UpdateTabBadges();
        UpdateTabVisibility();
    }

    private List<WebRule> GetCurrentWebRules()
    {
        var rules = new List<WebRule>();
        foreach (var p in BlockedRules)
        {
            rules.Add(new WebRule { Pattern = p, Allow = false });
        }
        foreach (var p in ExceptionRules)
        {
            rules.Add(new WebRule { Pattern = p, Allow = true });
        }
        return rules;
    }

    private void UpdateExtensionNote()
    {
        if (ExtensionNoteTextBlock == null) return;
        bool requires = UiLogic.RequiresExtensionNote(BlockAllWebsitesCheckBox.IsChecked == true, GetCurrentWebRules());
        ExtensionNoteTextBlock.Visibility = requires ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLiveTestUrl()
    {
        if (TestUrlResultTextBlock == null || TestUrlInput == null) return;
        string url = TestUrlInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            TestUrlResultTextBlock.Text = "Enter a URL above to test";
            TestUrlResultTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)); // #9CA3AF
            return;
        }

        string result = UiLogic.TestUrlAgainstRules(url, BlockAllWebsitesCheckBox.IsChecked == true, GetCurrentWebRules());
        TestUrlResultTextBlock.Text = result;

        if (result.StartsWith("Blocked", StringComparison.OrdinalIgnoreCase))
        {
            TestUrlResultTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107)); // Red
        }
        else if (result.StartsWith("Allowed by exception", StringComparison.OrdinalIgnoreCase))
        {
            TestUrlResultTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)); // Green
        }
        else if (result.Equals("Not blocked", StringComparison.OrdinalIgnoreCase))
        {
            TestUrlResultTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(147, 197, 253)); // Soft blue
        }
        else
        {
            TestUrlResultTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)); // Grey
        }
    }

    private void UpdateSystemWideState()
    {
        if (SystemWideCheckBox == null) return;
        if (!_isElevated)
        {
            SystemWideCheckBox.IsEnabled = false;
            SystemWideCheckBox.IsChecked = false;
            SystemWideCheckBox.ToolTip = "System-wide enforcement requires Administrator rights.";
            return;
        }

        var currentRules = GetCurrentWebRules();
        bool blockAll = BlockAllWebsitesCheckBox.IsChecked == true;
        bool canEnable = UiLogic.CanEnableSystemWide(blockAll, currentRules, out string? hint);
        if (!canEnable)
        {
            SystemWideCheckBox.IsEnabled = false;
            SystemWideCheckBox.IsChecked = false;
            SystemWideCheckBox.ToolTip = hint ?? "System-wide blocking edits the Windows hosts file and only supports plain domains without paths, exceptions, or wildcards.";
        }
        else
        {
            SystemWideCheckBox.IsEnabled = true;
            SystemWideCheckBox.ToolTip = "System-wide blocking edits the Windows hosts file and only supports plain domains without paths, exceptions, or wildcards.";
        }
    }

    private void SystemWideCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSystemWideState();
    }

    private void UpdateScheduleEmptyState()
    {
        if (NoScheduleTextBlock == null || rbLocked == null || LockedHintTextBlock == null) return;
        bool hasRules = ScheduleRows.Count > 0;
        NoScheduleTextBlock.Visibility = hasRules ? Visibility.Collapsed : Visibility.Visible;
        rbLocked.IsEnabled = hasRules;
        LockedHintTextBlock.Visibility = hasRules ? Visibility.Collapsed : Visibility.Visible;

        if (!hasRules && rbLocked.IsChecked == true)
        {
            rbUnlocked.IsChecked = true;
        }
    }

    #region Applications

    private void AppInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddAppInput_Click(sender, e);
        }
    }

    private void AddAppInput_Click(object sender, RoutedEventArgs e)
    {
        string raw = AppInput.Text;
        if (!UiLogic.ValidateProcessInput(raw, out string? norm, out string? err))
        {
            AppErrorTextBlock.Text = err;
            AppErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        if (Apps.Contains(norm!, StringComparer.OrdinalIgnoreCase))
        {
            AppErrorTextBlock.Text = $"'{norm}' is already in the list.";
            AppErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        Apps.Add(norm!);
        AppInput.Clear();
        AppErrorTextBlock.Visibility = Visibility.Collapsed;
    }

    private void BrowseApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select Application Executable",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        if (dlg.ShowDialog(this) == true)
        {
            string fileName = Path.GetFileName(dlg.FileName);
            if (UiLogic.ValidateProcessInput(fileName, out string? norm, out string? err))
            {
                if (!Apps.Contains(norm!, StringComparer.OrdinalIgnoreCase))
                {
                    Apps.Add(norm!);
                    AppErrorTextBlock.Visibility = Visibility.Collapsed;
                }
                else
                {
                    AppErrorTextBlock.Text = $"'{norm}' is already in the list.";
                    AppErrorTextBlock.Visibility = Visibility.Visible;
                }
            }
            else
            {
                AppErrorTextBlock.Text = err;
                AppErrorTextBlock.Visibility = Visibility.Visible;
            }
        }
    }

    private void PickApps_Click(object sender, RoutedEventArgs e)
    {
        var pickerDlg = new AppPickerDialog(Apps);
        if (IsLoaded && IsVisible)
        {
            try { pickerDlg.Owner = this; } catch { }
        }

        if (pickerDlg.ShowDialog() == true)
        {
            if (!_isLockedMode)
            {
                Apps.Clear();
            }
            foreach (var app in pickerDlg.SelectedApps)
            {
                if (!Apps.Contains(app, StringComparer.OrdinalIgnoreCase))
                {
                    Apps.Add(app);
                }
            }
            AppErrorTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (_isLockedMode)
        {
            DarkDialogWindow.Show("Cannot remove blocked applications while this block is locked.", "Action Refused");
            return;
        }
        if (sender is Button btn && btn.DataContext is string app)
        {
            Apps.Remove(app);
        }
    }

    #endregion

    #region Websites & Exceptions

    private void BlockedPatternInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (BlockedPatternPlaceholder != null)
        {
            BlockedPatternPlaceholder.Visibility = string.IsNullOrEmpty(BlockedPatternInput.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void BlockedPatternInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddBlockedPattern_Click(sender, e);
        }
    }

    private void AddBlockedPattern_Click(object sender, RoutedEventArgs e)
    {
        if (BlockedRules.Count + ExceptionRules.Count >= 500)
        {
            BlockedPatternErrorTextBlock.Text = "Maximum limit of 500 website rules reached per block.";
            BlockedPatternErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        string raw = BlockedPatternInput.Text;
        if (!WebRuleMatcher.TryNormalizePattern(raw, out string norm, out string? err))
        {
            BlockedPatternErrorTextBlock.Text = err ?? "Invalid pattern.";
            BlockedPatternErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        if (BlockedRules.Contains(norm, StringComparer.OrdinalIgnoreCase))
        {
            BlockedPatternErrorTextBlock.Text = $"'{norm}' is already in the blocked list.";
            BlockedPatternErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        BlockedRules.Add(norm);
        BlockedPatternInput.Clear();
        BlockedPatternErrorTextBlock.Visibility = Visibility.Collapsed;
        UpdateExtensionNote();
        UpdateLiveTestUrl();
        UpdateSystemWideState();
    }

    private void RemoveBlockedPattern_Click(object sender, RoutedEventArgs e)
    {
        if (_isLockedMode)
        {
            DarkDialogWindow.Show("Cannot remove blocked websites while this block is locked.", "Action Refused");
            return;
        }
        if (sender is Button btn && btn.DataContext is string pattern)
        {
            BlockedRules.Remove(pattern);
            UpdateExtensionNote();
            UpdateLiveTestUrl();
            UpdateSystemWideState();
        }
    }

    private void ExceptionPatternInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ExceptionPatternPlaceholder != null)
        {
            ExceptionPatternPlaceholder.Visibility = string.IsNullOrEmpty(ExceptionPatternInput.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ExceptionPatternInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddExceptionPattern_Click(sender, e);
        }
    }

    private void AddExceptionPattern_Click(object sender, RoutedEventArgs e)
    {
        if (_isLockedMode)
        {
            DarkDialogWindow.Show("Exceptions cannot be added while this block is locked.", "Action Refused");
            return;
        }

        if (BlockedRules.Count + ExceptionRules.Count >= 500)
        {
            ExceptionPatternErrorTextBlock.Text = "Maximum limit of 500 website rules reached per block.";
            ExceptionPatternErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        string raw = ExceptionPatternInput.Text;
        if (!WebRuleMatcher.TryNormalizePattern(raw, out string norm, out string? err))
        {
            ExceptionPatternErrorTextBlock.Text = err ?? "Invalid pattern.";
            ExceptionPatternErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        if (norm == "*" || norm == "*.*" || norm == "*/*" || norm.StartsWith("*/"))
        {
            ExceptionPatternErrorTextBlock.Text = "Universal wildcard '*' cannot be used as an exception. All websites outside your block list are already allowed. Exceptions should only specify URLs or paths within blocked sites (e.g. 'youtube.com/watch').";
            ExceptionPatternErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        if (ExceptionRules.Contains(norm, StringComparer.OrdinalIgnoreCase))
        {
            ExceptionPatternErrorTextBlock.Text = $"'{norm}' is already in the exceptions list.";
            ExceptionPatternErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        ExceptionRules.Add(norm);
        ExceptionPatternInput.Clear();
        ExceptionPatternErrorTextBlock.Visibility = Visibility.Collapsed;
        UpdateExtensionNote();
        UpdateLiveTestUrl();
        UpdateSystemWideState();
    }

    private void RemoveExceptionPattern_Click(object sender, RoutedEventArgs e)
    {
        if (_isLockedMode)
        {
            DarkDialogWindow.Show("Cannot remove exceptions while this block is locked.", "Action Refused");
            return;
        }
        if (sender is Button btn && btn.DataContext is string pattern)
        {
            ExceptionRules.Remove(pattern);
            UpdateExtensionNote();
            UpdateLiveTestUrl();
            UpdateSystemWideState();
        }
    }

    private void ImportBlockedWebsites_Click(object sender, RoutedEventArgs e)
    {
        ImportWebsitesFromDialog(BlockedRules, "Blocked Websites");
    }

    private void ImportExceptionWebsites_Click(object sender, RoutedEventArgs e)
    {
        if (_isLockedMode)
        {
            DarkDialogWindow.Show("Exceptions cannot be added while this block is locked.", "Action Refused");
            return;
        }
        ImportWebsitesFromDialog(ExceptionRules, "Exceptions");
    }

    private void BlockedPatternsListBox_Drop(object sender, System.Windows.DragEventArgs e)
    {
        HandleFileDrop(e, BlockedRules, "Blocked Websites");
    }

    private void ExceptionPatternsListBox_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (_isLockedMode) return;
        HandleFileDrop(e, ExceptionRules, "Exceptions");
    }

    private void PatternsListBox_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void HandleFileDrop(System.Windows.DragEventArgs e, ObservableCollection<string> targetCollection, string listTitle)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            string[]? files = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0 && File.Exists(files[0]))
            {
                ImportWebsitesFromFile(files[0], targetCollection, listTitle);
            }
        }
    }

    private void ImportWebsitesFromDialog(ObservableCollection<string> targetCollection, string listTitle)
    {
        var dlg = new OpenFileDialog
        {
            Title = $"Bulk Import {listTitle} (.txt)",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Multiselect = false
        };

        if (dlg.ShowDialog(this) == true)
        {
            ImportWebsitesFromFile(dlg.FileName, targetCollection, listTitle);
        }
    }

    private void ImportWebsitesFromFile(string filePath, ObservableCollection<string> targetCollection, string listTitle)
    {
        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to read file:\n{ex.Message}", "Error Reading File", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var patterns = WebRuleMatcher.ParsePatternsFromText(content, out int invalidCount, out int fileDupCount);
        if (patterns.Count == 0 && invalidCount == 0)
        {
            MessageBox.Show(this, "The selected file is empty or contains no website entries.", $"Import {listTitle}", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (BlockedRules.Count + ExceptionRules.Count >= 500)
        {
            MessageBox.Show(this, "Maximum limit of 500 website rules already reached for this block. Remove existing rules before importing new ones.", $"Import {listTitle}", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int addedCount = 0;
        int alreadyInListCount = 0;
        int cappedCount = 0;
        const int maxLimit = 500;

        foreach (var pattern in patterns)
        {
            if (BlockedRules.Count + ExceptionRules.Count >= maxLimit)
            {
                cappedCount++;
                continue;
            }

            if (!targetCollection.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            {
                targetCollection.Add(pattern);
                addedCount++;
            }
            else
            {
                alreadyInListCount++;
            }
        }

        UpdateExtensionNote();
        UpdateLiveTestUrl();
        UpdateSystemWideState();
        UpdateTabBadges();

        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"Successfully imported {addedCount} website(s) into {listTitle}.");
        if (cappedCount > 0)
        {
            summary.AppendLine($"• {cappedCount} website(s) were omitted: maximum safe limit of 500 website rules reached per block to keep your app fast.");
        }
        if (cappedCount > 0)
        {
            summary.AppendLine($"• {cappedCount} website(s) were omitted: maximum safe limit of 500 website rules reached per block to keep your app fast.");
        }
        if (alreadyInListCount > 0)
        {
            summary.AppendLine($"• {alreadyInListCount} entry/entries were already in your list.");
        }
        if (fileDupCount > 0)
        {
            summary.AppendLine($"• {fileDupCount} duplicate line(s) in the file were ignored.");
        }
        if (invalidCount > 0)
        {
            summary.AppendLine($"• {invalidCount} invalid pattern(s) were skipped.");
        }

        MessageBox.Show(this, summary.ToString().TrimEnd(), $"Import {listTitle}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BlockAllWebsitesCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateExtensionNote();
        UpdateLiveTestUrl();
        UpdateSystemWideState();
    }

    private void TestUrlInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateLiveTestUrl();
    }

    #endregion

    #region Schedule

    private void AddScheduleRow_Click(object sender, RoutedEventArgs e)
    {
        bool is24H = string.Equals(UiSettingsStore.Load().TimeFormat, "24h", StringComparison.OrdinalIgnoreCase);
        var row = new ScheduleRowItem
        {
            Mon = true,
            Tue = true,
            Wed = true,
            Thu = true,
            Fri = true,
            Sat = false,
            Sun = false,
            FromText = is24H ? "08:00" : "8:00 AM",
            ToText = is24H ? "17:00" : "5:00 PM"
        };
        ScheduleRows.Add(row);
        ScheduleErrorTextBlock.Visibility = Visibility.Collapsed;
    }

    private void RemoveScheduleRow_Click(object sender, RoutedEventArgs e)
    {
        if (_isLockedMode)
        {
            DarkDialogWindow.Show("Cannot remove schedule rules while this block is locked.", "Action Refused");
            return;
        }
        if (sender is Button btn && btn.DataContext is ScheduleRowItem row)
        {
            ScheduleRows.Remove(row);
        }
    }

    private void AllowanceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (AllowanceConfigPanel != null)
        {
            AllowanceConfigPanel.Visibility = AllowanceCheckBox.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void AllowanceQuick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag != null && AllowanceMinutesInput != null)
        {
            AllowanceMinutesInput.Text = btn.Tag.ToString();
        }
    }

    #endregion

    #region Lock & Save

    private void LockMode_Changed(object sender, RoutedEventArgs e)
    {
        if (PasswordInputsBorder != null)
        {
            PasswordInputsBorder.Visibility = rbPassword.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (TimedLockBorder != null)
        {
            TimedLockBorder.Visibility = rbTimedLock.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (PomodoroBorder != null)
        {
            PomodoroBorder.Visibility = rbPomodoro?.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        UpdateScheduleEmptyState();
    }

    private void StartPomodoroLockNow_Click(object sender, RoutedEventArgs e)
    {
        if (_isLockedMode) return;
        int focus = int.TryParse(PomoFocusInput?.Text?.Trim(), out int f) && f > 0 ? f : 25;
        int pbreak = int.TryParse(PomoBreakInput?.Text?.Trim(), out int b) && b > 0 ? b : 5;
        int cycles = int.TryParse(PomoCyclesInput?.Text?.Trim(), out int c) && c > 0 ? c : 4;
        StartTimedLockMinutes = (focus * cycles) + (pbreak * Math.Max(0, cycles - 1));
        SaveButton_Click(sender, e);
    }

    private void QuickLockDuration_Click(object sender, RoutedEventArgs e)
    {
        if (_isLockedMode) return;
        if (sender is Button btn && btn.Tag is string tagStr && TimedLockDurationInput != null && TimedLockUnitComboBox != null)
        {
            switch (tagStr)
            {
                case "1h":
                    TimedLockDurationInput.Text = "1";
                    TimedLockUnitComboBox.SelectedIndex = 1;
                    break;
                case "4h":
                    TimedLockDurationInput.Text = "4";
                    TimedLockUnitComboBox.SelectedIndex = 1;
                    break;
                case "1d":
                    TimedLockDurationInput.Text = "1";
                    TimedLockUnitComboBox.SelectedIndex = 2;
                    break;
                case "3d":
                    TimedLockDurationInput.Text = "3";
                    TimedLockUnitComboBox.SelectedIndex = 2;
                    break;
                case "1w":
                    TimedLockDurationInput.Text = "1";
                    TimedLockUnitComboBox.SelectedIndex = 3;
                    break;
                case "1m":
                    TimedLockDurationInput.Text = "1";
                    TimedLockUnitComboBox.SelectedIndex = 4;
                    break;
                case "3m":
                    TimedLockDurationInput.Text = "3";
                    TimedLockUnitComboBox.SelectedIndex = 4;
                    break;
                case "1y":
                    TimedLockDurationInput.Text = "1";
                    TimedLockUnitComboBox.SelectedIndex = 5;
                    break;
                default:
                    if (int.TryParse(tagStr, out int m))
                    {
                        TimedLockDurationInput.Text = m.ToString();
                        TimedLockUnitComboBox.SelectedIndex = 0;
                    }
                    break;
            }
        }
    }

    private int _selectedDelayedMinutes = 15;

    private void DelayedUnlockPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag != null && int.TryParse(btn.Tag.ToString(), out int mins))
        {
            SetDelayedUnlockPreset(mins);
        }
    }

    private void SetDelayedUnlockPreset(int minutes)
    {
        _selectedDelayedMinutes = minutes >= 15 ? minutes : 15;
        UpdateDelayedPresetButtonStyles();
    }

    private void UpdateDelayedPresetButtonStyles()
    {
        if (BtnDelayed15m == null || BtnDelayed30m == null || BtnDelayed45m == null || BtnDelayed60m == null) return;

        var secStyle = (Style)FindResource("SecondaryButton");
        var priStyle = (Style)FindResource("PrimaryButton");

        BtnDelayed15m.Style = _selectedDelayedMinutes == 15 ? priStyle : secStyle;
        BtnDelayed30m.Style = _selectedDelayedMinutes == 30 ? priStyle : secStyle;
        BtnDelayed45m.Style = _selectedDelayedMinutes == 45 ? priStyle : secStyle;
        BtnDelayed60m.Style = _selectedDelayedMinutes == 60 ? priStyle : secStyle;
    }

    private void StartTimedLockNow_Click(object sender, RoutedEventArgs e)
    {
        if (_isLockedMode) return;
        SaveButton_Click(sender, e);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // 1. Name validation
        string name = NameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            NameErrorTextBlock.Text = "Block name cannot be empty.";
            NameErrorTextBlock.Visibility = Visibility.Visible;
            NameInput.Focus();
            return;
        }

        if (IsNew && _existingNames != null && _existingNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            NameErrorTextBlock.Text = $"A block named '{name}' already exists.";
            NameErrorTextBlock.Visibility = Visibility.Visible;
            NameInput.Focus();
            return;
        }

        NameErrorTextBlock.Visibility = Visibility.Collapsed;

        // Validate at least one app, site rule, or block-all mode
        bool blockAll = BlockAllWebsitesCheckBox.IsChecked == true;
        if (!UiLogic.ValidateProfileSave(name, Apps.Count, BlockedRules.Count, _existingNames, _originalProfile?.Name, blockAll, out string? saveErr))
        {
            NameErrorTextBlock.Text = saveErr;
            NameErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        // 2. Schedule validation
        var rules = new List<ScheduleRule>();
        foreach (var row in ScheduleRows)
        {
            if (!row.TryGetRule(out var rule, out string err))
            {
                ScheduleErrorTextBlock.Text = err;
                ScheduleErrorTextBlock.Visibility = Visibility.Visible;
                return;
            }
            rules.Add(rule);
        }
        ScheduleErrorTextBlock.Visibility = Visibility.Collapsed;

        // 3. Lock validation
        LockMode lockMode = _isLockedMode && _originalProfile != null ? _originalProfile.LockMode : LockMode.None;
        StartTimedLockMinutes = 0;

        if (!_isLockedMode)
        {
            if (rbTimedLock.IsChecked == true)
            {
                if (!int.TryParse(TimedLockDurationInput?.Text?.Trim(), out int val) || val <= 0)
                {
                    LockErrorTextBlock.Text = "Please enter a valid positive lock duration number.";
                    LockErrorTextBlock.Visibility = Visibility.Visible;
                    TimedLockDurationInput?.Focus();
                    return;
                }

                int multiplier = TimedLockUnitComboBox?.SelectedIndex switch
                {
                    0 => 1,          // Minutes
                    1 => 60,         // Hours
                    2 => 1440,       // Days
                    3 => 10080,      // Weeks
                    4 => 43200,      // Months (30d)
                    5 => 525600,     // Years (365d)
                    _ => 1
                };

                try
                {
                    checked { StartTimedLockMinutes = val * multiplier; }
                }
                catch (OverflowException)
                {
                    StartTimedLockMinutes = int.MaxValue;
                }
                lockMode = LockMode.None;
            }
            else if (rbPomodoro?.IsChecked == true)
            {
                int focus = int.TryParse(PomoFocusInput?.Text?.Trim(), out int f) && f > 0 ? f : 25;
                int pbreak = int.TryParse(PomoBreakInput?.Text?.Trim(), out int b) && b > 0 ? b : 5;
                int cycles = int.TryParse(PomoCyclesInput?.Text?.Trim(), out int c) && c > 0 ? c : 4;
                StartTimedLockMinutes = (focus * cycles) + (pbreak * Math.Max(0, cycles - 1));
                lockMode = LockMode.None;
            }
            else if (rbLocked.IsChecked == true)
            {
                if (rules.Count == 0)
                {
                    LockErrorTextBlock.Text = "Locked mode requires at least one schedule rule.";
                    LockErrorTextBlock.Visibility = Visibility.Visible;
                    return;
                }
                lockMode = LockMode.Locked;
            }
            else if (rbTyping.IsChecked == true)
            {
                lockMode = LockMode.TypingChallenge;
            }
            else if (rbPassword.IsChecked == true)
            {
                lockMode = LockMode.Password;
                string newPwd = NewPasswordBox.Password;
                string confirmPwd = ConfirmPasswordBox.Password;

                if (!string.IsNullOrEmpty(newPwd))
                {
                    if (newPwd != confirmPwd)
                    {
                        LockErrorTextBlock.Text = "Passwords do not match.";
                        LockErrorTextBlock.Visibility = Visibility.Visible;
                        NewPasswordBox.Focus();
                        return;
                    }
                }
                else if (!_hasExistingPassword)
                {
                    LockErrorTextBlock.Text = "A password is required for Password lock mode.";
                    LockErrorTextBlock.Visibility = Visibility.Visible;
                    NewPasswordBox.Focus();
                    return;
                }
            }
            else if (rbDelayedUnlock != null && rbDelayedUnlock.IsChecked == true)
            {
                lockMode = LockMode.DelayedUnlock;
            }
        }
        LockErrorTextBlock.Visibility = Visibility.Collapsed;

        ResultName = name;
        ResultSchedule = ScheduleLogic.NormalizeSchedule(rules);
        int allowanceMins = int.TryParse(AllowanceMinutesInput?.Text?.Trim(), out int am) ? Math.Max(1, am) : 30;
        ResultAllowance = new AllowanceConfig
        {
            Enabled = AllowanceCheckBox?.IsChecked == true,
            DailyAllowanceMinutes = allowanceMins,
            OnlyWithinSchedule = rbAllowanceInSchedule?.IsChecked == true
        };
        ResultLockMode = lockMode;
        ResultDelayedUnlockMinutes = _selectedDelayedMinutes;
        ResultBlockAllWebsites = blockAll;
        ResultSystemWide = SystemWideCheckBox.IsChecked == true;
        ResultWebRules = GetCurrentWebRules();
        ResultPassword = !string.IsNullOrEmpty(NewPasswordBox.Password) ? NewPasswordBox.Password : null;
        ResultCurrentPassword = CurrentPasswordPanel.Visibility == Visibility.Visible && !string.IsNullOrEmpty(CurrentPasswordBox.Password)
            ? CurrentPasswordBox.Password
            : null;

        Saved = true;
        DialogResult = true;
        Close();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmDlg = DarkDialogWindow.CreateDialogWindow(
            $"Are you sure you want to delete block '{_originalProfile?.Name}'?",
            "Confirm Delete",
            isConfirmation: true,
            primaryText: "Delete",
            secondaryText: "Cancel");

        if (IsLoaded && IsVisible)
        {
            try { confirmDlg.Owner = this; } catch { }
        }

        if (confirmDlg.ShowDialog() == true)
        {
            Deleted = true;
            DialogResult = true;
            Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        UpdateTabVisibility();
    }

    private void UpdateTabVisibility()
    {
        if (AppsSectionBorder == null || WebsitesSectionBorder == null ||
            ScheduleSectionBorder == null || LockSectionBorder == null) return;

        bool showApps = TabAppsRadio?.IsChecked == true;
        bool showWeb = TabWebsitesRadio?.IsChecked == true;
        bool showSchedule = TabScheduleRadio?.IsChecked == true;
        bool showLock = TabLockRadio?.IsChecked == true;

        AppsSectionBorder.Visibility = showApps ? Visibility.Visible : Visibility.Collapsed;
        WebsitesSectionBorder.Visibility = showWeb ? Visibility.Visible : Visibility.Collapsed;
        ScheduleSectionBorder.Visibility = showSchedule ? Visibility.Visible : Visibility.Collapsed;
        LockSectionBorder.Visibility = showLock ? Visibility.Visible : Visibility.Collapsed;

        EditorScrollViewer?.ScrollToVerticalOffset(0);
    }

    public void SelectTabByIndex(int tabIndex)
    {
        switch (tabIndex)
        {
            case 3:
                if (TabLockRadio != null) TabLockRadio.IsChecked = true;
                break;
            case 2:
                if (TabScheduleRadio != null) TabScheduleRadio.IsChecked = true;
                break;
            case 1:
                if (TabWebsitesRadio != null) TabWebsitesRadio.IsChecked = true;
                break;
            default:
                if (TabAppsRadio != null) TabAppsRadio.IsChecked = true;
                break;
        }
        UpdateTabVisibility();
    }

    public void SelectTab(string tab)
    {
        if (string.Equals(tab, "Lock", StringComparison.OrdinalIgnoreCase))
        {
            if (TabLockRadio != null) TabLockRadio.IsChecked = true;
        }
        else if (string.Equals(tab, "Schedule", StringComparison.OrdinalIgnoreCase))
        {
            if (TabScheduleRadio != null) TabScheduleRadio.IsChecked = true;
        }
        else if (string.Equals(tab, "Websites", StringComparison.OrdinalIgnoreCase))
        {
            if (TabWebsitesRadio != null) TabWebsitesRadio.IsChecked = true;
        }
        else
        {
            if (TabAppsRadio != null) TabAppsRadio.IsChecked = true;
        }
        UpdateTabVisibility();
    }

    private void UpdateTabBadges()
    {
        if (TabAppsText != null)
            TabAppsText.Text = Apps.Count > 0 ? $"Applications ({Apps.Count})" : "Applications";
        if (TabWebsitesText != null)
        {
            int total = BlockedRules.Count + ExceptionRules.Count;
            TabWebsitesText.Text = total > 0 ? $"Websites ({total})" : "Websites";
        }
        if (TabScheduleText != null)
            TabScheduleText.Text = ScheduleRows.Count > 0 ? $"Schedule ({ScheduleRows.Count})" : "Schedule";
    }

    private void NavApps_Click(object sender, RoutedEventArgs e) => SelectTab("Apps");
    private void NavWebsites_Click(object sender, RoutedEventArgs e) => SelectTab("Websites");
    private void NavSchedule_Click(object sender, RoutedEventArgs e) => SelectTab("Schedule");
    private void NavLock_Click(object sender, RoutedEventArgs e) => SelectTab("Lock");

    public void SetValidationErrors(string? nameError, string? appError, string? domainError)
    {
        if (!string.IsNullOrEmpty(nameError))
        {
            NameErrorTextBlock.Text = nameError;
            NameErrorTextBlock.Visibility = Visibility.Visible;
        }
        if (!string.IsNullOrEmpty(appError))
        {
            AppErrorTextBlock.Text = appError;
            AppErrorTextBlock.Visibility = Visibility.Visible;
        }
        if (!string.IsNullOrEmpty(domainError))
        {
            BlockedPatternErrorTextBlock.Text = domainError;
            BlockedPatternErrorTextBlock.Visibility = Visibility.Visible;
        }
    }

    public void SelectTimedLockForTest(int minutes = 60)
    {
        SelectTab("Lock");
        if (rbTimedLock != null)
        {
            rbTimedLock.IsChecked = true;
        }
        if (TimedLockDurationInput != null && TimedLockUnitComboBox != null)
        {
            TimedLockDurationInput.Text = "1";
            TimedLockUnitComboBox.SelectedIndex = 2; // 1 Day
        }
    }

    #endregion
}
