using System;
using System.Windows;
using System.Windows.Threading;

namespace Blocker.App;

public partial class MeditationScheduledWarningDialog : Window
{
    private readonly DispatcherTimer _countdownTimer;
    private int _secondsRemaining = 60;
    private bool _hasSnoozed;

    public bool Snoozed { get; private set; }
    public Action? OnSnoozeRequested { get; set; }

    public MeditationScheduledWarningDialog(int countdownSeconds = 60, bool canSnooze = true)
    {
        InitializeComponent();
        WindowHelper.EnableDarkMode(this);

        _secondsRemaining = Math.Max(1, countdownSeconds);
        CountdownProgressBar.Maximum = _secondsRemaining;
        CountdownProgressBar.Value = _secondsRemaining;

        if (!canSnooze)
        {
            SnoozeButton.IsEnabled = false;
            SnoozeButton.ToolTip = "Already snoozed. Cannot snooze again.";
        }

        UpdateMessage();

        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += CountdownTimer_Tick;

        Loaded += (_, _) => _countdownTimer.Start();
        Closed += (_, _) => _countdownTimer.Stop();
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        _secondsRemaining--;
        if (_secondsRemaining <= 0)
        {
            _countdownTimer.Stop();
            DialogResult = true;
            Close();
            return;
        }

        CountdownProgressBar.Value = _secondsRemaining;
        UpdateMessage();
    }

    private void UpdateMessage()
    {
        MessageTextBlock.Text = $"🧘 Scheduled Meditation session starts in {_secondsRemaining}s. Wrap up and save your work.";
    }

    private void SnoozeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_hasSnoozed) return;

        _hasSnoozed = true;
        Snoozed = true;
        _countdownTimer.Stop();

        OnSnoozeRequested?.Invoke();

        SnoozeButton.IsEnabled = false;
        SnoozeButton.Content = "Snoozed +5m";

        DialogResult = false;
        Close();
    }

    private void BeginNowButton_Click(object sender, RoutedEventArgs e)
    {
        _countdownTimer.Stop();
        DialogResult = true;
        Close();
    }
}
