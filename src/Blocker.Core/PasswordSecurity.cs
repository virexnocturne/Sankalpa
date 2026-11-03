using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Blocker.Core;

public static class PasswordSecurity
{
    private const int Iterations = 100_000;
    private const int SaltByteSize = 16;
    private const int HashByteSize = 32;
    private const int MaxFailedAttempts = 5;
    private const int LockoutDurationSeconds = 60;

    private class LockoutState
    {
        public int FailedAttempts { get; set; }
        public DateTime? LockoutUntilUtc { get; set; }
    }

    private static readonly ConcurrentDictionary<string, LockoutState> Lockouts = new(StringComparer.OrdinalIgnoreCase);

    public static void HashPassword(string password, out string saltBase64, out string hashBase64)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password cannot be empty.", nameof(password));
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltByteSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashByteSize);

        saltBase64 = Convert.ToBase64String(salt);
        hashBase64 = Convert.ToBase64String(hash);
    }

    public static bool VerifyPassword(string password, string? saltBase64, string? hashBase64)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(saltBase64) || string.IsNullOrEmpty(hashBase64))
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(saltBase64);
            byte[] expectedHash = Convert.FromBase64String(hashBase64);

            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                HashByteSize);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsLockedOut(string profileKey, out int remainingSeconds)
    {
        remainingSeconds = 0;
        if (string.IsNullOrWhiteSpace(profileKey)) return false;

        if (Lockouts.TryGetValue(profileKey, out var state) && state.LockoutUntilUtc.HasValue)
        {
            lock (state)
            {
                if (state.LockoutUntilUtc.HasValue)
                {
                    DateTime now = DateTime.UtcNow;
                    if (now < state.LockoutUntilUtc.Value)
                    {
                        remainingSeconds = (int)Math.Ceiling((state.LockoutUntilUtc.Value - now).TotalSeconds);
                        return true;
                    }
                    else
                    {
                        // Lockout expired: reset failed attempts
                        state.LockoutUntilUtc = null;
                        state.FailedAttempts = 0;
                    }
                }
            }
        }

        return false;
    }

    public static void RecordFailedAttempt(string profileKey)
    {
        if (string.IsNullOrWhiteSpace(profileKey)) return;

        var state = Lockouts.GetOrAdd(profileKey, _ => new LockoutState());
        lock (state)
        {
            state.FailedAttempts++;
            if (state.FailedAttempts >= MaxFailedAttempts)
            {
                state.LockoutUntilUtc = DateTime.UtcNow.AddSeconds(LockoutDurationSeconds);
            }
        }
    }

    public static void RecordSuccessfulAttempt(string profileKey)
    {
        if (string.IsNullOrWhiteSpace(profileKey)) return;

        if (Lockouts.TryGetValue(profileKey, out var state))
        {
            lock (state)
            {
                state.FailedAttempts = 0;
                state.LockoutUntilUtc = null;
            }
        }
    }

    public static void ResetAllLockouts()
    {
        Lockouts.Clear();
    }
}
