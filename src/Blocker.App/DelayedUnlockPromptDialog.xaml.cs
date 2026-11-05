using System.Windows;

namespace Blocker.App;

public partial class DelayedUnlockPromptDialog : Window
{
    public bool Confirmed { get; private set; }

    public DelayedUnlockPromptDialog(string profileName, int minutes)
    {
        InitializeComponent();
        int displayMins = minutes >= 15 ? minutes : 15;
        PromptTextBlock.Text = $"To unlock '{profileName}', you must wait for {displayMins} minutes with the timer active in the foreground.";
    }

    private void StartTimerButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }
}
