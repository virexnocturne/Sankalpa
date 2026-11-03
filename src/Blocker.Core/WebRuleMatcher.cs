using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Blocker.Core;

public enum HostMatchMode
{
    SubdomainOrExact, // without '*': matches exact host or any subdomain (ends with '.' + host)
    Glob              // with '*': glob match against full URL host, no implicit subdomain extension
}

public enum PathMatchMode
{
    WholeSite,        // empty pattern path, not exact -> matches any URL path
    HomepageOnly,     // empty pattern path, exact ($) -> matches urlPath == ""
    GlobPath          // glob path matching
}

public sealed class CompiledPattern
{
    public string OriginalPattern { get; }
    public HostMatchMode HostMode { get; }
    public string Host { get; }
    public PathMatchMode PathMode { get; }
    public string PatternPath { get; }
    public bool IsExact { get; }

    public CompiledPattern(
        string originalPattern,
        HostMatchMode hostMode,
        string host,
        PathMatchMode pathMode,
        string patternPath,
        bool isExact)
    {
        OriginalPattern = originalPattern;
        HostMode = hostMode;
        Host = host;
        PathMode = pathMode;
        PatternPath = patternPath;
        IsExact = isExact;
    }
}

public static class WebRuleMatcher
{
    private const int MaxCompileCacheCapacity = 2000;
    private static readonly IdnMapping _idn = new();
    private static readonly ConcurrentDictionary<string, CompiledPattern> _compileCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Validates and normalizes typed website patterns.
    /// Format: host[/path]. Stored lower-case with no scheme.
    /// </summary>
    public static bool TryNormalizePattern(string? input, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Pattern cannot be empty.";
            return false;
        }

        string s = input.Trim();

        // Strip leading scheme
        int protoIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (protoIdx >= 0)
        {
            s = s[(protoIdx + 3)..];
        }
        else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            s = s[7..];
        }
        else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            s = s[8..];
        }

        // Strip query or fragment
        int qIdx = s.IndexOf('?');
        if (qIdx >= 0)
        {
            s = s[..qIdx];
        }
        int hashIdx = s.IndexOf('#');
        if (hashIdx >= 0)
        {
            s = s[..hashIdx];
        }

        s = s.ToLowerInvariant();

        if (string.IsNullOrEmpty(s))
        {
            error = "Pattern cannot be empty.";
            return false;
        }

        if (s.Length > 200)
        {
            error = "Pattern exceeds maximum length of 200 characters.";
            return false;
        }

        if (s.Contains(' '))
        {
            error = "Pattern cannot contain spaces.";
            return false;
        }

        int starCount = s.Count(c => c == '*');
        if (starCount > 8)
        {
            error = "Pattern cannot contain more than 8 wildcard '*' characters.";
            return false;
        }

        // Exact match marker '$' can only appear at the very end of the pattern
        int dollarIdx = s.IndexOf('$');
        if (dollarIdx >= 0 && dollarIdx != s.Length - 1)
        {
            error = "Exact match marker '$' can only appear at the end of the pattern.";
            return false;
        }

        // Split into host and path
        int slashIdx = s.IndexOf('/');
        string hostPart = slashIdx >= 0 ? s[..slashIdx] : s;
        string pathPart = slashIdx >= 0 ? s[slashIdx..] : string.Empty;

        // If hostPart ends with '$' (e.g. example.com$), treat '$' as homepage-exact path
        if (hostPart.EndsWith("$"))
        {
            hostPart = hostPart[..^1];
            pathPart = "/$";
        }

        // If pattern has a path, strip any accidental trailing dot on host (e.g. "youtube.com./waterexample" -> "youtube.com/waterexample")
        if (!string.IsNullOrEmpty(pathPart))
        {
            while (hostPart.EndsWith(".") && hostPart.Length > 1)
            {
                hostPart = hostPart[..^1];
            }
        }

        if (string.IsNullOrEmpty(hostPart))
        {
            error = "Host cannot be empty.";
            return false;
        }

        // Validate allowed characters in host: letters, digits, '-', '.', '*', '_', '%'
        foreach (char c in hostPart)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '.' && c != '*' && c != '_' && c != '%')
            {
                error = $"Invalid character '{c}' in hostname.";
                return false;
            }
        }

        // Convert host to punycode if it contains non-ASCII characters
        try
        {
            if (hostPart.Any(c => c > 127))
            {
                hostPart = _idn.GetAscii(hostPart);
            }
        }
        catch (Exception ex)
        {
            error = $"Invalid international domain name: {ex.Message}";
            return false;
        }

        // Host must have at least one dot OR at least one '*'
        if (!hostPart.Contains('.') && !hostPart.Contains('*'))
        {
            error = "Host must contain at least one dot or wildcard '*'.";
            return false;
        }

        // Empty labels check (e.g. "a..com", leading or trailing dot in hostPart)
        if (hostPart.StartsWith(".") || hostPart.EndsWith("."))
        {
            error = "Host cannot start or end with a dot.";
            return false;
        }
        if (hostPart.Contains(".."))
        {
            error = "Host cannot contain consecutive dots.";
            return false;
        }

        // Validate pathPart characters if present
        if (!string.IsNullOrEmpty(pathPart))
        {
            for (int i = 0; i < pathPart.Length; i++)
            {
                char c = pathPart[i];
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '.' && c != '*' && c != '/' &&
                    c != '$' && c != '%' && c != '_' && c != '+' && c != '~' && c != '!' &&
                    c != '@' && c != '(' && c != ')' && c != ';')
                {
                    error = $"Invalid character '{c}' in path.";
                    return false;
                }
            }
        }

        // Canonical normalization:
        // Strip trailing slash unless it's "/$"
        if (pathPart.EndsWith("/") && pathPart != "/$")
        {
            pathPart = pathPart[..^1];
        }

        normalized = hostPart + pathPart;
        return true;
    }

    /// <summary>
    /// Parses website domain/pattern lines from raw text (e.g. from an imported .txt file).
    /// Handles one per line, comma/semicolon/space separation, hosts-file syntax (0.0.0.0 or 127.0.0.1 prefix),
    /// comments (#, //, ;), URL stripping (http/https/queries/anchors), and deduplication.
    /// </summary>
    public static List<string> ParsePatternsFromText(string? text, out int invalidCount, out int duplicateCount)
    {
        invalidCount = 0;
        duplicateCount = 0;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            // Comment lines
            if (line.StartsWith("#") || line.StartsWith("//") || line.StartsWith(";"))
            {
                continue;
            }

            // Strip trailing inline comments if any
            string lineWithoutComment = line;
            int hashIdx = lineWithoutComment.IndexOf('#');
            if (hashIdx >= 0)
            {
                lineWithoutComment = lineWithoutComment[..hashIdx].Trim();
            }
            int semiIdx = lineWithoutComment.IndexOf(';');
            if (semiIdx > 0 && char.IsWhiteSpace(lineWithoutComment[semiIdx - 1]))
            {
                lineWithoutComment = lineWithoutComment[..semiIdx].Trim();
            }
            int slashSlashIdx = lineWithoutComment.IndexOf("//", StringComparison.Ordinal);
            if (slashSlashIdx > 0 && lineWithoutComment[slashSlashIdx - 1] == ':')
            {
                // Protocol like http:// or https:// - keep it
            }
            else if (slashSlashIdx >= 0)
            {
                lineWithoutComment = lineWithoutComment[..slashSlashIdx].Trim();
            }

            if (string.IsNullOrWhiteSpace(lineWithoutComment))
            {
                continue;
            }

            // Check if line is formatted like a hosts file entry: e.g. "127.0.0.1 example.com" or "0.0.0.0 example.com"
            var spaceTokens = lineWithoutComment.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (spaceTokens.Length >= 2 && (IPAddress.TryParse(spaceTokens[0], out _) || spaceTokens[0].Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            {
                for (int i = 1; i < spaceTokens.Length; i++)
                {
                    ProcessCandidateToken(spaceTokens[i], result, seen, ref invalidCount, ref duplicateCount);
                }
            }
            else
            {
                // Normal format: could be single token, or separated by commas, semicolons, or whitespace
                var tokens = lineWithoutComment.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var token in tokens)
                {
                    ProcessCandidateToken(token, result, seen, ref invalidCount, ref duplicateCount);
                }
            }
        }

        return result;
    }

    private static void ProcessCandidateToken(
        string token,
        List<string> result,
        HashSet<string> seen,
        ref int invalidCount,
        ref int duplicateCount)
    {
        string trimmed = token.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("//") || trimmed.StartsWith(";"))
        {
            return;
        }

        // Filter out raw IP addresses and localhost
        if (IPAddress.TryParse(trimmed, out _) || trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (TryNormalizePattern(trimmed, out string norm, out _))
        {
            if (seen.Add(norm))
            {
                result.Add(norm);
            }
            else
            {
                duplicateCount++;
            }
        }
        else
        {
            invalidCount++;
        }
    }

    /// <summary>
    /// Linear-time wildcard matching algorithm (O(n * m), iterative, remembers only the last '*' position).
    /// '*' matches any run of characters including '/', case-insensitive.
    /// </summary>
    public static bool IsGlobMatch(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        int sIdx = 0, pIdx = 0;
        int starIdx = -1;
        int matchIdx = 0;

        while (sIdx < text.Length)
        {
            if (pIdx < pattern.Length && pattern[pIdx] == '*')
            {
                starIdx = pIdx;
                matchIdx = sIdx;
                pIdx++;
            }
            else if (pIdx < pattern.Length && (char.ToUpperInvariant(pattern[pIdx]) == char.ToUpperInvariant(text[sIdx])))
            {
                sIdx++;
                pIdx++;
            }
            else if (starIdx != -1)
            {
                pIdx = starIdx + 1;
                matchIdx++;
                sIdx = matchIdx;
            }
            else
            {
                return false;
            }
        }

        while (pIdx < pattern.Length && pattern[pIdx] == '*')
        {
            pIdx++;
        }

        return pIdx == pattern.Length;
    }

    /// <summary>
    /// Whole-segment path matching.
    /// If exact, must match urlPath completely.
    /// If not exact, matches if it matches urlPath completely or matches from start ending right before a '/'.
    /// </summary>
    public static bool MatchesPath(string urlPath, string patternPath, bool isExact)
    {
        if (isExact)
        {
            return IsGlobMatch(urlPath.AsSpan(), patternPath.AsSpan());
        }

        if (IsGlobMatch(urlPath.AsSpan(), patternPath.AsSpan()))
        {
            return true;
        }

        for (int i = urlPath.Length - 1; i >= 1; i--)
        {
            if (urlPath[i] == '/')
            {
                if (IsGlobMatch(urlPath.AsSpan(0, i), patternPath.AsSpan()))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Pattern-to-match helper: compiles a normalized pattern into matchable modes and parts.
    /// </summary>
    public static CompiledPattern CompilePattern(string pattern)
    {
        if (_compileCache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        string p = pattern.Trim();
        int slashIdx = p.IndexOf('/');
        string hostPart = slashIdx >= 0 ? p[..slashIdx] : p;
        string rawPatternPath = slashIdx >= 0 ? p[slashIdx..] : string.Empty;

        if (hostPart.EndsWith("$"))
        {
            hostPart = hostPart[..^1];
            rawPatternPath = "/$";
        }

        // Strip trailing dots from hostPart, e.g. "youtube.com." -> "youtube.com"
        while (hostPart.EndsWith(".") && hostPart.Length > 1)
        {
            hostPart = hostPart[..^1];
        }

        // Host mode
        HostMatchMode hostMode = hostPart.Contains('*') ? HostMatchMode.Glob : HostMatchMode.SubdomainOrExact;

        // Path mode
        bool isExact = false;
        if (rawPatternPath.EndsWith("$"))
        {
            isExact = true;
            rawPatternPath = rawPatternPath[..^1];
        }
        if (rawPatternPath.EndsWith("/"))
        {
            rawPatternPath = rawPatternPath[..^1];
        }

        PathMatchMode pathMatchMode;
        if (string.IsNullOrEmpty(rawPatternPath))
        {
            pathMatchMode = isExact ? PathMatchMode.HomepageOnly : PathMatchMode.WholeSite;
        }
        else
        {
            pathMatchMode = PathMatchMode.GlobPath;
        }

        var compiled = new CompiledPattern(p, hostMode, hostPart, pathMatchMode, rawPatternPath, isExact);

        if (_compileCache.Count >= MaxCompileCacheCapacity)
        {
            _compileCache.Clear();
        }

        _compileCache.TryAdd(pattern, compiled);
        return compiled;
    }

    /// <summary>
    /// Matches a raw pattern against an absolute URL string.
    /// </summary>
    public static bool Matches(string pattern, string urlString)
    {
        if (!TryParseUrl(urlString, out string urlHost, out string urlPath)) return false;
        var compiled = CompilePattern(pattern);
        return Matches(compiled, urlHost, urlPath);
    }

    /// <summary>
    /// Matches a compiled pattern against a parsed URL host and URL path.
    /// </summary>
    public static bool Matches(CompiledPattern compiled, string urlHost, string urlPath)
    {
        // 1. Host match
        bool hostMatch = false;

        if (compiled.HostMode == HostMatchMode.SubdomainOrExact)
        {
            // Strip leading "www." from BOTH pattern host and URL host
            string effectiveUrlHost = urlHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? urlHost[4..] : urlHost;
            string effectivePatHost = compiled.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? compiled.Host[4..] : compiled.Host;

            if (effectivePatHost.Equals("youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                // User requirement: blocking youtube.com must NOT block subdomains of youtube (e.g. music.youtube.com, studio.youtube.com) unless glob *.youtube.com is used
                hostMatch = effectiveUrlHost.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                            effectiveUrlHost.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase) ||
                            effectiveUrlHost.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                hostMatch = effectiveUrlHost.Equals(effectivePatHost, StringComparison.OrdinalIgnoreCase) ||
                            effectiveUrlHost.EndsWith("." + effectivePatHost, StringComparison.OrdinalIgnoreCase);
            }
        }
        else if (compiled.HostMode == HostMatchMode.Glob)
        {
            hostMatch = IsGlobMatch(urlHost.AsSpan(), compiled.Host.AsSpan());
        }

        if (!hostMatch)
        {
            return false;
        }

        // 2. Path match
        return compiled.PathMode switch
        {
            PathMatchMode.WholeSite => true,
            PathMatchMode.HomepageOnly => urlPath.Length == 0,
            PathMatchMode.GlobPath => MatchesPath(urlPath, compiled.PatternPath, compiled.IsExact),
            _ => false
        };
    }

    /// <summary>
    /// Parses a URL string into IDN host and normalized path.
    /// Only http and https schemes are supported; returns false otherwise.
    /// </summary>
    public static bool TryParseUrl(string? urlString, out string urlHost, out string urlPath)
    {
        urlHost = string.Empty;
        urlPath = string.Empty;

        if (string.IsNullOrWhiteSpace(urlString))
        {
            return false;
        }

        if (!Uri.TryCreate(urlString.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        urlHost = uri.IdnHost.ToLowerInvariant();

        string rawPath = uri.AbsolutePath;
        string decodedPath;
        try
        {
            decodedPath = Uri.UnescapeDataString(rawPath);
        }
        catch
        {
            decodedPath = rawPath;
        }

        if (decodedPath.EndsWith("/"))
        {
            decodedPath = decodedPath[..^1];
        }

        urlPath = decodedPath;
        return true;
    }

    /// <summary>
    /// Evaluates a URL against a single profile's rules.
    /// Blocked if (any block pattern matches OR profile.BlockAllWebsites) AND no exception pattern matches.
    /// </summary>
    public static WebRuleMatchResult Evaluate(Profile profile, string? urlString)
    {
        if (!TryParseUrl(urlString, out string urlHost, out string urlPath))
        {
            return new WebRuleMatchResult { Blocked = false, ProfileName = profile.Name };
        }

        var webRules = profile.WebRules ?? new List<WebRule>();

        // 1. Check exceptions of this profile
        foreach (var rule in webRules.Where(r => r.Allow))
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern)) continue;
            string trimmedPat = rule.Pattern.Trim();
            if (trimmedPat == "*" || trimmedPat == "*.*" || trimmedPat == "*/*") continue;

            var compiled = CompilePattern(rule.Pattern);
            if (Matches(compiled, urlHost, urlPath))
            {
                return new WebRuleMatchResult
                {
                    Blocked = false,
                    ProfileName = profile.Name,
                    MatchedExceptionPattern = rule.Pattern
                };
            }
        }

        // 2. Check blocks
        if (profile.BlockAllWebsites)
        {
            return new WebRuleMatchResult
            {
                Blocked = true,
                ProfileName = profile.Name,
                MatchedBlockPattern = "all websites"
            };
        }

        foreach (var rule in webRules.Where(r => !r.Allow))
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern)) continue;
            var compiled = CompilePattern(rule.Pattern);
            if (Matches(compiled, urlHost, urlPath))
            {
                return new WebRuleMatchResult
                {
                    Blocked = true,
                    ProfileName = profile.Name,
                    MatchedBlockPattern = rule.Pattern
                };
            }
        }

        return new WebRuleMatchResult
        {
            Blocked = false,
            ProfileName = profile.Name
        };
    }

    /// <summary>
    /// Evaluates a URL across multiple active profiles.
    /// Blocked if ANY active profile blocks it. Exceptions in one profile never unblock another profile.
    /// </summary>
    public static WebRuleMatchResult Evaluate(IEnumerable<Profile>? activeProfiles, string? urlString)
    {
        if (activeProfiles == null)
        {
            return new WebRuleMatchResult { Blocked = false };
        }

        string? firstException = null;

        foreach (var profile in activeProfiles)
        {
            var result = Evaluate(profile, urlString);
            if (result.Blocked)
            {
                return result; // Any profile blocking means blocked!
            }

            if (firstException == null && result.MatchedExceptionPattern != null)
            {
                firstException = result.MatchedExceptionPattern;
            }
        }

        return new WebRuleMatchResult
        {
            Blocked = false,
            MatchedExceptionPattern = firstException
        };
    }

    /// <summary>
    /// Derives the domain-only block list for hosts file enforcement.
    /// Returns domain-only block patterns (no path, no "*") ONLY when the profile has
    /// no exception patterns and BlockAllWebsites is false.
    /// </summary>
    public static List<string> DeriveBlockedDomains(IEnumerable<WebRule>? webRules, bool blockAllWebsites)
    {
        if (blockAllWebsites || webRules == null)
        {
            return new List<string>();
        }

        var rulesList = webRules.ToList();

        // If there are ANY exception patterns (Allow == true), hosts file cannot enforce exceptions
        if (rulesList.Any(r => r.Allow))
        {
            return new List<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rulesList.Where(r => !r.Allow))
        {
            string p = rule.Pattern.Trim();

            // Domain-only block patterns: no path (no '/'), no wildcard (no '*'), no exact marker ('$')
            if (p.Contains('/') || p.Contains('*') || p.Contains('$'))
            {
                continue;
            }

            if (HostsFileBuilder.TryNormalizeDomain(p, out string normalized))
            {
                if (seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }
        }

        return result;
    }
}
