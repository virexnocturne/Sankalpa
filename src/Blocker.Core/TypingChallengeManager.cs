using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Blocker.Core;

public static class TypingChallengeManager
{
    // 80 characters from unambiguous alphabet (excludes 0, O, 1, l, I)
    private const string Alphabet = "23456789abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string DigitsAlphabet = "0123456789";
    private const int ChallengeLength = 80;
    private static readonly TimeSpan ExpiryDuration = TimeSpan.FromMinutes(10);

    private class ChallengeEntry
    {
        public string Text { get; set; } = string.Empty;
        public DateTime ExpiresUtc { get; set; }
    }

    private static readonly ConcurrentDictionary<string, ChallengeEntry> Challenges = new(StringComparer.OrdinalIgnoreCase);

    public static string GenerateNumericChallenge(string profileKey, int length = 64)
    {
        if (string.IsNullOrWhiteSpace(profileKey))
        {
            throw new ArgumentException("Profile key cannot be empty.", nameof(profileKey));
        }

        int targetLen = length > 0 ? length : 64;
        var sb = new StringBuilder(targetLen);
        byte[] randomBytes = new byte[targetLen];
        RandomNumberGenerator.Fill(randomBytes);

        for (int i = 0; i < targetLen; i++)
        {
            int index = randomBytes[i] % DigitsAlphabet.Length;
            sb.Append(DigitsAlphabet[index]);
        }

        string challengeText = sb.ToString();

        Challenges[profileKey] = new ChallengeEntry
        {
            Text = challengeText,
            ExpiresUtc = DateTime.UtcNow.Add(ExpiryDuration)
        };

        return challengeText;
    }

    public static string GenerateChallenge(string profileKey, int length = ChallengeLength)
    {
        if (string.IsNullOrWhiteSpace(profileKey))
        {
            throw new ArgumentException("Profile key cannot be empty.", nameof(profileKey));
        }

        int targetLen = length > 0 ? length : ChallengeLength;
        var sb = new StringBuilder(targetLen);
        byte[] randomBytes = new byte[targetLen];
        RandomNumberGenerator.Fill(randomBytes);

        for (int i = 0; i < targetLen; i++)
        {
            int index = randomBytes[i] % Alphabet.Length;
            sb.Append(Alphabet[index]);
        }

        string challengeText = sb.ToString();

        Challenges[profileKey] = new ChallengeEntry
        {
            Text = challengeText,
            ExpiresUtc = DateTime.UtcNow.Add(ExpiryDuration)
        };

        return challengeText;
    }

    public static bool ValidateChallenge(string profileKey, string? responseText, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(profileKey))
        {
            error = "Profile key cannot be empty.";
            return false;
        }

        if (!Challenges.TryRemove(profileKey, out var entry))
        {
            error = "No active challenge found. Request a new challenge first.";
            return false;
        }

        if (DateTime.UtcNow > entry.ExpiresUtc)
        {
            error = "Challenge expired. Please request a new challenge.";
            return false;
        }

        if (!string.Equals(entry.Text, responseText, StringComparison.Ordinal))
        {
            error = "Challenge text did not match exactly.";
            return false;
        }

        return true;
    }

    public static void Clear(string profileKey)
    {
        Challenges.TryRemove(profileKey, out _);
    }

    public static void ClearAll()
    {
        Challenges.Clear();
    }
}
