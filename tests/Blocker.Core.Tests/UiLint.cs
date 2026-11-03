using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Blocker.Tests;

public class UiLintViolation
{
    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string Rule { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string OffendingText { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"[{Rule}] {Path.GetFileName(FilePath)}:{LineNumber} - {Message} (Text: '{OffendingText}')";
    }
}

public static class UiLint
{
    private static readonly string[] BannedWords =
    {
        "Additive",
        "watchdog",
        "Unconditional",
        "Obsidian",
        "Classic",
        "Crisp"
    };

    private static readonly string[] NamedColors =
    {
        "Black", "White", "Red", "Green", "Blue", "Yellow", "Orange", "Purple",
        "Gray", "Grey"
    };

    public static List<UiLintViolation> LintFile(string filePath)
    {
        var violations = new List<UiLintViolation>();
        if (!File.Exists(filePath))
        {
            return violations;
        }

        string fileName = Path.GetFileName(filePath);
        if (fileName.Equals("UiLint.cs", StringComparison.OrdinalIgnoreCase))
        {
            return violations;
        }

        bool isThemeFile = fileName.StartsWith("Theme.", StringComparison.OrdinalIgnoreCase) &&
                           fileName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);

        string fullText = File.ReadAllText(filePath);
        string[] lines = File.ReadAllLines(filePath);

        // Regex patterns
        var hexPattern = new Regex(@"#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})\b", RegexOptions.Compiled);
        var fontLiteralPattern = new Regex(@"FontFamily=""(?!\{)[^""]+""", RegexOptions.Compiled);
        var fontSizeLiteralPattern = new Regex(@"FontSize=""([0-9.]+)""", RegexOptions.Compiled);
        var fontSizeCsPattern = new Regex(@"FontSize\s*=\s*([0-9.]+)", RegexOptions.Compiled);
        var sPluralPattern = new Regex(@"\([sS]\)", RegexOptions.Compiled);

        // Emoji & Symbols regex:
        // Arrows: \u2190-\u21FF
        // Misc Technical/Symbols/Dingbats: \u2300-\u27BF
        // Supplementary symbols & emojis: \uD83C-\uD83E [\uDC00-\uDFFF]
        var emojiPattern = new Regex(@"[\u2190-\u21FF\u2300-\u27BF]|\uD83C[\uDF00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDD00-\uDFFF]", RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string rawLine = lines[i];
            string line = rawLine.Trim();

            // Skip comments
            if (line.StartsWith("//") || line.StartsWith("<!--") && line.EndsWith("-->"))
            {
                continue;
            }

            // Strip inline comment from line for C#
            string codeLine = line;
            int commentIdx = codeLine.IndexOf("//", StringComparison.Ordinal);
            if (commentIdx >= 0)
            {
                codeLine = codeLine.Substring(0, commentIdx);
            }

            // (a) Hard-coded colors outside Theme.*.xaml
            if (!isThemeFile)
            {
                var hexMatches = hexPattern.Matches(codeLine);
                foreach (Match m in hexMatches)
                {
                    violations.Add(new UiLintViolation
                    {
                        FilePath = filePath,
                        LineNumber = lineNum,
                        Rule = "HARD_CODED_COLOR",
                        Message = $"Hard-coded hex color '{m.Value}' outside Theme.*.xaml",
                        OffendingText = m.Value
                    });
                }

                if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    if (Regex.IsMatch(codeLine, @"\b(?:Colors|Brushes)\.[A-Za-z]+|\bColor\.From(?:A)?Rgb\b"))
                    {
                        violations.Add(new UiLintViolation
                        {
                            FilePath = filePath,
                            LineNumber = lineNum,
                            Rule = "HARD_CODED_COLOR",
                            Message = "Hard-coded Color/Brush call outside Theme.*.xaml",
                            OffendingText = line
                        });
                    }
                }
                else if (filePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var namedColor in NamedColors)
                    {
                        var namedRegex = new Regex($@"\b(?:Background|Foreground|BorderBrush|Fill|Stroke)=""{namedColor}""", RegexOptions.IgnoreCase);
                        if (namedRegex.IsMatch(codeLine))
                        {
                            violations.Add(new UiLintViolation
                            {
                                FilePath = filePath,
                                LineNumber = lineNum,
                                Rule = "HARD_CODED_COLOR",
                                Message = $"Hard-coded literal color '{namedColor}' outside Theme.*.xaml",
                                OffendingText = namedColor
                            });
                        }
                    }
                }
            }

            // (b) FontFamily or FontSize literals and any size below 12
            if (filePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                var fontMat = fontLiteralPattern.Match(codeLine);
                if (fontMat.Success)
                {
                    violations.Add(new UiLintViolation
                    {
                        FilePath = filePath,
                        LineNumber = lineNum,
                        Rule = "LITERAL_FONT_FAMILY",
                        Message = "Literal FontFamily used instead of theme token",
                        OffendingText = fontMat.Value
                    });
                }

                var sizeMatches = fontSizeLiteralPattern.Matches(codeLine);
                foreach (Match sm in sizeMatches)
                {
                    if (double.TryParse(sm.Groups[1].Value, out double size))
                    {
                        if (size < 12.0)
                        {
                            violations.Add(new UiLintViolation
                            {
                                FilePath = filePath,
                                LineNumber = lineNum,
                                Rule = "FONT_SIZE_TOO_SMALL",
                                Message = $"FontSize {size} is below the minimum allowed size of 12",
                                OffendingText = sm.Value
                            });
                        }
                        else
                        {
                            violations.Add(new UiLintViolation
                            {
                                FilePath = filePath,
                                LineNumber = lineNum,
                                Rule = "LITERAL_FONT_SIZE",
                                Message = $"Literal FontSize {size} used instead of token",
                                OffendingText = sm.Value
                            });
                        }
                    }
                }
            }
            else if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var sizeMatches = fontSizeCsPattern.Matches(codeLine);
                foreach (Match sm in sizeMatches)
                {
                    if (double.TryParse(sm.Groups[1].Value, out double size) && size < 12.0)
                    {
                        violations.Add(new UiLintViolation
                        {
                            FilePath = filePath,
                            LineNumber = lineNum,
                            Rule = "FONT_SIZE_TOO_SMALL",
                            Message = $"FontSize {size} in code is below 12",
                            OffendingText = sm.Value
                        });
                    }
                }
            }

            // (c) Emoji, dingbat and arrow characters in strings
            var emojiMatches = emojiPattern.Matches(codeLine);
            foreach (Match em in emojiMatches)
            {
                violations.Add(new UiLintViolation
                {
                    FilePath = filePath,
                    LineNumber = lineNum,
                    Rule = "EMOJI_OR_ARROW",
                    Message = $"Emoji, dingbat, or typed arrow character found: '{em.Value}'",
                    OffendingText = em.Value
                });
            }

            // (d) "(s)" plurals
            var sPluralMatches = sPluralPattern.Matches(codeLine);
            foreach (Match sp in sPluralMatches)
            {
                violations.Add(new UiLintViolation
                {
                    FilePath = filePath,
                    LineNumber = lineNum,
                    Rule = "PLURAL_PARENTHESES",
                    Message = "Broken '(s)' plural notation found",
                    OffendingText = sp.Value
                });
            }

            // (e) Banned words
            foreach (var banned in BannedWords)
            {
                var bannedPattern = new Regex($@"\b{banned}\b", RegexOptions.IgnoreCase);
                if (bannedPattern.IsMatch(codeLine))
                {
                    violations.Add(new UiLintViolation
                    {
                        FilePath = filePath,
                        LineNumber = lineNum,
                        Rule = "BANNED_WORD",
                        Message = $"Banned word '{banned}' found",
                        OffendingText = banned
                    });
                }
            }
        }

        return violations;
    }

    public static List<UiLintViolation> Lint(IEnumerable<string> filePaths)
    {
        var all = new List<UiLintViolation>();
        foreach (var path in filePaths)
        {
            all.AddRange(LintFile(path));
        }
        return all;
    }

    public static void AssertPasses(IEnumerable<string> filePaths)
    {
        var violations = Lint(filePaths);
        if (violations.Count > 0)
        {
            string summary = string.Join(Environment.NewLine, violations.Select(v => v.ToString()));
            Assert.Fail($"UiLint detected {violations.Count} violation(s):{Environment.NewLine}{summary}");
        }
    }
}
