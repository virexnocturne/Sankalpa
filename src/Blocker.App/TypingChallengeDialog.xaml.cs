using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Blocker.App;

public partial class TypingChallengeDialog : Window
{
    public string ChallengeText { get; }
    public string ResultText { get; private set; } = string.Empty;

    public TypingChallengeDialog(string challengeText, string? promptMessage = null)
    {
        InitializeComponent();
        ChallengeText = challengeText ?? string.Empty;
        ChallengeTextBlock.Text = ChallengeText;
        if (!string.IsNullOrWhiteSpace(promptMessage))
        {
            PromptTextBlock.Text = promptMessage;
        }

        // Disable paste, cut, copy commands
        CommandManager.AddPreviewExecutedHandler(InputTextBox, (s, e) =>
        {
            if (e.Command == ApplicationCommands.Paste ||
                e.Command == ApplicationCommands.Cut ||
                e.Command == ApplicationCommands.Copy)
            {
                e.Handled = true;
            }
        });

        // Disable context menu and drag drop
        InputTextBox.ContextMenu = null;
        InputTextBox.AllowDrop = false;

        Loaded += (s, e) =>
        {
            InputTextBox.Focus();
        };
    }

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        int count = InputTextBox.Text.Length;
        CharCountText.Text = $"{count} / {ChallengeText.Length}";

        if (count == ChallengeText.Length)
        {
            if (string.Equals(InputTextBox.Text, ChallengeText, StringComparison.Ordinal))
            {
                ErrorTextBlock.Visibility = Visibility.Collapsed;
            }
            else
            {
                ErrorTextBlock.Text = "Text does not match challenge. Please verify each character.";
                ErrorTextBlock.Visibility = Visibility.Visible;
            }
        }
        else
        {
            ErrorTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(InputTextBox.Text, ChallengeText, StringComparison.Ordinal))
        {
            ErrorTextBlock.Text = "Challenge text does not match exactly.";
            ErrorTextBlock.Visibility = Visibility.Visible;
            InputTextBox.Focus();
            return;
        }

        ResultText = InputTextBox.Text;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
