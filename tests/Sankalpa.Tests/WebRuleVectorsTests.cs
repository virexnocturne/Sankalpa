using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class WebRuleVectorsTests
{
    private class VectorDto
    {
        public List<string>? patterns { get; set; }
        public List<string>? exceptions { get; set; }
        public bool blockAll { get; set; }
        public string? url { get; set; }
        public bool expectedBlocked { get; set; }
        public string? note { get; set; }
        public List<ProfileDto>? profiles { get; set; }
    }

    private class ProfileDto
    {
        public string name { get; set; } = string.Empty;
        public List<string>? patterns { get; set; }
        public List<string>? exceptions { get; set; }
        public bool blockAll { get; set; }
    }

    private class VectorFileDto
    {
        public string? format { get; set; }
        public int version { get; set; }
        public List<VectorDto>? vectors { get; set; }
    }

    [Fact]
    public void LoadAndExecute_AllSharedVectors_PassSuccessfully()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "shared", "url-rule-vectors.json");
        if (!File.Exists(path))
        {
            // Fallback for running from different directories
            path = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "shared", "url-rule-vectors.json");
            if (!File.Exists(path))
            {
                path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "shared", "url-rule-vectors.json"));
            }
        }

        Assert.True(File.Exists(path), $"Vector file not found at: {path}");

        string json = File.ReadAllText(path);
        var fileDto = JsonSerializer.Deserialize<VectorFileDto>(json);
        Assert.NotNull(fileDto);
        Assert.NotNull(fileDto.vectors);
        Assert.True(fileDto.vectors.Count >= 60, $"Expected >= 60 vectors, found {fileDto.vectors.Count}");

        int index = 0;
        foreach (var v in fileDto.vectors)
        {
            index++;
            if (v.profiles != null && v.profiles.Count > 0)
            {
                var profiles = v.profiles.Select(p =>
                {
                    var prof = new Profile
                    {
                        Name = p.name,
                        BlockAllWebsites = p.blockAll,
                        WebRules = new List<WebRule>()
                    };
                    if (p.patterns != null)
                    {
                        foreach (var pat in p.patterns)
                        {
                            prof.WebRules.Add(new WebRule { Pattern = pat, Allow = false });
                        }
                    }
                    if (p.exceptions != null)
                    {
                        foreach (var ex in p.exceptions)
                        {
                            prof.WebRules.Add(new WebRule { Pattern = ex, Allow = true });
                        }
                    }
                    return prof;
                }).ToList();

                var matchResult = WebRuleMatcher.Evaluate(profiles, v.url);
                Assert.True(matchResult.Blocked == v.expectedBlocked,
                    $"Vector #{index} failed: {v.note}. URL: '{v.url}'. Expected Blocked: {v.expectedBlocked}, Actual: {matchResult.Blocked}");
            }
            else
            {
                var prof = new Profile
                {
                    Name = "TestProfile",
                    BlockAllWebsites = v.blockAll,
                    WebRules = new List<WebRule>()
                };
                if (v.patterns != null)
                {
                    foreach (var pat in v.patterns)
                    {
                        prof.WebRules.Add(new WebRule { Pattern = pat, Allow = false });
                    }
                }
                if (v.exceptions != null)
                {
                    foreach (var ex in v.exceptions)
                    {
                        prof.WebRules.Add(new WebRule { Pattern = ex, Allow = true });
                    }
                }

                var matchResult = WebRuleMatcher.Evaluate(prof, v.url);
                Assert.True(matchResult.Blocked == v.expectedBlocked,
                    $"Vector #{index} failed: {v.note}. URL: '{v.url}'. Expected Blocked: {v.expectedBlocked}, Actual: {matchResult.Blocked}. MatchedBlock: {matchResult.MatchedBlockPattern}, MatchedEx: {matchResult.MatchedExceptionPattern}");
            }
        }
    }

    [Theory]
    [InlineData("", false, "empty")]
    [InlineData("   ", false, "empty")]
    [InlineData(null, false, "empty")]
    [InlineData("example", false, "at least one dot or wildcard")]
    [InlineData(".example.com", false, "cannot start or end with a dot")]
    [InlineData("example.com.", false, "cannot start or end with a dot")]
    [InlineData("example..com", false, "consecutive dots")]
    [InlineData("example .com", false, "cannot contain spaces")]
    [InlineData("example.com/foo$bar", false, "end of the pattern")]
    [InlineData("example<>.com", false, "Invalid character")]
    public void TryNormalizePattern_RejectsInvalidInput(string? input, bool expectedValid, string expectedErrorContains)
    {
        bool ok = WebRuleMatcher.TryNormalizePattern(input, out string normalized, out string? error);
        Assert.Equal(expectedValid, ok);
        if (!expectedValid)
        {
            Assert.NotNull(error);
            Assert.Contains(expectedErrorContains, error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TryNormalizePattern_RejectsTooLongPattern()
    {
        string tooLong = "example.com/" + new string('a', 200);
        bool ok = WebRuleMatcher.TryNormalizePattern(tooLong, out _, out string? error);
        Assert.False(ok);
        Assert.Contains("maximum length", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryNormalizePattern_RejectsMoreThan8Stars()
    {
        string nineStars = "example.com/*/*/*/*/*/*/*/*/*";
        bool ok = WebRuleMatcher.TryNormalizePattern(nineStars, out _, out string? error);
        Assert.False(ok);
        Assert.Contains("more than 8 wildcard", error, StringComparison.OrdinalIgnoreCase);

        string eightStars = "example.com/*/*/*/*/*/*/*/*";
        bool ok8 = WebRuleMatcher.TryNormalizePattern(eightStars, out _, out string? error8);
        Assert.True(ok8, $"Expected valid for 8 stars, got: {error8}");
    }

    [Fact]
    public void LinearGlob_StressTest_5000Chars_8Stars_FinishesUnder100Ms()
    {
        // 5000-character non-matching path
        string text = "/" + new string('a', 4999);
        // Pattern with 8 stars and a non-matching final character 'z'
        string pattern = "/*a*a*a*a*a*a*a*z";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool match = WebRuleMatcher.IsGlobMatch(text.AsSpan(), pattern.AsSpan());
        sw.Stop();

        Assert.False(match);
        Assert.True(sw.ElapsedMilliseconds < 100, $"Expected linear glob to finish under 100ms, took {sw.ElapsedMilliseconds}ms");
    }

    [Theory]
    [InlineData("https://example.com/path", "example.com/path")]
    [InlineData("HTTP://EXAMPLE.COM", "example.com")]
    [InlineData("example.com?query=1#frag", "example.com")]
    [InlineData("example.com/", "example.com")]
    [InlineData("example.com/$", "example.com/$")]
    [InlineData("*.example.com", "*.example.com")]
    [InlineData("example.*", "example.*")]
    [InlineData("example.com/news/*", "example.com/news/*")]
    public void TryNormalizePattern_NormalizesValidInput(string input, string expected)
    {
        bool ok = WebRuleMatcher.TryNormalizePattern(input, out string normalized, out string? error);
        Assert.True(ok, $"Expected valid for '{input}', got error: {error}");
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void DeriveBlockedDomains_DerivesOnlyCleanDomainBlocks()
    {
        var rules = new List<WebRule>
        {
            new WebRule { Pattern = "example.com", Allow = false },
            new WebRule { Pattern = "example.org", Allow = false },
            new WebRule { Pattern = "example.com/news", Allow = false }, // Has path -> ignored for hosts
            new WebRule { Pattern = "*.example.net", Allow = false },    // Has wildcard -> ignored for hosts
            new WebRule { Pattern = "example.com$", Allow = false }      // Has exact -> ignored for hosts
        };

        var derived = WebRuleMatcher.DeriveBlockedDomains(rules, blockAllWebsites: false);
        Assert.Equal(2, derived.Count);
        Assert.Contains("example.com", derived);
        Assert.Contains("example.org", derived);
    }

    [Fact]
    public void DeriveBlockedDomains_ReturnsEmpty_WhenBlockAllWebsitesIsTrue()
    {
        var rules = new List<WebRule>
        {
            new WebRule { Pattern = "example.com", Allow = false }
        };

        var derived = WebRuleMatcher.DeriveBlockedDomains(rules, blockAllWebsites: true);
        Assert.Empty(derived);
    }

    [Fact]
    public void DeriveBlockedDomains_ReturnsEmpty_WhenAnyExceptionExists()
    {
        var rules = new List<WebRule>
        {
            new WebRule { Pattern = "example.com", Allow = false },
            new WebRule { Pattern = "example.org", Allow = true } // Exception!
        };

        var derived = WebRuleMatcher.DeriveBlockedDomains(rules, blockAllWebsites: false);
        Assert.Empty(derived);
    }

    [Fact]
    public void IsGlobMatch_LiteralAsteriskInText_HandlesWildcardCorrectly()
    {
        // 1. pattern "*" vs text "*x"
        Assert.True(WebRuleMatcher.IsGlobMatch("*x", "*"));

        // 2. pattern "a*" vs text "a*b"
        Assert.True(WebRuleMatcher.IsGlobMatch("a*b", "a*"));

        // 3. wildcard path pattern vs URL path containing literal "*"
        Assert.True(WebRuleMatcher.Matches("example.com/*", "http://example.com/*x"));
        Assert.True(WebRuleMatcher.Matches("example.com/a*", "http://example.com/a*b"));
        Assert.True(WebRuleMatcher.Matches("example.com/search/*", "http://example.com/search/*query*"));
    }

    [Fact]
    public void BlockYoutube_WithWatchException_BlocksHomepage_AllowsWatchUrl()
    {
        var prof = new Profile
        {
            Name = "YoutubeTest",
            BlockAllWebsites = false,
            WebRules = new List<WebRule>
            {
                new WebRule { Pattern = "youtube.com", Allow = false },
                new WebRule { Pattern = "youtube.com/watch", Allow = true }
            }
        };

        // Homepage and other pages must be blocked
        var resHome = WebRuleMatcher.Evaluate(prof, "https://www.youtube.com/");
        Assert.True(resHome.Blocked, "youtube.com homepage must be blocked");

        var resFeed = WebRuleMatcher.Evaluate(prof, "https://www.youtube.com/feed/subscriptions");
        Assert.True(resFeed.Blocked, "youtube.com feed must be blocked");

        // Watch URLs must be allowed
        var resWatch = WebRuleMatcher.Evaluate(prof, "https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        Assert.False(resWatch.Blocked, "youtube.com/watch URL must be allowed as an exception");

        var resWatchClean = WebRuleMatcher.Evaluate(prof, "https://www.youtube.com/watch");
        Assert.False(resWatchClean.Blocked, "youtube.com/watch without query must be allowed as an exception");
    }

    [Fact]
    public void CatchAllWildcardException_IgnoredInBlacklistMode()
    {
        var prof = new Profile
        {
            Name = "CatchAllExTest",
            BlockAllWebsites = false,
            WebRules = new List<WebRule>
            {
                new WebRule { Pattern = "youtube.com", Allow = false },
                new WebRule { Pattern = "*", Allow = true }, // catch-all exception should be ignored
                new WebRule { Pattern = "*.*", Allow = true },
                new WebRule { Pattern = "*/*", Allow = true },
                new WebRule { Pattern = "youtube.com/watch", Allow = true }
            }
        };

        // With catch-all exceptions ignored, homepage must STILL be blocked
        var resHome = WebRuleMatcher.Evaluate(prof, "https://www.youtube.com/");
        Assert.True(resHome.Blocked, "youtube.com homepage must remain blocked even if '*' exception is present");

        // Legitimate path exception still works
        var resWatch = WebRuleMatcher.Evaluate(prof, "https://www.youtube.com/watch?v=123");
        Assert.False(resWatch.Blocked, "youtube.com/watch must still be allowed");
    }

    [Fact]
    public void TrailingDotAndConsecutiveSlashes_NormalizedProperly()
    {
        // 1. Trailing dot FQDN
        bool parsedDot = WebRuleMatcher.TryParseUrl("https://youtube.com./watch?v=1", out string hostDot, out string pathDot);
        Assert.True(parsedDot);
        Assert.Equal("youtube.com", hostDot);

        var prof = new Profile
        {
            Name = "TrailingDotTest",
            BlockAllWebsites = false,
            WebRules = new List<WebRule> { new WebRule { Pattern = "youtube.com", Allow = false } }
        };
        var evalDot = WebRuleMatcher.Evaluate(prof, "https://youtube.com./watch");
        Assert.True(evalDot.Blocked);

        // 2. Consecutive slashes compression
        bool parsedSlash = WebRuleMatcher.TryParseUrl("https://example.com//news///article", out string hostSlash, out string pathSlash);
        Assert.True(parsedSlash);
        Assert.Equal("example.com", hostSlash);
        Assert.Equal("/news/article", pathSlash);
    }
}
