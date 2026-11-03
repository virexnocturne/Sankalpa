using System.Windows;
using System.Windows.Controls;
using Blocker.Core;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;

namespace Blocker.App;

public partial class StartSessionDialog : Window
{
    private readonly string _profileKey;
    private readonly string _settingsPath;
    private readonly UiSettings _uiSettings;

    public int DurationMinutes { get; private set; } = 25;
    public bool IsLocked { get; private set; } = true;
    public bool IsPomodoro { get; private set; } = false;
    public int PomodoroFocusMinutes { get; private set; } = 25;
    public int PomodoroBreakMinutes { get; private set; } = 5;
    public int PomodoroCycles { get; private set; } = 4;

    public StartSessionDialog(string profileKey, string profileName)
    {
        InitializeComponent();
        WindowHelper.EnableDarkMode(this);

        _profileKey = string.IsNullOrWhiteSpace(profileKey) ? profileName : profileKey;
        ProfileHeader.Text = $"Start Block: {profileName}";

        _settingsPath = UiSettingsStore.GetDefaultFilePath();
        _uiSettings = UiSettingsStore.LoadFromFile(_settingsPath);
        var remembered = UiSettingsStore.GetSettings(_uiSettings, _profileKey);

        DurationMinutes = remembered.DurationMinutes;
        IsLocked = remembered.Locked;

        if (remembered.DurationMinutes % 525600 == 0)
        {
            DurationInput.Text = Math.Max(1, remembered.DurationMinutes / 525600).ToString();
            DurationUnitComboBox.SelectedIndex = 5; // Years
        }
        else if (remembered.DurationMinutes % 43200 == 0)
        {
            DurationInput.Text = Math.Max(1, remembered.DurationMinutes / 43200).ToString();
            DurationUnitComboBox.SelectedIndex = 4; // Months
        }
        else if (remembered.DurationMinutes % 10080 == 0)
        {
            DurationInput.Text = Math.Max(1, remembered.DurationMinutes / 10080).ToString();
            DurationUnitComboBox.SelectedIndex = 3; // Weeks
        }
        else if (remembered.DurationMinutes % 1440 == 0)
        {
            DurationInput.Text = Math.Max(1, remembered.DurationMinutes / 1440).ToString();
            DurationUnitComboBox.SelectedIndex = 2; // Days
        }
        else if (remembered.DurationMinutes % 60 == 0)
        {
            DurationInput.Text = Math.Max(1, remembered.DurationMinutes / 60).ToString();
            DurationUnitComboBox.SelectedIndex = 1; // Hours
        }
        else
        {
            DurationInput.Text = Math.Max(1, remembered.DurationMinutes).ToString();
            DurationUnitComboBox.SelectedIndex = 0; // Minutes
        }

        LockedCheckBox.IsChecked = IsLocked;
    }

    public StartSessionDialog(string profileName) : this(profileName, profileName)
    {
    }

    private void QuickDuration_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr)
        {
            switch (tagStr)
            {
                case "1h":
                    DurationInput.Text = "1";
                    DurationUnitComboBox.SelectedIndex = 1;
                    break;
                case "4h":
                    DurationInput.Text = "4";
                    DurationUnitComboBox.SelectedIndex = 1;
                    break;
                case "1d":
                    DurationInput.Text = "1";
                    DurationUnitComboBox.SelectedIndex = 2;
                    break;
                case "3d":
                    DurationInput.Text = "3";
                    DurationUnitComboBox.SelectedIndex = 2;
                    break;
                case "1w":
                    DurationInput.Text = "1";
                    DurationUnitComboBox.SelectedIndex = 3;
                    break;
                case "1m":
                    DurationInput.Text = "1";
                    DurationUnitComboBox.SelectedIndex = 4;
                    break;
                case "3m":
                    DurationInput.Text = "3";
                    DurationUnitComboBox.SelectedIndex = 4;
                    break;
                case "1y":
                    DurationInput.Text = "1";
                    DurationUnitComboBox.SelectedIndex = 5;
                    break;
                default:
                    if (int.TryParse(tagStr, out int m))
                    {
                        DurationInput.Text = m.ToString();
                        DurationUnitComboBox.SelectedIndex = 0;
                    }
                    break;
            }
        }
    }

    private void SessionType_Changed(object sender, RoutedEventArgs e)
    {
        if (SprintPanel != null && PomodoroPanel != null)
        {
            bool isPomo = rbModePomodoro.IsChecked == true;
            SprintPanel.Visibility = isPomo ? Visibility.Collapsed : Visibility.Visible;
            PomodoroPanel.Visibility = isPomo ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        bool isPomo = rbModePomodoro?.IsChecked == true;
        int mins;

        if (isPomo)
        {
            if (!int.TryParse(PomoFocusInput?.Text?.Trim(), out int focusMins) || focusMins <= 0)
            {
                DarkDialogWindow.Show("Please enter a valid focus interval in minutes.", "Invalid Focus Duration", owner: this);
                return;
            }
            int.TryParse(PomoBreakInput?.Text?.Trim(), out int breakMins);
            int.TryParse(PomoCyclesInput?.Text?.Trim(), out int cycles);

            IsPomodoro = true;
            PomodoroFocusMinutes = focusMins;
            PomodoroBreakMinutes = Math.Max(1, breakMins);
            PomodoroCycles = Math.Max(1, cycles);
            mins = focusMins;
        }
        else
        {
            if (!int.TryParse(DurationInput.Text.Trim(), out int val) || val <= 0)
            {
                DarkDialogWindow.Show("Please enter a valid positive duration number.", "Invalid Duration", owner: this);
                return;
            }

            int multiplier = DurationUnitComboBox?.SelectedIndex switch
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
                checked { mins = val * multiplier; }
            }
            catch (OverflowException)
            {
                mins = int.MaxValue;
            }
            IsPomodoro = false;
        }

        bool isLocked = LockedCheckBox.IsChecked == true;

        if (isLocked)
        {
            bool confirmed = DarkDialogWindow.Show(
                "Locked sessions cannot be stopped early. Are you sure?",
                "Confirm Locked Session",
                isConfirmation: true,
                primaryText: "Start",
                secondaryText: "Cancel",
                owner: this);

            if (!confirmed)
            {
                return;
            }
        }

        DurationMinutes = mins;
        IsLocked = isLocked;

        // Remember settings for this profile
        UiSettingsStore.SetSettings(_uiSettings, _profileKey, mins, isLocked);
        UiSettingsStore.SaveToFile(_uiSettings, _settingsPath);

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
