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

    [Fact]
    public void IPAddressEquals_ComparesIPv4AndIPv6_Correctly()
    {
        Assert.True(DnsManager.IPAddressEquals("1.1.1.3", "1.1.1.3"));
        Assert.True(DnsManager.IPAddressEquals("1.1.1.3", " 1.1.1.3 "));
        Assert.False(DnsManager.IPAddressEquals("1.1.1.3", "1.0.0.3"));
        Assert.False(DnsManager.IPAddressEquals("1.1.1.3", "8.8.8.8"));

        // IPv6 normalization (shorthand vs expanded)
        Assert.True(DnsManager.IPAddressEquals("2606:4700:4700::1113", "2606:4700:4700:0:0:0:0:1113"));
        Assert.True(DnsManager.IPAddressEquals("2606:4700:4700::1113", "2606:4700:4700::1113"));
        Assert.False(DnsManager.IPAddressEquals("2606:4700:4700::1113", "2606:4700:4700::1003"));

        // Null / empty / invalid
        Assert.False(DnsManager.IPAddressEquals(null, "1.1.1.3"));
        Assert.False(DnsManager.IPAddressEquals("1.1.1.3", ""));
        Assert.False(DnsManager.IPAddressEquals("   ", "   "));
    }

    [Fact]
    public void IsVirtualOrExcludedAdapter_IdentifiesVirtualAdapters()
    {
        // Virtual host-only adapters that must not hijack DNS checks
        Assert.True(DnsManager.IsVirtualOrExcludedAdapter("VirtualBox Host-Only Ethernet Adapter", "Ethernet 4", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet));
        Assert.True(DnsManager.IsVirtualOrExcludedAdapter("VMware Network Adapter VMnet1", "VMnet1", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet));
        Assert.True(DnsManager.IsVirtualOrExcludedAdapter("Microsoft Wi-Fi Direct Virtual Adapter", "Local Area Connection* 1", System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211));
        Assert.True(DnsManager.IsVirtualOrExcludedAdapter("Hyper-V Virtual Ethernet Adapter", "vEthernet (WSL)", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet));

        // Loopback and tunnel
        Assert.True(DnsManager.IsVirtualOrExcludedAdapter("Software Loopback Interface", "Loopback", System.Net.NetworkInformation.NetworkInterfaceType.Loopback));
        Assert.True(DnsManager.IsVirtualOrExcludedAdapter("Tunnel Interface", "Tunnel", System.Net.NetworkInformation.NetworkInterfaceType.Tunnel));

        // Real physical adapters must never be excluded
        Assert.False(DnsManager.IsVirtualOrExcludedAdapter("Realtek Gaming GbE Family Controller", "Ethernet", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet));
        Assert.False(DnsManager.IsVirtualOrExcludedAdapter("Realtek RTL8852AE WiFi 6 802.11ax PCIe Adapter", "Wi-Fi", System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211));
    }

    [Fact]
    public void IsDnsActive_WithEmptyInputs_ReturnsFalse()
    {
        Assert.False(DnsManager.IsDnsActive("", ""));
        Assert.False(DnsManager.IsDnsActive(null!, null!));
        Assert.False(DnsManager.IsDnsActive("   ", "   "));
    }
}
