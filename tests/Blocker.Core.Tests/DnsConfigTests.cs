using System;
using System.Linq;
using System.Text.Json;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class DnsConfigTests
{
    [Fact]
    public void DnsPresets_ContainsRequiredSafePresets()
    {
        var presets = DnsPresets.All;
        Assert.NotNull(presets);
        Assert.NotEmpty(presets);

        // Verify presets exist
        var cloudflare = presets.FirstOrDefault(p => p.Name.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(cloudflare);
        Assert.Equal("1.1.1.3", cloudflare.Primary);
        Assert.Equal("1.0.0.3", cloudflare.Secondary);

        var cleanBrowsing = presets.FirstOrDefault(p => p.Name.Contains("CleanBrowsing", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(cleanBrowsing);
        Assert.Equal("185.228.168.168", cleanBrowsing.Primary);
        Assert.Equal("185.228.169.168", cleanBrowsing.Secondary);

        var adguard = presets.FirstOrDefault(p => p.Name.Contains("AdGuard", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(adguard);
        Assert.Equal("94.140.14.15", adguard.Primary);
        Assert.Equal("94.140.15.16", adguard.Secondary);

        var quad9 = presets.FirstOrDefault(p => p.Name.Contains("Quad9", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(quad9);
        Assert.Equal("9.9.9.9", quad9.Primary);
        Assert.Equal("149.112.112.112", quad9.Secondary);

        var custom = presets.FirstOrDefault(p => p.Name.Equals("Custom", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(custom);
    }

    [Fact]
    public void DnsConfig_Serialization_RoundTripsCorrectly()
    {
        var config = new DnsConfig
        {
            Enabled = true,
            PresetName = "CleanBrowsing",
            PrimaryDns = "185.228.168.168",
            SecondaryDns = "185.228.169.168",
            Locked = true,
            LockType = "Timer",
            LockDurationSeconds = 7200,
            LockedUntilUtc = DateTime.UtcNow.AddHours(2)
        };

        string json = JsonSerializer.Serialize(config);
        var deserialized = JsonSerializer.Deserialize<DnsConfig>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Enabled);
        Assert.Equal("CleanBrowsing", deserialized.PresetName);
        Assert.Equal("185.228.168.168", deserialized.PrimaryDns);
        Assert.Equal("185.228.169.168", deserialized.SecondaryDns);
        Assert.True(deserialized.Locked);
        Assert.Equal("Timer", deserialized.LockType);
        Assert.Equal(7200, deserialized.LockDurationSeconds);
        Assert.NotNull(deserialized.LockedUntilUtc);
    }

    [Fact]
    public void TypingChallenge_CustomLength64_GeneratesAndValidatesCorrectly()
    {
        string challengeKey = "TEST_CHALLENGE_" + Guid.NewGuid();
        string challenge = TypingChallengeManager.GenerateChallenge(challengeKey, length: 64);

        Assert.Equal(64, challenge.Length);

        // Ambiguous characters like 0, O, 1, I, l should not appear
        Assert.DoesNotContain("0", challenge);
        Assert.DoesNotContain("O", challenge);
        Assert.DoesNotContain("1", challenge);
        Assert.DoesNotContain("I", challenge);
        Assert.DoesNotContain("l", challenge);

        // Validation with wrong text fails and consumes challenge
        Assert.False(TypingChallengeManager.ValidateChallenge(challengeKey, "incorrect", out _));

        // Challenge was consumed
        Assert.False(TypingChallengeManager.ValidateChallenge(challengeKey, challenge, out _));

        // Generate fresh challenge and validate with exact text
        string freshChallenge = TypingChallengeManager.GenerateChallenge(challengeKey, length: 64);
        Assert.Equal(64, freshChallenge.Length);
        Assert.True(TypingChallengeManager.ValidateChallenge(challengeKey, freshChallenge, out _));

        // Once consumed, cannot be reused
        Assert.False(TypingChallengeManager.ValidateChallenge(challengeKey, freshChallenge, out _));
    }

    [Fact]
    public void GenerateNumericChallenge_Generates64DigitsAndValidates()
    {
        string challengeKey = "NUMERIC_CHALLENGE_" + Guid.NewGuid();
        string challenge = TypingChallengeManager.GenerateNumericChallenge(challengeKey, length: 64);

        Assert.Equal(64, challenge.Length);
        Assert.All(challenge, c => Assert.True(char.IsDigit(c)));

        Assert.True(TypingChallengeManager.ValidateChallenge(challengeKey, challenge, out _));
    }
}
