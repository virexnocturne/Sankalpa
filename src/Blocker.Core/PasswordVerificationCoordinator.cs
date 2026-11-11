using System;
using System.Threading;
using System.Threading.Tasks;

namespace Blocker.Core;

/// <summary>
/// Result of a single password verification attempt.
/// </summary>
public readonly record struct PasswordVerificationAttemptResult(
    bool Succeeded,
    bool IgnoredInFlight,
    bool TimedOut,
    string? ErrorMessage);

/// <summary>
/// Coordinates single-execution, off-UI-thread, fail-closed password verification per attempt.
/// Ensures validation runs at most once per click/submit, tracks a read-only verified flag
/// updated only when the validator explicitly returns true, and fails closed on errors or timeouts.
/// </summary>
public sealed class PasswordVerificationCoordinator
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<string, bool> _validator;
    private readonly TimeSpan _timeout;
    private int _inFlight;
    private int _verificationCallCount;

    public bool IsVerified { get; private set; }
    public bool IsVerifying => Volatile.Read(ref _inFlight) != 0;
    public int VerificationCallCount => Volatile.Read(ref _verificationCallCount);

    public PasswordVerificationCoordinator(Func<string, bool> validator, TimeSpan? timeout = null)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Resets any prior verified state (e.g., if dialog is cancelled).
    /// </summary>
    public void Reset()
    {
        IsVerified = false;
    }

    /// <summary>
    /// Verifies <paramref name="password"/> exactly once on a background thread with bounded timeout.
    /// Never leaks passwords/hashes in errors and always fails closed on false return, exception,
    /// cancellation, or timeout.
    /// </summary>
    public async Task<PasswordVerificationAttemptResult> VerifyAttemptAsync(
        string? password,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            return new PasswordVerificationAttemptResult(
                Succeeded: false,
                IgnoredInFlight: true,
                TimedOut: false,
                ErrorMessage: null);
        }

        IsVerified = false;

        try
        {
            if (string.IsNullOrEmpty(password))
            {
                return new PasswordVerificationAttemptResult(
                    Succeeded: false,
                    IgnoredInFlight: false,
                    TimedOut: false,
                    ErrorMessage: "Password cannot be empty.");
            }

            bool matched;
            try
            {
                var verifyTask = Task.Run(() =>
                {
                    Interlocked.Increment(ref _verificationCallCount);
                    return _validator(password);
                }, cancellationToken);

                matched = await verifyTask.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                IsVerified = false;
                return new PasswordVerificationAttemptResult(
                    Succeeded: false,
                    IgnoredInFlight: false,
                    TimedOut: true,
                    ErrorMessage: "Verification timed out. Access denied.");
            }
            catch (OperationCanceledException)
            {
                IsVerified = false;
                return new PasswordVerificationAttemptResult(
                    Succeeded: false,
                    IgnoredInFlight: false,
                    TimedOut: false,
                    ErrorMessage: "Verification cancelled.");
            }
            catch
            {
                // Fail closed on any unexpected verification error without logging secrets
                IsVerified = false;
                return new PasswordVerificationAttemptResult(
                    Succeeded: false,
                    IgnoredInFlight: false,
                    TimedOut: false,
                    ErrorMessage: "Verification failed. Access denied.");
            }

            if (matched && !cancellationToken.IsCancellationRequested)
            {
                IsVerified = true;
                return new PasswordVerificationAttemptResult(
                    Succeeded: true,
                    IgnoredInFlight: false,
                    TimedOut: false,
                    ErrorMessage: null);
            }

            IsVerified = false;
            return new PasswordVerificationAttemptResult(
                Succeeded: false,
                IgnoredInFlight: false,
                TimedOut: false,
                ErrorMessage: "Incorrect password. Access denied.");
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }
}
