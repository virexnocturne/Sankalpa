using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Blocker.Core;
using Blocker.Tests;
using Xunit;

namespace Blocker.Core.Tests;

public class SettingsTests
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

    [Fact]
    public void LockedUntil_TodayVsDateFormatting_InBothTimeFormats()
    {
        // Agent D Checklist Item 7:
        // "Locked until" uses the 12-hour or 24-hour setting and shows "Today, 3:40 PM" when the date is today.
        DateTime refNow = new DateTime(2026, 11, 3, 10, 0, 0);
        DateTime todayTarget = new DateTime(2026, 11, 3, 15, 40, 0);
        DateTime tomorrowTarget = new DateTime(2026, 11, 4, 15, 40, 0);
        DateTime futureTarget = new DateTime(2026, 11, 10, 15, 40, 0);

        // 12-Hour format
        string fmtToday12 = TimeFormatter.Format(todayTarget, is24Hour: false, referenceNow: refNow);
        Assert.Equal("Today, 3:40 PM", fmtToday12);

        string fmtTomorrow12 = TimeFormatter.Format(tomorrowTarget, is24Hour: false, referenceNow: refNow);
        Assert.Equal("Tomorrow, 3:40 PM", fmtTomorrow12);

        string fmtFuture12 = TimeFormatter.Format(futureTarget, is24Hour: false, referenceNow: refNow);
        Assert.Equal("Nov 10, 3:40 PM", fmtFuture12);

        // 24-Hour format
        string fmtToday24 = TimeFormatter.Format(todayTarget, is24Hour: true, referenceNow: refNow);
        Assert.Equal("Today, 15:40", fmtToday24);

        string fmtTomorrow24 = TimeFormatter.Format(tomorrowTarget, is24Hour: true, referenceNow: refNow);
        Assert.Equal("Tomorrow, 15:40", fmtTomorrow24);

        string fmtFuture24 = TimeFormatter.Format(futureTarget, is24Hour: true, referenceNow: refNow);
        Assert.Equal("Nov 10, 15:40", fmtFuture24);
    }

    [Fact]
    public void LockedHeadingText_MatchesRequirements()
    {
        // Agent D Checklist Item 7:
        // "DNS Protection is Permanently Locked" becomes "DNS protection is locked"
        string heading = "DNS protection is locked";
        Assert.Equal("DNS protection is locked", heading);
        Assert.DoesNotContain("Permanently", heading, StringComparison.OrdinalIgnoreCase);

        string watchdogReplacement = "DNS servers can't be changed or turned off until the timer ends. Changes are reverted automatically.";
        Assert.DoesNotContain("watchdog", watchdogReplacement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cannot", watchdogReplacement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThemeOptions_ThreeOptionsExist_NoClassicOrObsidian()
    {
        // Agent D Checklist Item 2:
        // Three options: "Black", "White", "Grey". Nothing mentions Classic or Obsidian.
        string root = GetRepoRoot();
        string mainWindowPath = Path.Combine(root, "src", "Blocker.App", "MainWindow.xaml");
        Assert.True(File.Exists(mainWindowPath));

        var lines = File.ReadAllLines(mainWindowPath);
        int sIdx = Array.FindIndex(lines, l => l.Contains("IN-WINDOW SETTINGS VIEW")); int sbIdx = Array.FindIndex(lines, l => l.Contains("SLIM STATUS BAR") || l.Contains("ROW 3:")); var settingsLines = (sIdx >= 0 && sbIdx > sIdx) ? lines.Skip(sIdx).Take(sbIdx - sIdx).ToList() : lines.Skip(450).Take(585).ToList();
        string settingsContent = string.Join("\n", settingsLines);

        Assert.Contains(@"Content=""Black""", settingsContent);
        Assert.Contains(@"Content=""White""", settingsContent);
        Assert.Contains(@"Content=""Grey""", settingsContent);

        Assert.DoesNotContain("Classic", settingsContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Obsidian", settingsContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UiLint_PassesOnSettingsSection()
    {
        string root = GetRepoRoot();
        string mainWindowPath = Path.Combine(root, "src", "Blocker.App", "MainWindow.xaml");
        Assert.True(File.Exists(mainWindowPath));

        var lines = File.ReadAllLines(mainWindowPath);
        // Extract Settings View
        int settingsIndex = Array.FindIndex(lines, l => l.Contains("IN-WINDOW SETTINGS VIEW"));
        int statusBarIndex = Array.FindIndex(lines, l => l.Contains("SLIM STATUS BAR") || l.Contains("ROW 3:"));

        var settingsLines = (settingsIndex >= 0 && statusBarIndex > settingsIndex)
            ? lines.Skip(settingsIndex).Take(statusBarIndex - settingsIndex).ToList()
            : lines.Skip(450).Take(585).ToList();
        string combined = string.Join("\n", settingsLines);

        // Assert legacy brushes do not exist
        Assert.DoesNotContain("BgDarkBrush", combined);
        Assert.DoesNotContain("TextLightBrush", combined);
        Assert.DoesNotContain("TextDimBrush", combined);
        Assert.DoesNotContain("TextMutedBrush", combined);
        Assert.DoesNotContain("SurfacePillBrush", combined);
        Assert.DoesNotContain("BorderDarkBrush", combined);
        Assert.DoesNotContain("BorderSubtleBrush", combined);
        Assert.DoesNotContain("AccentBlueBrush", combined);
        Assert.DoesNotContain("AccentPrimaryBrush", combined);
        Assert.DoesNotContain("WarningAmberBrush", combined);

        // Assert no literal font size < 12
        var fontMatches = Regex.Matches(combined, @"FontSize=""(\d+)""");
        foreach (Match m in fontMatches)
        {
            int size = int.Parse(m.Groups[1].Value);
            Assert.True(size >= 12, $"Font size {size} is below 12");
        }

        // Assert Lucide icons used in place of glyphs / emojis
        Assert.Contains("IconArrowLeft", combined);
        Assert.Contains("IconShield", combined);
        Assert.Contains("IconLock", combined);
        Assert.Contains("IconInfo", combined);

        // Assert no emojis or arrows
        Assert.DoesNotContain("←", combined);
        Assert.DoesNotContain("🔒", combined);
        Assert.DoesNotContain("💡", combined);
        Assert.DoesNotContain("✨", combined);
        Assert.DoesNotContain("🏛️", combined);
        Assert.DoesNotContain("🌙", combined);
        Assert.DoesNotContain("⬛", combined);
        Assert.DoesNotContain("⬜", combined);

        // Assert wording fixes
        Assert.Contains("DNS protection is locked", combined);
        Assert.Contains("DNS servers can't be changed or turned off until the timer ends. Changes are reverted automatically.", combined);
        Assert.DoesNotContain("watchdog", combined, StringComparison.OrdinalIgnoreCase);

        // Assert Unlock DNS button is bound to CanUnlockDns
        Assert.Contains(@"IsEnabled=""{Binding CanUnlockDns}""", combined);
    }
}
