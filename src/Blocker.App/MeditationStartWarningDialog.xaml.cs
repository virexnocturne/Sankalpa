using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Blocker.Core;

namespace Blocker.App;

public partial class MeditationStartWarningDialog : Window
{
    public bool DoNotShowAgain => DoNotShowAgainCheckBox.IsChecked == true;

    public MeditationStartWarningDialog(
        int durationMinutes,
        string lockRigor,
        IEnumerable<string>? allowedApps = null,
        Window? owner = null)
    {
        InitializeComponent();
        WindowHelper.EnableDarkMode(this);

        if (owner != null && owner.IsLoaded && owner.IsVisible)
        {
            try
            {
                Owner = owner;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            catch
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        SetDuration(durationMinutes);
        SetLockRigor(lockRigor);
        SetAllowedApps(allowedApps);
    }

    public MeditationStartWarningDialog(
        long durationSeconds,
        LockMode lockMode,
        IEnumerable<string>? allowedApps = null,
        Window? owner = null)
        : this((int)Math.Max(1, durationSeconds / 60), FormatLockRigor(lockMode), allowedApps, owner)
    {
    }

    public static string FormatLockRigor(LockMode lockMode) => lockMode switch
    {
        LockMode.Locked => "Complete Lock",
        LockMode.Password => "Password",
        LockMode.TypingChallenge => "Typing Challenge",
        LockMode.DelayedUnlock => "Delayed Unlock",
        LockMode.HardLocked => "Hard Locked",
        _ => "Standard"
    };

    public void SetDuration(int durationMinutes)
    {
        DurationValueText.Text = durationMinutes == 1 ? "1 minute" : $"{durationMinutes} minutes";
    }

    public void SetLockRigor(string lockRigor)
    {
        LockModeValueText.Text = string.IsNullOrWhiteSpace(lockRigor) ? "Standard" : lockRigor;
    }

    public void SetAllowedApps(IEnumerable<string>? allowedApps)
    {
        var appsList = allowedApps?.Where(a => !string.IsNullOrWhiteSpace(a)).ToList() ?? new List<string>();
        if (appsList.Count == 0)
        {
            AllowedAppsValueText.Text = "0 allowed apps — Full Screen Lockout";
        }
        else if (appsList.Count == 1)
        {
            AllowedAppsValueText.Text = $"1 app allowed ({appsList[0]})";
        }
        else
        {
            AllowedAppsValueText.Text = $"{appsList.Count} apps allowed ({string.Join(", ", appsList)})";
        }
    }

    private void StartSessionButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
