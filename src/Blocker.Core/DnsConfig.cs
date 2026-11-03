using System;
using System.Collections.Generic;

namespace Blocker.Core;

public class DnsConfig
{
    public bool Enabled { get; set; }
    public string PresetName { get; set; } = "Cloudflare Family";
    public string PrimaryDns { get; set; } = "1.1.1.3";
    public string SecondaryDns { get; set; } = "1.0.0.3";
    public string PrimaryIpv6 { get; set; } = "2606:4700:4700::1113";
    public string SecondaryIpv6 { get; set; } = "2606:4700:4700::1003";
    public bool Locked { get; set; }
    public string LockType { get; set; } = "Challenge"; // "Challenge" (64 char), "Timer", "Schedule"
    public long LockDurationSeconds { get; set; } = 3600;
    public DateTime? LockedUntilUtc { get; set; }
    public string? UnlockChallenge { get; set; }
    public string ScheduleJson { get; set; } = "[]";
}

public record DnsPreset(string Name, string Description, string Primary, string Secondary, string PrimaryIpv6 = "", string SecondaryIpv6 = "");

public static class DnsPresets
{
    public static readonly IReadOnlyList<DnsPreset> All = new List<DnsPreset>
    {
        new(
            "Cloudflare Family",
            "Blocks malware, phishing, and adult content automatically.",
            "1.1.1.3",
            "1.0.0.3",
            "2606:4700:4700::1113",
            "2606:4700:4700::1003"
        ),
        new(
            "CleanBrowsing",
            "Strict family filter blocking adult sites and enforcing SafeSearch.",
            "185.228.168.168",
            "185.228.169.168",
            "2a0d:2a00:1::",
            "2a0d:2a00:2::1"
        ),
        new(
            "AdGuard Family",
            "Blocks ads, tracking domains, and adult content.",
            "94.140.14.15",
            "94.140.15.16",
            "2a10:50c0::bad1:ff",
            "2a10:50c0::bad2:ff"
        ),
        new(
            "Quad9",
            "Blocks malicious domains, botnets, and malware.",
            "9.9.9.9",
            "149.112.112.112",
            "2620:fe::fe",
            "2620:fe::9"
        ),
        new(
            "Custom",
            "Specify your own primary and secondary DNS server IP addresses.",
            "",
            "",
            "",
            ""
        )
    };
}
