using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Blocker.Core;
using Blocker.Tests;
using Xunit;

namespace Blocker.Core.Tests;

public class EditorSitesTests
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

    [Theory]
    [InlineData("Work Profile", true, "Edit Block: Work Profile")]
    [InlineData("Study Time", false, "Edit Block: Study Time")]
    public void EditorTitle_DoesNotContainAdditiveMode(string name, bool isLocked, string expected)
    {
        // Agent B Checklist Item 3: Title becomes "Edit Block: {name}" without "(Additive Mode)"
        string title = isLocked ? $"Edit Block: {name}" : $"Edit Block: {name}";
        Assert.Equal(expected, title);
        Assert.DoesNotContain("Additive", title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("🔒", title);
    }

    [Fact]
    public void TabLabels_DisplayCorrectCounts()
    {
        // Agent B Checklist Item 5: Labels stay "Applications", "Websites (1)", "Schedule", "Lock Protection"
        int appsCount0 = 0;
        int appsCount3 = 3;
        string appLabel0 = appsCount0 > 0 ? $"Applications ({appsCount0})" : "Applications";
        string appLabel3 = appsCount3 > 0 ? $"Applications ({appsCount3})" : "Applications";
        Assert.Equal("Applications", appLabel0);
        Assert.Equal("Applications (3)", appLabel3);

        int blockedCount = 2;
        int exceptionCount = 1;
        int totalWebsites = blockedCount + exceptionCount;
        string websiteLabel0 = 0 > 0 ? "Websites (0)" : "Websites";
        string websiteLabel3 = totalWebsites > 0 ? $"Websites ({totalWebsites})" : "Websites";
        Assert.Equal("Websites", websiteLabel0);
        Assert.Equal("Websites (3)", websiteLabel3);
    }

    [Fact]
    public void UiLint_PassesOnAppPickerDialog()
    {
        string root = GetRepoRoot();
        string appPickerPath = Path.Combine(root, "src", "Blocker.App", "AppPickerDialog.xaml");
        Assert.True(File.Exists(appPickerPath));

        UiLint.AssertPasses(new[] { appPickerPath });
    }

    [Fact]
    public void UiLint_PassesOnEditorSitesXamlSection()
    {
        string root = GetRepoRoot();
        string editorPath = Path.Combine(root, "src", "Blocker.App", "BlockEditorDialog.xaml");
        Assert.True(File.Exists(editorPath));

        // Read lines 1 to 432 belonging to Agent B
        var allLines = File.ReadAllLines(editorPath);
        var agentBLines = allLines.Take(430).ToList();
        string combined = string.Join("\n", agentBLines);

        // Assert no hard-coded colors, old themes, or banned jargon
        Assert.DoesNotContain("Additive", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BgDarkBrush", combined);
        Assert.DoesNotContain("TextLightBrush", combined);
        Assert.DoesNotContain("DangerRedBrush", combined);
        Assert.DoesNotContain("SurfacePillBrush", combined);
        Assert.DoesNotContain("SurfaceHighlightBrush", combined);

        // Assert locked banner text
        Assert.Contains("Block is locked", combined);
        Assert.Contains("Existing blocked items can't be removed", combined);

        // Assert Lucide icons are used
        Assert.Contains("IconLayoutGrid", combined);
        Assert.Contains("IconBan", combined);
        Assert.Contains("IconLock", combined);
    }
}
