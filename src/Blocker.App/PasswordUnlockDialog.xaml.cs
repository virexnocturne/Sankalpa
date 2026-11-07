using System;
using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Blocker.App;

public partial class PasswordUnlockDialog : Window
{
    public string ResultPassword { get; private set; } = string.Empty;

    /// <summary>
    /// Optional validator function to verify the password before closing the dialog.
    /// Returning false keeps the dialog open and displays an inline error message.
    /// </summary>
    public Func<string, bool>? ValidatePassword { get; set; }

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

        // Perform inline validation before setting DialogResult to keep modal frame healthy
        if (ValidatePassword != null)
        {
            if (!ValidatePassword(pwd))
            {
                SetError("Incorrect password. Access denied.");
                return;
            }
        }

        try
        {
            DialogResult = true;
        }
        catch
        {
            Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
