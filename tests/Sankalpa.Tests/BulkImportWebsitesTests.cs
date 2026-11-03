using System.Collections.Generic;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class BulkImportWebsitesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n \n \t ")]
    public void ParsePatternsFromText_NullOrEmpty_ReturnsEmpty(string? input)
    {
        var result = WebRuleMatcher.ParsePatternsFromText(input, out int invalid, out int dups);
        Assert.Empty(result);
        Assert.Equal(0, invalid);
        Assert.Equal(0, dups);
    }

    [Fact]
    public void ParsePatternsFromText_SimpleOnePerLine_ParsesAll()
    {
        string text = @"
facebook.com
twitter.com
reddit.com
youtube.com
";
        var result = WebRuleMatcher.ParsePatternsFromText(text, out int invalid, out int dups);
        Assert.Equal(new[] { "facebook.com", "twitter.com", "reddit.com", "youtube.com" }, result);
        Assert.Equal(0, invalid);
        Assert.Equal(0, dups);
    }

    [Fact]
    public void ParsePatternsFromText_WithUrlsAndSchemes_NormalizesCleanly()
    {
        string text = @"
https://www.youtube.com/
http://news.ycombinator.com/item?id=123
https://github.com/foo/bar#readme
";
        var result = WebRuleMatcher.ParsePatternsFromText(text, out int invalid, out int dups);
        Assert.Contains("www.youtube.com", result);
        Assert.Contains("news.ycombinator.com/item", result);
        Assert.Contains("github.com/foo/bar", result);
        Assert.Equal(0, invalid);
        Assert.Equal(0, dups);
    }

    [Fact]
    public void ParsePatternsFromText_WithWildcardsAndPaths_PreservesValidSyntax()
    {
        string text = @"
*.gambling.com
twitch.tv/$
instagram.com/explore/*
";
        var result = WebRuleMatcher.ParsePatternsFromText(text, out int invalid, out int dups);
        Assert.Contains("*.gambling.com", result);
        Assert.Contains("twitch.tv/$", result);
        Assert.Contains("instagram.com/explore/*", result);
        Assert.Equal(0, invalid);
        Assert.Equal(0, dups);
    }

    [Fact]
    public void ParsePatternsFromText_HostsFileFormat_ExtractsDomainAndSkipsIP()
    {
        string text = @"
# Standard hosts file blocklist
127.0.0.1  adservice.google.com
0.0.0.0    doubleclick.net   tracker.org
::1        telemetry.microsoft.com
127.0.0.1  localhost
";
        var result = WebRuleMatcher.ParsePatternsFromText(text, out int invalid, out int dups);
        Assert.Contains("adservice.google.com", result);
        Assert.Contains("doubleclick.net", result);
        Assert.Contains("tracker.org", result);
        Assert.Contains("telemetry.microsoft.com", result);
        // "localhost" and "127.0.0.1" shouldn't be added as website domains
        Assert.DoesNotContain("localhost", result);
        Assert.DoesNotContain("127.0.0.1", result);
        Assert.Equal(4, result.Count);
        Assert.Equal(0, invalid);
    }

    [Fact]
    public void ParsePatternsFromText_CommentsAndBlankLines_IgnoredCorrectly()
    {
        string text = @"
# Section: Social media
facebook.com # main site
// Video platforms
youtube.com // entertainment
; News
cnn.com ; breaking news

";
        var result = WebRuleMatcher.ParsePatternsFromText(text, out int invalid, out int dups);
        Assert.Equal(3, result.Count);
        Assert.Contains("facebook.com", result);
        Assert.Contains("youtube.com", result);
        Assert.Contains("cnn.com", result);
        Assert.Equal(0, invalid);
    }

    [Fact]
    public void ParsePatternsFromText_CommaAndSemicolonSeparated_ParsesMultiple()
    {
        string text = "site1.com, site2.com; site3.com site4.com";
        var result = WebRuleMatcher.ParsePatternsFromText(text, out int invalid, out int dups);
        Assert.Equal(new[] { "site1.com", "site2.com", "site3.com", "site4.com" }, result);
        Assert.Equal(0, invalid);
        Assert.Equal(0, dups);
    }

    [Fact]
    public void ParsePatternsFromText_DeduplicatesWithinFile_CountsDuplicates()
    {
        string text = @"
YOUTUBE.COM
youtube.com
YouTube.Com
vimeo.com
";
        var result = WebRuleMatcher.ParsePatternsFromText(text, out int invalid, out int dups);
        Assert.Equal(2, result.Count);
        Assert.Equal("youtube.com", result[0]);
        Assert.Equal("vimeo.com", result[1]);
        Assert.Equal(2, dups);
        Assert.Equal(0, invalid);
    }

    [Fact]
    public void ParsePatternsFromText_InvalidPatterns_SkippedAndCounted()
    {
        string text = @"
good-domain.com
invalid..domain.com
http://
good2.org
not a domain with spaces
";
        var result = WebRuleMatcher.ParsePatternsFromText(text, out int invalid, out int dups);
        Assert.Contains("good-domain.com", result);
        Assert.Contains("good2.org", result);
        Assert.Equal(2, result.Count);
        Assert.True(invalid > 0);
    }
}
