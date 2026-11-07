using System.Windows;
using Application = System.Windows.Application;

namespace Blocker.App;

public partial class DarkDialogWindow : Window
{
    public DarkDialogWindow()
    {
        InitializeComponent();
        WindowHelper.EnableDarkMode(this);
    }

    public bool DontShowAgainChecked => DontShowAgainCheckBox.IsChecked == true;

    public static DarkDialogWindow CreateDialogWindow(
        string message,
        string title = "Blocker",
        bool isConfirmation = false,
        string primaryText = "OK",
        string secondaryText = "Cancel",
        bool showDontShowAgain = false)
    {
        var win = new DarkDialogWindow
        {
            Title = title
        };
        win.TitleTextBlock.Text = title;
        win.MessageTextBlock.Text = message;
        win.PrimaryButton.Content = primaryText;
        win.SecondaryButton.Content = secondaryText;

        if (isConfirmation)
        {
            win.SecondaryButton.Visibility = Visibility.Visible;
        }
        else
        {
            win.SecondaryButton.Visibility = Visibility.Collapsed;
        }

        win.DontShowAgainCheckBox.Visibility = showDontShowAgain ? Visibility.Visible : Visibility.Collapsed;

        return win;
    }

    public static bool Show(
        string message,
        string title = "Blocker",
        bool isConfirmation = false,
        string primaryText = "OK",
        string secondaryText = "Cancel",
        Window? owner = null,
        bool showDontShowAgain = false,
        Action<bool>? onDontShowAgain = null)
    {
        var win = CreateDialogWindow(message, title, isConfirmation, primaryText, secondaryText, showDontShowAgain);

        var targetOwner = owner ?? Application.Current?.MainWindow;
        if (targetOwner != null && targetOwner != win && targetOwner.IsLoaded && targetOwner.IsVisible)
        {
            try
            {
                win.Owner = targetOwner;
                if (targetOwner.Topmost)
                {
                    win.Topmost = true;
                }
            }
            catch
            {
                // Fallback if owner window handle is not yet available
            }
        }
        else if (Application.Current != null)
        {
            // If any active window in the app is Topmost (e.g. ZenShieldWindow), ensure dialog is also Topmost
            foreach (Window w in Application.Current.Windows)
            {
                if (w != win && w.IsVisible && w.Topmost)
                {
                    win.Topmost = true;
                    break;
                }
            }
        }
        bool result = win.ShowDialog() == true;
        if (result && showDontShowAgain && win.DontShowAgainChecked)
        {
            onDontShowAgain?.Invoke(true);
        }
        return result;
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
