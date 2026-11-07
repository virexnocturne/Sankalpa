using System.Text;

namespace Blocker.Core;

public static class HostsFileBuilder
{
    public const string BeginMarker = "# BLOCKER-BEGIN";
    public const string EndMarker = "# BLOCKER-END";

    public static bool TryNormalizeDomain(string? input, out string normalizedDomain)
    {
        normalizedDomain = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string s = input.Trim().ToLowerInvariant();

        // Reject anything with spaces or wildcards upfront
        if (s.Contains(' ') || s.Contains('*'))
        {
            return false;
        }

        // Strip protocol if present
        int protoIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (protoIdx >= 0)
        {
            s = s[(protoIdx + 3)..];
        }

        // Strip path, query, or fragment
        int slashIdx = s.IndexOf('/');
        if (slashIdx >= 0)
        {
            s = s[..slashIdx];
        }
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

        // Strip port if present
        int colonIdx = s.IndexOf(':');
        if (colonIdx >= 0)
        {
            s = s[..colonIdx];
        }

        // Trim leading and trailing dots
        s = s.Trim('.');

        // Strip leading "www." and "m." so root domain is stored and expanded
        bool strippedPrefix = true;
        while (strippedPrefix)
        {
            if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                s = s[4..];
            }
            else if (s.StartsWith("m.", StringComparison.OrdinalIgnoreCase))
            {
                s = s[2..];
            }
            else
            {
                strippedPrefix = false;
            }
        }

        s = s.Trim('.');

        // Reject empty, 'localhost', or invalid characters
        if (string.IsNullOrWhiteSpace(s) ||
            s.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            s.Contains(' ') ||
            s.Contains('*') ||
            s.Contains('#'))
        {
            return false;
        }

        // Must have at least one dot
        int dotCount = s.Count(c => c == '.');
        if (dotCount < 1)
        {
            return false;
        }

        // Reject IP addresses (IPv4 and IPv6)
        if (System.Net.IPAddress.TryParse(s, out _))
        {
            return false;
        }

        // Convert host to Punycode if it contains non-ASCII characters
        try
        {
            if (s.Any(c => c > 127))
            {
                var idn = new System.Globalization.IdnMapping();
                s = idn.GetAscii(s);
            }
        }
        catch
        {
            return false;
        }

        // Ensure non-empty labels and valid hostname characters
        string[] labels = s.Split('.');
        foreach (string label in labels)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            foreach (char c in label)
            {
                if (!char.IsLetterOrDigit(c) && c != '-')
                {
                    return false;
                }
            }
        }

        normalizedDomain = s;
        return true;
    }

    public static List<string> ExpandDomain(string domain)
    {
        if (!TryNormalizeDomain(domain, out string root))
        {
            return new List<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddVariant(string d)
        {
            if (string.IsNullOrWhiteSpace(d)) return;
            string clean = d.Trim().ToLowerInvariant();
            if (clean.Contains(' ') || clean.Contains('#') || clean.Contains('*')) return;
            if (seen.Add(clean))
            {
                result.Add(clean);
            }
        }

        // Add root and standard www/m variants
        AddVariant(root);
        AddVariant($"www.{root}");
        AddVariant($"m.{root}");

        return result;
    }

    public static List<string> ExpandDomains(IEnumerable<string>? domains)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (domains != null)
        {
            foreach (var d in domains)
            {
                foreach (var exp in ExpandDomain(d))
                {
                    if (seen.Add(exp))
                    {
                        result.Add(exp);
                    }
                }
            }
        }

        return result;
    }

    public static string ApplyBlock(string? existingHostsText, IEnumerable<string>? domains)
    {
        string text = existingHostsText ?? string.Empty;
        string nl = DetectNewline(text);

        var expanded = ExpandDomains(domains);
        if (expanded.Count == 0)
        {
            return RemoveBlock(text);
        }

        var sb = new StringBuilder();
        sb.Append(BeginMarker).Append(nl);
        foreach (var domain in expanded)
        {
            sb.Append("127.0.0.1 ").Append(domain).Append(nl);
            sb.Append("::1 ").Append(domain).Append(nl);
        }
        sb.Append(EndMarker);
        string blockContent = sb.ToString();

        int beginIdx = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        int endIdx = beginIdx >= 0 ? text.IndexOf(EndMarker, beginIdx, StringComparison.Ordinal) : -1;

        if (beginIdx >= 0 && endIdx >= 0)
        {
            // Replace existing block
            int replaceEnd = endIdx + EndMarker.Length;
            return text[..beginIdx] + blockContent + text[replaceEnd..];
        }

        // No existing block
        if (string.IsNullOrEmpty(text))
        {
            return blockContent + nl;
        }

        if (text.EndsWith("\n", StringComparison.Ordinal))
        {
            return text + blockContent + nl;
        }

        return text + nl + blockContent;
    }

    public static string RemoveBlock(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int beginIdx = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (beginIdx < 0)
        {
            return text;
        }

        int endIdx = text.IndexOf(EndMarker, beginIdx, StringComparison.Ordinal);
        if (endIdx < 0)
        {
            return text;
        }

        int markerEnd = endIdx + EndMarker.Length;
        int afterBlockIdx = markerEnd;

        // Skip newline after EndMarker if present
        if (afterBlockIdx < text.Length && text[afterBlockIdx] == '\r')
        {
            afterBlockIdx++;
        }
        if (afterBlockIdx < text.Length && text[afterBlockIdx] == '\n')
        {
            afterBlockIdx++;
        }

        int beforeBlockIdx = beginIdx;

        // If block is at the very end of the file and was attached without a trailing newline
        if (afterBlockIdx == text.Length && markerEnd == text.Length && beforeBlockIdx > 0)
        {
            if (text[beforeBlockIdx - 1] == '\n')
            {
                beforeBlockIdx--;
                if (beforeBlockIdx > 0 && text[beforeBlockIdx - 1] == '\r')
                {
                    beforeBlockIdx--;
                }
            }
        }

        return text[..beforeBlockIdx] + text[afterBlockIdx..];
    }

    public static List<string> GetBlockedDomains(string? hostsContent)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(hostsContent))
        {
            return result;
        }

        int beginIdx = hostsContent.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (beginIdx < 0)
        {
            return result;
        }

        int endIdx = hostsContent.IndexOf(EndMarker, beginIdx, StringComparison.Ordinal);
        if (endIdx < 0)
        {
            return result;
        }

        string section = hostsContent.Substring(beginIdx + BeginMarker.Length, endIdx - (beginIdx + BeginMarker.Length));
        var lines = section.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                string domain = parts[1].Trim().ToLowerInvariant();
                if (seen.Add(domain))
                {
                    result.Add(domain);
                }
            }
        }

        return result;
    }

    private static string DetectNewline(string text)
    {
        if (text.Contains("\r\n", StringComparison.Ordinal))
        {
            return "\r\n";
        }
        if (text.Contains('\n'))
        {
            return "\n";
        }
        return Environment.NewLine;
    }
}
