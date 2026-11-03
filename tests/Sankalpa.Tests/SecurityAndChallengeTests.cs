using System;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class SecurityAndChallengeTests
{
    [Fact]
    public void PasswordSecurity_HashAndVerify_SucceedsWithCorrectPassword()
    {
        PasswordSecurity.HashPassword("Secret123!", out string salt, out string hash);
        Assert.NotNull(salt);
        Assert.NotNull(hash);

        Assert.True(PasswordSecurity.VerifyPassword("Secret123!", salt, hash));
        Assert.False(PasswordSecurity.VerifyPassword("WrongPassword", salt, hash));
    }

    [Fact]
    public void PasswordSecurity_GeneratesUniqueSaltsAndHashes()
    {
        PasswordSecurity.HashPassword("Test", out string salt1, out string hash1);
        PasswordSecurity.HashPassword("Test", out string salt2, out string hash2);

        Assert.NotEqual(salt1, salt2);
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void PasswordSecurity_Lockout_After5Failures()
    {
        PasswordSecurity.ResetAllLockouts();
        string profileKey = "test_profile_" + Guid.NewGuid().ToString("N");

        for (int i = 0; i < 4; i++)
        {
            PasswordSecurity.RecordFailedAttempt(profileKey);
            Assert.False(PasswordSecurity.IsLockedOut(profileKey, out _));
        }

        // 5th attempt triggers lockout
        PasswordSecurity.RecordFailedAttempt(profileKey);
        Assert.True(PasswordSecurity.IsLockedOut(profileKey, out int remaining));
        Assert.InRange(remaining, 1, 60);

        // Reset
        PasswordSecurity.RecordSuccessfulAttempt(profileKey);
        Assert.False(PasswordSecurity.IsLockedOut(profileKey, out _));
    }

    [Fact]
    public void TypingChallengeManager_Generates80CharUnambiguousString()
    {
        string profileKey = "prof1";
        string challenge = TypingChallengeManager.GenerateChallenge(profileKey);

        Assert.Equal(80, challenge.Length);
        Assert.DoesNotContain('0', challenge);
        Assert.DoesNotContain('O', challenge);
        Assert.DoesNotContain('1', challenge);
        Assert.DoesNotContain('l', challenge);
        Assert.DoesNotContain('I', challenge);
    }

    [Fact]
    public void TypingChallengeManager_ValidatesExactMatch_AndIsSingleUse()
    {
        string profileKey = "prof2";
        string challenge = TypingChallengeManager.GenerateChallenge(profileKey);

        // Wrong text fails
        Assert.False(TypingChallengeManager.ValidateChallenge(profileKey, "wrong text", out var err1));
        Assert.NotNull(err1);

        // Challenge was consumed/single-use
        Assert.False(TypingChallengeManager.ValidateChallenge(profileKey, challenge, out var err2));
        Assert.Contains("No active challenge found", err2);

        // Fresh challenge with exact text succeeds
        string challenge2 = TypingChallengeManager.GenerateChallenge(profileKey);
        Assert.True(TypingChallengeManager.ValidateChallenge(profileKey, challenge2, out var err3));
        Assert.Null(err3);

        // Consumed again
        Assert.False(TypingChallengeManager.ValidateChallenge(profileKey, challenge2, out _));
    }
}
