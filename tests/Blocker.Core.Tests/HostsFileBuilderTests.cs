using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class HostsFileBuilderTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("EXAMPLE.COM", "example.com")]
    [InlineData("  sub.domain.org  ", "sub.domain.org")]
    [InlineData("https://youtube.com/watch?v=123", "youtube.com")]
    [InlineData("http://reddit.com/r/all", "reddit.com")]
    [InlineData("ftp://files.server.co.uk/dir", "files.server.co.uk")]
    [InlineData("http://site.com:8080/test", "site.com")]
    [InlineData("my-cool-site.net", "my-cool-site.net")]
    public void TryNormalizeDomain_ValidDomains_ShouldSucceed(string input, string expected)
    {
        bool success = HostsFileBuilder.TryNormalizeDomain(input, out string normalized);
        Assert.True(success);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("http://localhost")]
    [InlineData("http://localhost/test")]
    [InlineData("nodots")]
    [InlineData("com")]
    [InlineData("*.example.com")]
    [InlineData("example*.com")]
    [InlineData("site name.com")]
    [InlineData("site#1.com")]
    [InlineData("http://site#hash.com")]
    [InlineData("example..com")]
    [InlineData(".com")]
    [InlineData("example.")]
    public void TryNormalizeDomain_InvalidDomains_ShouldFail(string? input)
    {
        bool success = HostsFileBuilder.TryNormalizeDomain(input, out string normalized);
        Assert.False(success);
        Assert.Empty(normalized);
    }

    [Fact]
    public void ExpandDomain_ShouldReturnSelfAndWwwAndMVariants()
    {
        var variants = HostsFileBuilder.ExpandDomain("example.com");

        Assert.Equal(3, variants.Count);
        Assert.Contains("example.com", variants);
        Assert.Contains("www.example.com", variants);
        Assert.Contains("m.example.com", variants);
    }

    [Theory]
    [InlineData("www.example.com")]
    [InlineData("m.example.com")]
    public void ExpandDomain_WhenPrefixAlreadyPresent_ShouldDeduplicateAndExpandCleanly(string input)
    {
        var variants = HostsFileBuilder.ExpandDomain(input);

        Assert.Equal(3, variants.Count);
        Assert.Contains("example.com", variants);
        Assert.Contains("www.example.com", variants);
        Assert.Contains("m.example.com", variants);
    }

    [Fact]
    public void ApplyBlock_OnEmptyContent_ShouldAddBlockWithMarkersAndIps()
    {
        string result = HostsFileBuilder.ApplyBlock(string.Empty, new[] { "example.com" });

        Assert.Contains(HostsFileBuilder.BeginMarker, result);
        Assert.Contains(HostsFileBuilder.EndMarker, result);
        Assert.Contains("127.0.0.1 example.com", result);
        Assert.Contains("::1 example.com", result);
        Assert.Contains("127.0.0.1 www.example.com", result);
        Assert.Contains("::1 www.example.com", result);
        Assert.Contains("127.0.0.1 m.example.com", result);
        Assert.Contains("::1 m.example.com", result);
    }

    [Fact]
    public void ApplyBlock_OnExistingContentWithCrlf_ShouldPreserveLineEndingsAndExistingLines()
    {
        string existing = "127.0.0.1 localhost\r\n::1 localhost\r\n# Custom mapping\r\n192.168.1.50 test.local\r\n";
        string result = HostsFileBuilder.ApplyBlock(existing, new[] { "reddit.com" });

        Assert.StartsWith(existing, result);
        Assert.Contains(HostsFileBuilder.BeginMarker, result);
        Assert.Contains(HostsFileBuilder.EndMarker, result);
        Assert.Contains("127.0.0.1 reddit.com\r\n", result);
        Assert.Contains("::1 reddit.com\r\n", result);
        Assert.DoesNotContain("\r\r", result);
    }

    [Fact]
    public void ApplyBlock_ShouldReplaceExistingBlock_WithoutDuplicatingMarkers()
    {
        string original = "127.0.0.1 localhost\r\n";
        string step1 = HostsFileBuilder.ApplyBlock(original, new[] { "old-site.com" });
        string step2 = HostsFileBuilder.ApplyBlock(step1, new[] { "new-site.com" });

        // Exactly one BEGIN and one END marker
        int beginCount = CountOccurrences(step2, HostsFileBuilder.BeginMarker);
        int endCount = CountOccurrences(step2, HostsFileBuilder.EndMarker);

        Assert.Equal(1, beginCount);
        Assert.Equal(1, endCount);
        Assert.Contains("new-site.com", step2);
        Assert.DoesNotContain("old-site.com", step2);
        Assert.Contains("127.0.0.1 localhost", step2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1 localhost\r\n::1 localhost\r\n")]
    [InlineData("127.0.0.1 localhost\r\n::1 localhost")] // no trailing newline
    [InlineData("# Header comment\n127.0.0.1 localhost\n")] // LF line endings
    public void RemoveBlock_ShouldGiveBackOriginalContentExactly(string originalContent)
    {
        string blocked = HostsFileBuilder.ApplyBlock(originalContent, new[] { "blocked-site.com", "other.org" });
        string restored = HostsFileBuilder.RemoveBlock(blocked);

        Assert.Equal(originalContent, restored);
    }

    [Fact]
    public void RemoveBlock_ShouldBeIdempotent()
    {
        string original = "127.0.0.1 localhost\r\n";
        string blocked = HostsFileBuilder.ApplyBlock(original, new[] { "site.com" });

        string removedOnce = HostsFileBuilder.RemoveBlock(blocked);
        string removedTwice = HostsFileBuilder.RemoveBlock(removedOnce);
        string removedThrice = HostsFileBuilder.RemoveBlock(removedTwice);

        Assert.Equal(original, removedOnce);
        Assert.Equal(original, removedTwice);
        Assert.Equal(original, removedThrice);
    }

    [Fact]
    public void ApplyBlock_ShouldBeIdempotent()
    {
        string original = "127.0.0.1 localhost\r\n";
        string appliedOnce = HostsFileBuilder.ApplyBlock(original, new[] { "site.com" });
        string appliedTwice = HostsFileBuilder.ApplyBlock(appliedOnce, new[] { "site.com" });

        Assert.Equal(appliedOnce, appliedTwice);
    }

    [Theory]
    [InlineData("www.youtube.com", "youtube.com")]
    [InlineData("m.youtube.com", "youtube.com")]
    [InlineData("www.www.youtube.com", "youtube.com")]
    [InlineData("https://www.youtube.com:443/watch?v=123/", "youtube.com")]
    [InlineData("http://m.reddit.com:8080/r/all/", "reddit.com")]
    [InlineData("www.example.co.uk...", "example.co.uk")]
    public void TryNormalizeDomain_StripsWwwMProtocolPortAndPath(string input, string expected)
    {
        bool success = HostsFileBuilder.TryNormalizeDomain(input, out string normalized);
        Assert.True(success);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void ExpandDomain_Generic_IncludesRootAndWwwAndM()
    {
        var variants = HostsFileBuilder.ExpandDomain("www.example.com");

        // Must not contain double www
        Assert.DoesNotContain("www.www.example.com", variants);

        // Core and generic www/m variants only
        Assert.Contains("example.com", variants);
        Assert.Contains("www.example.com", variants);
        Assert.Contains("m.example.com", variants);
        Assert.Equal(3, variants.Count);
    }

    [Fact]
    public void GetBlockedDomains_ParsesEntriesCorrectly()
    {
        string hosts = """
            # localhost
            127.0.0.1 localhost
            # BLOCKER-BEGIN
            127.0.0.1 example.com
            ::1 example.com
            127.0.0.1 www.example.com
            ::1 www.example.com
            # BLOCKER-END
            """;

        var domains = HostsFileBuilder.GetBlockedDomains(hosts);
        Assert.Equal(2, domains.Count);
        Assert.Contains("example.com", domains);
        Assert.Contains("www.example.com", domains);
    }

    [Fact]
    public void GetBlockedDomains_NoBlockerSection_ReturnsEmpty()
    {
        string hosts = "127.0.0.1 localhost\r\n::1 localhost";
        var domains = HostsFileBuilder.GetBlockedDomains(hosts);
        Assert.Empty(domains);
    }

    private static int CountOccurrences(string source, string target)
    {
        int count = 0;
        int idx = 0;
        while ((idx = source.IndexOf(target, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += target.Length;
        }
        return count;
    }
}
