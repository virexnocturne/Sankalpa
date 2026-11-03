using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Blocker.Core;
using Blocker.Tests;
using Xunit;

namespace Blocker.Core.Tests;

public class DashboardTests
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
    public void SessionStartedMessage_DurationText_CorrectPlurals()
    {
        // Agent A Checklist Item 6: "1-minute", "25-minute", "2-hour"
        Assert.Equal("1-minute", DurationFormatter.FormatAdjective(TimeSpan.FromMinutes(1)));
        Assert.Equal("25-minute", DurationFormatter.FormatAdjective(TimeSpan.FromMinutes(25)));
        Assert.Equal("1-hour", DurationFormatter.FormatAdjective(TimeSpan.FromHours(1)));
        Assert.Equal("2-hour", DurationFormatter.FormatAdjective(TimeSpan.FromHours(2)));

        string msg1 = $"{DurationFormatter.FormatAdjective(TimeSpan.FromMinutes(1))} session started on New Block.";
        string msg25 = $"{DurationFormatter.FormatAdjective(TimeSpan.FromMinutes(25))} session started on New Block.";
        string msg2h = $"{DurationFormatter.FormatAdjective(TimeSpan.FromHours(2))} session started on New Block.";

        Assert.Equal("1-minute session started on New Block.", msg1);
        Assert.Equal("25-minute session started on New Block.", msg25);
        Assert.Equal("2-hour session started on New Block.", msg2h);

        // Assert no "(s)"
        Assert.DoesNotContain("(s)", msg1);
        Assert.DoesNotContain("(s)", msg25);
        Assert.DoesNotContain("(s)", msg2h);
    }

    [Fact]
    public void CardCounts_SingularAndPlural_CorrectFormatting()
    {
        // Agent A Checklist Item 7: "0 apps", "1 app", "2 apps", "0 sites", "1 site", "2 sites"
        Assert.Equal("0 apps", UiLogic.FormatAppsBadge(0));
        Assert.Equal("1 app", UiLogic.FormatAppsBadge(1));
        Assert.Equal("2 apps", UiLogic.FormatAppsBadge(2));

        Assert.Equal("0 sites", UiLogic.FormatSitesBadge(0));
        Assert.Equal("1 site", UiLogic.FormatSitesBadge(1));
        Assert.Equal("2 sites", UiLogic.FormatSitesBadge(2));
        Assert.Equal("all sites", UiLogic.FormatSitesBadge(10, blockAll: true));

        Assert.Equal("1 exception", UiLogic.FormatExceptionsBadge(1));
        Assert.Equal("2 exceptions", UiLogic.FormatExceptionsBadge(2));
    }

    [Fact]
    public void UiLint_PassesOnDialogWindows()
    {
        string root = GetRepoRoot();
        string darkDlgPath = Path.Combine(root, "src", "Blocker.App", "DarkDialogWindow.xaml");
        string startSessionPath = Path.Combine(root, "src", "Blocker.App", "StartSessionDialog.xaml");
        string extDialogPath = Path.Combine(root, "src", "Blocker.App", "ExtensionDialog.xaml");

        Assert.True(File.Exists(darkDlgPath));
        Assert.True(File.Exists(startSessionPath));
        Assert.True(File.Exists(extDialogPath));

        UiLint.AssertPasses(new[] { darkDlgPath, startSessionPath, extDialogPath });
    }

    [Fact]
    public void UiLint_PassesOnDashboardMainWindowSections()
    {
        string root = GetRepoRoot();
        string mainWindowPath = Path.Combine(root, "src", "Blocker.App", "MainWindow.xaml");
        Assert.True(File.Exists(mainWindowPath));

        var lines = File.ReadAllLines(mainWindowPath);

        // Header + Block cards section: lines 1 to 442
        var dashboardLines = lines.Take(442).ToList();
        // Status bar section: lines 1034 to end
        dashboardLines.AddRange(lines.Skip(1033));

        string combined = string.Join("\n", dashboardLines);

        // Assert legacy brushes do not exist in Dashboard sections
        Assert.DoesNotContain("BgDarkBrush", combined);
        Assert.DoesNotContain("TextLightBrush", combined);
        Assert.DoesNotContain("TextDimBrush", combined);
        Assert.DoesNotContain("SurfacePillBrush", combined);
        Assert.DoesNotContain("SuccessGreenBrush", combined);
        Assert.DoesNotContain("DangerRedBrush", combined);

        // Assert no font size < 12
        var fontMatches = Regex.Matches(combined, @"FontSize=""(\d+)""");
        foreach (Match m in fontMatches)
        {
            int size = int.Parse(m.Groups[1].Value);
            Assert.True(size >= 12, $"Font size {size} is below 12");
        }

        // Assert shift fix on block card actions column (reserved width)
        Assert.Contains(@"<ColumnDefinition Width=""144"" />", combined);

        // Assert ToolTips
        Assert.Contains(@"ToolTip=""Settings""", combined);
        Assert.Contains(@"ToolTip=""Delete block""", combined);

        // Assert Lucide icons
        Assert.Contains("IconShield", combined);
        Assert.Contains("IconSettings", combined);
        Assert.Contains("IconTrash", combined);
        Assert.Contains("IconClock", combined);

        // Assert no emojis in status bar or cards
        Assert.DoesNotContain("🔒", combined);
        Assert.DoesNotContain("⚠️", combined);
        Assert.DoesNotContain("⚡", combined);
        Assert.DoesNotContain("🍅", combined);
    }
}
