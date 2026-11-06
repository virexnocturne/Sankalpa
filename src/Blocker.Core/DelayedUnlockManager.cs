using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Blocker.Core;

public static class DelayedUnlockManager
{
    private class DelayedUnlockEntry
    {
        public string ProfileName { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public DateTime StartedUtc { get; set; }
        public int DurationSeconds { get; set; }
        public DateTime CanUnlockAfterUtc { get; set; }
    }

    private static readonly ConcurrentDictionary<string, DelayedUnlockEntry> ActiveUnlocks = new(StringComparer.OrdinalIgnoreCase);

    public static string StartDelayedUnlock(string profileName, int durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new ArgumentException("Profile name cannot be empty.", nameof(profileName));
        }

        if (durationSeconds < 1)
        {
            durationSeconds = 900; // Default 15 minutes
        }

        byte[] tokenBytes = new byte[24];
        RandomNumberGenerator.Fill(tokenBytes);
        string token = Convert.ToHexString(tokenBytes).ToLowerInvariant();

        DateTime now = DateTime.UtcNow;
        var entry = new DelayedUnlockEntry
        {
            ProfileName = profileName.Trim(),
            Token = token,
            StartedUtc = now,
            DurationSeconds = durationSeconds,
            CanUnlockAfterUtc = now.AddSeconds(durationSeconds)
        };

        ActiveUnlocks[entry.ProfileName] = entry;
        return token;
    }

    public static bool CancelDelayedUnlock(string profileName, string token)
    {
        if (string.IsNullOrWhiteSpace(profileName) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        string key = profileName.Trim();
        if (ActiveUnlocks.TryGetValue(key, out var entry))
        {
            if (string.Equals(entry.Token, token.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return ActiveUnlocks.TryRemove(key, out _);
            }
        }

        return false;
    }

    public static bool ValidateUnlock(string profileName, string? token, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(profileName))
        {
            error = "Profile name cannot be empty.";
            return false;
        }

        string key = profileName.Trim();
        if (!ActiveUnlocks.TryGetValue(key, out var entry))
        {
            error = $"No active delayed unlock timer found for '{profileName}'. Please initiate the wait timer first.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(token) || !string.Equals(entry.Token, token, StringComparison.OrdinalIgnoreCase))
        {
            error = "Invalid or expired delayed unlock token.";
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (now < entry.CanUnlockAfterUtc)
        {
            int remaining = (int)Math.Ceiling((entry.CanUnlockAfterUtc - now).TotalSeconds);
            error = $"Refused: Delayed unlock timer is still running ({remaining} seconds remaining).";
            return false;
        }

        // Successfully validated - remove active token to prevent reuse
        ActiveUnlocks.TryRemove(key, out _);
        return true;
    }

    public static bool IsDelayedUnlockActive(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return false;
        return ActiveUnlocks.ContainsKey(profileName.Trim());
    }

    public static int GetRemainingSeconds(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return 0;
        if (ActiveUnlocks.TryGetValue(profileName.Trim(), out var entry))
        {
            DateTime now = DateTime.UtcNow;
            if (now >= entry.CanUnlockAfterUtc) return 0;
            return (int)Math.Ceiling((entry.CanUnlockAfterUtc - now).TotalSeconds);
        }
        return 0;
    }

    public static void ResetAll()
    {
        ActiveUnlocks.Clear();
    }
}
