using System;
using System.Windows;
using System.Windows.Threading;

namespace Blocker.App;

public partial class CoolDownTimerWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _remainingSeconds;
    private bool _isClosingFromCode;

    public bool Completed { get; private set; }
    public bool Cancelled { get; private set; }
    public string? CancellationReason { get; private set; }

    public CoolDownTimerWindow(string profileName, int totalSeconds)
    {
        InitializeComponent();

        ProfileNameText.Text = $"Unlocking '{profileName}'";
        _remainingSeconds = totalSeconds > 0 ? totalSeconds : 900;
        UpdateDisplay();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;

        Loaded += (s, e) =>
        {
            _timer.Start();
            Activate();
        };

        StateChanged += CoolDownTimerWindow_StateChanged;
        Deactivated += CoolDownTimerWindow_Deactivated;
        Closing += CoolDownTimerWindow_Closing;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingSeconds--;
        if (_remainingSeconds <= 0)
        {
            _remainingSeconds = 0;
            UpdateDisplay();
            _timer.Stop();
            Completed = true;
            _isClosingFromCode = true;
            DialogResult = true;
            Close();
            return;
        }

        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        int hours = _remainingSeconds / 3600;
        int minutes = (_remainingSeconds % 3600) / 60;
        int seconds = _remainingSeconds % 60;

        TimerDisplay.Text = hours > 0
            ? $"{hours:D2}:{minutes:D2}:{seconds:D2}"
            : $"{minutes:D2}:{seconds:D2}";
    }

    private void CoolDownTimerWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && !Completed && !_isClosingFromCode)
        {
            TriggerCancellation("Unlock cancelled because the timer window was minimized.");
        }
    }

    private void CoolDownTimerWindow_Deactivated(object? sender, EventArgs e)
    {
        // Deactivated fires when another application or window is clicked / brought to foreground
        if (!Completed && !_isClosingFromCode && !Cancelled)
        {
            TriggerCancellation("Unlock cancelled because another window was overlayed or clicked.");
        }
    }

    private void CoolDownTimerWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!Completed && !Cancelled)
        {
            Cancelled = true;
            CancellationReason = "Unlock cancelled because the timer was closed.";
            _timer.Stop();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        TriggerCancellation("Focus preserved! You chose to stay focused.");
    }

    private void TriggerCancellation(string reason)
    {
        if (Cancelled || Completed) return;

        Cancelled = true;
        CancellationReason = reason;
        _timer.Stop();

        if (!_isClosingFromCode)
        {
            _isClosingFromCode = true;
            try
            {
                DialogResult = false;
            }
            catch { }

            Close();
        }
    }
}
