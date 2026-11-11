using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using Blocker.Core;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Blocker.App;

public partial class PasswordUnlockDialog : Window
{
    private PasswordVerificationCoordinator? _coordinator;
    private Func<string, bool>? _coordinatorValidator;
    private CancellationTokenSource? _verifyCts;
    private bool _isVerifying;
    private bool _isClosingOrCancelling;

    public string ResultPassword { get; private set; } = string.Empty;

    /// <summary>
    /// Authoritative verification flag. Set to true ONLY when <see cref="ValidatePassword"/>
    /// executes cleanly and explicitly returns true for the submitted attempt.
    /// Callers using local verification MUST check both DialogResult == true and IsVerified.
    /// </summary>
    public bool IsVerified { get; private set; }

    /// <summary>
    /// Optional validator function to verify the password before closing the dialog.
    /// Returning false keeps the dialog open and displays an inline error message.
    /// </summary>
    public Func<string, bool>? ValidatePassword { get; set; }

    /// <summary>
    /// Bounded timeout applied to each async verification attempt (fail-closed on timeout).
    /// </summary>
    public TimeSpan VerificationTimeout { get; set; } = PasswordVerificationCoordinator.DefaultTimeout;

    public PasswordUnlockDialog(string profileName)
    {
        InitializeComponent();
        PromptTextBlock.Text = $"Profile '{profileName}' is protected with a password. Enter your password to continue:";

        Closing += PasswordUnlockDialog_Closing;
        Loaded += (s, e) =>
        {
            PasswordInput.Focus();
        };
    }

    private void PasswordUnlockDialog_Closing(object? sender, CancelEventArgs e)
    {
        // Fail closed if DialogResult == true is somehow observed while a validator exists without verification.
        if (DialogResult == true && ValidatePassword != null && !IsVerified)
        {
            e.Cancel = true;
            IsVerified = false;
            SetError("Incorrect password. Access denied.");
            return;
        }

        _isClosingOrCancelling = true;
        if (!IsVerified || DialogResult != true)
        {
            IsVerified = false;
        }
        try { _verifyCts?.Cancel(); } catch { }
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

    private async void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isVerifying || _isClosingOrCancelling)
        {
            return;
        }

        string pwd = PasswordInput.Password;
        if (string.IsNullOrEmpty(pwd))
        {
            SetError("Password cannot be empty.");
            return;
        }

        IsVerified = false;
        ResultPassword = pwd;

        var validator = ValidatePassword;
        if (validator == null)
        {
            try
            {
                DialogResult = true;
            }
            catch
            {
                Close();
            }
            return;
        }

        if (_coordinator == null || !ReferenceEquals(_coordinatorValidator, validator))
        {
            _coordinatorValidator = validator;
            _coordinator = new PasswordVerificationCoordinator(validator, VerificationTimeout);
        }

        _isVerifying = true;
        UnlockButton.IsEnabled = false;
        UnlockButton.Content = "Verifying...";
        PasswordInput.IsEnabled = false;
        ErrorTextBlock.Visibility = Visibility.Collapsed;

        _verifyCts?.Dispose();
        _verifyCts = new CancellationTokenSource();

        PasswordVerificationAttemptResult result;
        try
        {
            result = await _coordinator.VerifyAttemptAsync(pwd, _verifyCts.Token);
        }
        catch
        {
            // Fail closed on any unexpected UI dispatcher/marshaling error
            result = new PasswordVerificationAttemptResult(false, false, false, "Verification failed. Access denied.");
        }
        finally
        {
            _isVerifying = false;
            UnlockButton.IsEnabled = true;
            UnlockButton.Content = "Unlock";
            PasswordInput.IsEnabled = true;
        }

        if (_isClosingOrCancelling || !IsVisible)
        {
            IsVerified = false;
            return;
        }

        if (result.IgnoredInFlight)
        {
            return;
        }

        if (result.Succeeded && _coordinator.IsVerified)
        {
            IsVerified = true;
            try
            {
                DialogResult = true;
            }
            catch
            {
                IsVerified = false;
                Close();
            }
        }
        else
        {
            IsVerified = false;
            SetError(result.ErrorMessage ?? "Incorrect password. Access denied.");
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _isClosingOrCancelling = true;
        IsVerified = false;
        _coordinator?.Reset();
        try { _verifyCts?.Cancel(); } catch { }
        try { DialogResult = false; } catch { }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosingOrCancelling = true;
        if (!IsVerified)
        {
            _coordinator?.Reset();
        }
        try { _verifyCts?.Cancel(); } catch { }
        _verifyCts?.Dispose();
        _verifyCts = null;
        base.OnClosed(e);
    }
}
