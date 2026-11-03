using System;
using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Blocker.App;

public partial class PasswordUnlockDialog : Window
{
    public string ResultPassword { get; private set; } = string.Empty;

    public PasswordUnlockDialog(string profileName)
    {
        InitializeComponent();
        PromptTextBlock.Text = $"Profile '{profileName}' is protected with a password. Enter your password to continue:";

        Loaded += (s, e) =>
        {
            PasswordInput.Focus();
        };
    }

    public void SetError(string error)
    {
        ErrorTextBlock.Text = error;
        ErrorTextBlock.Visibility = Visibility.Visible;
        PasswordInput.SelectAll();
        PasswordInput.Focus();
    }

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            UnlockButton_Click(this, new RoutedEventArgs());
        }
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        string pwd = PasswordInput.Password;
        if (string.IsNullOrEmpty(pwd))
        {
            SetError("Password cannot be empty.");
            return;
        }

        ResultPassword = pwd;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
