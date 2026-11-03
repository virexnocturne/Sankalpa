using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Blocker.Core;
using Blocker.Tests;
using Xunit;

namespace Blocker.Core.Tests;

public class FoundationTests
{
    private static string GetRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Blocker.sln")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }
        throw new InvalidOperationException("Could not locate repository root containing Blocker.sln");
    }

    #region 1. Identical Theme Key Sets

    [Fact]
    public void AllThreeThemeDictionaries_HaveIdenticalKeySets()
    {
        string root = GetRepoRoot();
        string blackPath = Path.Combine(root, "src", "Blocker.App", "Theme.Black.xaml");
        string whitePath = Path.Combine(root, "src", "Blocker.App", "Theme.White.xaml");
        string greyPath = Path.Combine(root, "src", "Blocker.App", "Theme.Grey.xaml");

        Assert.True(File.Exists(blackPath), "Theme.Black.xaml must exist");
        Assert.True(File.Exists(whitePath), "Theme.White.xaml must exist");
        Assert.True(File.Exists(greyPath), "Theme.Grey.xaml must exist");

        var blackKeys = ExtractKeysFromXaml(blackPath);
        var whiteKeys = ExtractKeysFromXaml(whitePath);
        var greyKeys = ExtractKeysFromXaml(greyPath);

        Assert.NotEmpty(blackKeys);
        Assert.Equal(blackKeys.OrderBy(k => k), whiteKeys.OrderBy(k => k));
        Assert.Equal(blackKeys.OrderBy(k => k), greyKeys.OrderBy(k => k));

        // Verify required brush tokens exist in all
        string[] requiredBrushTokens =
        {
            "Bg", "Surface", "SurfaceRaised", "Border", "BorderStrong",
            "TextPrimary", "TextSecondary", "TextDisabled",
            "Accent", "TextOnAccent", "Success", "Warning", "Danger", "TextOnDanger"
        };

        foreach (var token in requiredBrushTokens)
        {
            Assert.Contains(token, blackKeys);
        }
    }

    private static HashSet<string> ExtractKeysFromXaml(string filePath)
    {
        var doc = XDocument.Load(filePath);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (var element in doc.Descendants())
        {
            var keyAttr = element.Attribute(xNs + "Key");
            if (keyAttr != null)
            {
                keys.Add(keyAttr.Value);
            }
        }
        return keys;
    }

    #endregion

    #region 2. WCAG Contrast Ratios

    private static double ComputeRelativeLuminance(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 8) hex = hex.Substring(2); // ARGB -> RGB
        int rInt = Convert.ToInt32(hex.Substring(0, 2), 16);
        int gInt = Convert.ToInt32(hex.Substring(2, 2), 16);
        int bInt = Convert.ToInt32(hex.Substring(4, 2), 16);

        double CalcComponent(int val)
        {
            double c = val / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        double r = CalcComponent(rInt);
        double g = CalcComponent(gInt);
        double b = CalcComponent(bInt);

        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static double ComputeContrast(string hex1, string hex2)
    {
        double l1 = ComputeRelativeLuminance(hex1);
        double l2 = ComputeRelativeLuminance(hex2);
        if (l1 < l2)
        {
            (l1, l2) = (l2, l1);
        }
        return (l1 + 0.05) / (l2 + 0.05);
    }

    private static Dictionary<string, string> ExtractColorsFromTheme(string themePath)
    {
        var doc = XDocument.Load(themePath);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (var el in doc.Descendants())
        {
            if (el.Name.LocalName == "Color")
            {
                var key = el.Attribute(xNs + "Key")?.Value;
                if (!string.IsNullOrEmpty(key))
                {
                    map[key] = el.Value.Trim();
                }
            }
        }
        return map;
    }

    [Theory]
    [InlineData("Theme.Black.xaml")]
    [InlineData("Theme.White.xaml")]
    [InlineData("Theme.Grey.xaml")]
    public void Theme_ContrastRatios_MeetWcagRequirements(string themeFileName)
    {
        string root = GetRepoRoot();
        string themePath = Path.Combine(root, "src", "Blocker.App", themeFileName);
        var colors = ExtractColorsFromTheme(themePath);

        string bg = colors["ColorBg"];
        string surface = colors["ColorSurface"];
        string surfaceRaised = colors["ColorSurfaceRaised"];
        string textPrimary = colors["ColorTextPrimary"];
        string textSecondary = colors["ColorTextSecondary"];
        string textDisabled = colors["ColorTextDisabled"];
        string success = colors["ColorSuccess"];
        string warning = colors["ColorWarning"];
        string danger = colors["ColorDanger"];

        // TextPrimary at least 7:1 on Bg, Surface and SurfaceRaised
        Assert.True(ComputeContrast(textPrimary, bg) >= 7.0, $"{themeFileName}: TextPrimary on Bg failed 7:1");
        Assert.True(ComputeContrast(textPrimary, surface) >= 7.0, $"{themeFileName}: TextPrimary on Surface failed 7:1");
        Assert.True(ComputeContrast(textPrimary, surfaceRaised) >= 7.0, $"{themeFileName}: TextPrimary on SurfaceRaised failed 7:1");

        // TextSecondary at least 4.5:1 on all three
        Assert.True(ComputeContrast(textSecondary, bg) >= 4.5, $"{themeFileName}: TextSecondary on Bg failed 4.5:1");
        Assert.True(ComputeContrast(textSecondary, surface) >= 4.5, $"{themeFileName}: TextSecondary on Surface failed 4.5:1");
        Assert.True(ComputeContrast(textSecondary, surfaceRaised) >= 4.5, $"{themeFileName}: TextSecondary on SurfaceRaised failed 4.5:1");

        // TextDisabled at least 3:1 on Bg and Surface
        Assert.True(ComputeContrast(textDisabled, bg) >= 3.0, $"{themeFileName}: TextDisabled on Bg failed 3:1");
        Assert.True(ComputeContrast(textDisabled, surface) >= 3.0, $"{themeFileName}: TextDisabled on Surface failed 3:1");

        // Success, Warning, Danger at least 4.5:1 against Surface
        Assert.True(ComputeContrast(success, surface) >= 4.5, $"{themeFileName}: Success on Surface failed 4.5:1");
        Assert.True(ComputeContrast(warning, surface) >= 4.5, $"{themeFileName}: Warning on Surface failed 4.5:1");
        Assert.True(ComputeContrast(danger, surface) >= 4.5, $"{themeFileName}: Danger on Surface failed 4.5:1");
    }


    #endregion

    #region 3. Settings Migration & Unknown Keys

    [Fact]
    public void SettingsMigration_OldDarkOrClassic_BecomesBlack()
    {
        var s1 = UiSettingsStore.FromJson("{\"ThemeMode\":\"Dark\"}");
        Assert.Equal("black", s1.Theme);

        var s2 = UiSettingsStore.FromJson("{\"DesignTheme\":\"Classic\"}");
        Assert.Equal("black", s2.Theme);

        var s3 = UiSettingsStore.FromJson("{\"DesignTheme\":\"Obsidian\"}");
        Assert.Equal("black", s3.Theme);
    }

    [Fact]
    public void SettingsMigration_OldLight_BecomesWhite()
    {
        var s1 = UiSettingsStore.FromJson("{\"ThemeMode\":\"Light\"}");
        Assert.Equal("white", s1.Theme);

        var s2 = UiSettingsStore.FromJson("{\"ExtensionTheme\":\"MinimalWhite\"}");
        Assert.Equal("white", s2.Theme);
    }

    [Fact]
    public void SettingsMigration_Missing_BecomesBlack()
    {
        var s1 = UiSettingsStore.FromJson("{}");
        Assert.Equal("black", s1.Theme);

        var s2 = UiSettingsStore.FromJson("{\"AccentColor\":\"Indigo\"}");
        Assert.Equal("black", s2.Theme);
    }

    [Fact]
    public void SettingsMigration_UnknownKeys_IgnoredWithoutErrors()
    {
        string json = @"{
            ""HideBrowserExtensionTab"": true,
            ""MinimiseSettingsMenuToGearIcon"": false,
            ""HideDesignThemeSwitcherButton"": true,
            ""HideQuickDarkModeButton"": false,
            ""SomeCompletelyUnknownSetting"": 999
        }";

        var s = UiSettingsStore.FromJson(json);
        Assert.NotNull(s);
        Assert.Equal("black", s.Theme);
        Assert.Equal("Emerald", s.AccentColor);
    }

    #endregion

    #region 4. Duration and Time Formatters

    [Fact]
    public void DurationFormatter_StandardExamples_MatchExactly()
    {
        Assert.Equal("58 min", DurationFormatter.Format(TimeSpan.FromMinutes(58)));
        Assert.Equal("2 h 14 min", DurationFormatter.Format(new TimeSpan(2, 14, 0)));
        Assert.Equal("1 hour", DurationFormatter.Format(TimeSpan.FromHours(1)));
        Assert.Equal("2 hours", DurationFormatter.Format(TimeSpan.FromHours(2)));
        Assert.Equal("1 day", DurationFormatter.Format(TimeSpan.FromDays(1)));
        Assert.Equal("3 days", DurationFormatter.Format(TimeSpan.FromDays(3)));
    }

    [Fact]
    public void DurationFormatter_CountdownFormat_WorksForMinutesAndHours()
    {
        Assert.Equal("24:59", DurationFormatter.FormatCountdown(new TimeSpan(0, 24, 59)));
        Assert.Equal("05:03", DurationFormatter.FormatCountdown(new TimeSpan(0, 5, 3)));
        Assert.Equal("00:45", DurationFormatter.FormatCountdown(new TimeSpan(0, 0, 45)));
        Assert.Equal("01:25:00", DurationFormatter.FormatCountdown(new TimeSpan(1, 25, 0)));
    }

    [Fact]
    public void DurationFormatter_AdjectiveFormat_WorksForMessages()
    {
        Assert.Equal("1-minute", DurationFormatter.FormatAdjective(TimeSpan.FromMinutes(1)));
        Assert.Equal("25-minute", DurationFormatter.FormatAdjective(TimeSpan.FromMinutes(25)));
        Assert.Equal("1-hour", DurationFormatter.FormatAdjective(TimeSpan.FromHours(1)));
        Assert.Equal("2-hour", DurationFormatter.FormatAdjective(TimeSpan.FromHours(2)));
    }

    [Fact]
    public void TimeFormatter_Follows12hAnd24h_WithTodayHandling()
    {
        var today = DateTime.Today;
        var testTime = new DateTime(today.Year, today.Month, today.Day, 15, 40, 0);

        string out12 = TimeFormatter.Format(testTime, is24Hour: false, referenceNow: today);
        Assert.Equal("Today, 3:40 PM", out12);

        string out24 = TimeFormatter.Format(testTime, is24Hour: true, referenceNow: today);
        Assert.Equal("Today, 15:40", out24);

        var otherDay = new DateTime(2026, 11, 4, 15, 40, 0);
        var refDay = new DateTime(2026, 11, 2, 0, 0, 0);
        string outOther12 = TimeFormatter.Format(otherDay, is24Hour: false, referenceNow: refDay);
        Assert.Equal("Nov 4, 3:40 PM", outOther12);

        string outOther24 = TimeFormatter.Format(otherDay, is24Hour: true, referenceNow: refDay);
        Assert.Equal("Nov 4, 15:40", outOther24);
    }

    #endregion

    #region 5. Embedded Fonts Check

    [Fact]
    public void EachFontToken_PointsToActualEmbeddedFontFile()
    {
        string root = GetRepoRoot();
        string typoPath = Path.Combine(root, "src", "Blocker.App", "Typography.xaml");
        Assert.True(File.Exists(typoPath), "Typography.xaml must exist");

        var doc = XDocument.Load(typoPath);
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        var fontTokens = new Dictionary<string, string>();
        foreach (var el in doc.Descendants())
        {
            if (el.Name.LocalName == "FontFamily")
            {
                var key = el.Attribute(xNs + "Key")?.Value;
                if (!string.IsNullOrEmpty(key))
                {
                    fontTokens[key] = el.Value.Trim();
                }
            }
        }

        Assert.True(fontTokens.ContainsKey("FontTitle"), "FontTitle token must exist");
        Assert.True(fontTokens.ContainsKey("FontBody"), "FontBody token must exist");
        Assert.True(fontTokens.ContainsKey("FontMono"), "FontMono token must exist");

        string fontsDir = Path.Combine(root, "src", "Blocker.App", "Fonts");
        Assert.True(Directory.Exists(fontsDir), "Fonts directory must exist");

        var fontFiles = Directory.GetFiles(fontsDir, "*.ttf").Select(Path.GetFileName).ToList();

        // Familjen Grotesk (SemiBold 600)
        Assert.Contains("FamiljenGrotesk-SemiBold.ttf", fontFiles);

        // IBM Plex Sans (400, 500, 600)
        Assert.Contains("IBMPlexSans-Regular.ttf", fontFiles);
        Assert.Contains("IBMPlexSans-Medium.ttf", fontFiles);
        Assert.Contains("IBMPlexSans-SemiBold.ttf", fontFiles);

        // IBM Plex Mono (400, 500)
        Assert.Contains("IBMPlexMono-Regular.ttf", fontFiles);
        Assert.Contains("IBMPlexMono-Medium.ttf", fontFiles);
    }

    #endregion

    #region 6. UiLint on Foundation Files

    [Fact]
    public void UiLint_PassesOnFoundationFiles()
    {
        string root = GetRepoRoot();
        var filesToLint = new[]
        {
            Path.Combine(root, "src", "Blocker.App", "Theme.Black.xaml"),
            Path.Combine(root, "src", "Blocker.App", "Theme.White.xaml"),
            Path.Combine(root, "src", "Blocker.App", "Theme.Grey.xaml"),
            Path.Combine(root, "src", "Blocker.App", "Typography.xaml"),
            Path.Combine(root, "src", "Blocker.App", "Icons.xaml"),
            Path.Combine(root, "src", "Blocker.App", "Controls.xaml"),
            Path.Combine(root, "src", "Blocker.Core", "Formatters.cs")
        };

        UiLint.AssertPasses(filesToLint);
    }

    #endregion

    #region 7. Lucide Icons Check

    [Fact]
    public void IconsDictionary_ContainsAllEighteenRequiredLucideIcons()
    {
        string root = GetRepoRoot();
        string iconsPath = Path.Combine(root, "src", "Blocker.App", "Icons.xaml");
        Assert.True(File.Exists(iconsPath), "Icons.xaml must exist");

        var keys = ExtractKeysFromXaml(iconsPath);
        string[] requiredIcons =
        {
            "IconShield", "IconSettings", "IconLock", "IconLockOpen",
            "IconTrash", "IconPlus", "IconX", "IconCheck",
            "IconClock", "IconLayoutGrid", "IconBan", "IconTimer",
            "IconPlay", "IconHourglass", "IconChevronDown", "IconArrowLeft",
            "IconInfo", "IconFolderOpen"
        };

        foreach (var icon in requiredIcons)
        {
            Assert.Contains(icon, keys);
        }
        Assert.Contains("Icon", keys); // Base icon style
    }

    #endregion
}
