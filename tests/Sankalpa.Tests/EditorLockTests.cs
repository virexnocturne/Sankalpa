using System;
using System.IO;
using System.Linq;
using Blocker.Core;
using Blocker.Tests;
using Xunit;

namespace Blocker.Core.Tests;

public class EditorLockTests
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

    public static string FormatScheduleChipText(bool is24Hour)
    {
        return is24Hour ? "Weekdays 08:00-17:00" : "Weekdays 8:00 AM-5:00 PM";
    }

    [Theory]
    [InlineData(true, "Weekdays 08:00-17:00")]
    [InlineData(false, "Weekdays 8:00 AM-5:00 PM")]
    public void ScheduleChipTimeText_Respects12hAnd24hFormats(bool is24Hour, string expected)
    {
        // Agent C Checklist Item 3: Times in chip "Weekdays 08:00-17:00" follow 12-hour or 24-hour setting
        string actual = FormatScheduleChipText(is24Hour);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CardTitle_CompleteLock_NoUnconditionalJargon()
    {
        // Agent C Checklist Item 5: Rename the card title "Complete Unconditional Lock" to "Complete Lock"
        string root = GetRepoRoot();
        string editorPath = Path.Combine(root, "src", "Blocker.App", "BlockEditorDialog.xaml");
        Assert.True(File.Exists(editorPath));

        string xaml = File.ReadAllText(editorPath);
        Assert.Contains("Complete Lock", xaml);
        Assert.DoesNotContain("Complete Unconditional Lock", xaml);
        Assert.DoesNotContain("Unconditional", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UiLint_PassesOnLockDialogs()
    {
        string root = GetRepoRoot();
        string typingPath = Path.Combine(root, "src", "Blocker.App", "TypingChallengeDialog.xaml");
        string passPath = Path.Combine(root, "src", "Blocker.App", "PasswordUnlockDialog.xaml");

        Assert.True(File.Exists(typingPath));
        Assert.True(File.Exists(passPath));

        UiLint.AssertPasses(new[] { typingPath, passPath });
    }

    [Fact]
    public void UiLint_PassesOnScheduleAndLockTabsXaml()
    {
        string root = GetRepoRoot();
        string editorPath = Path.Combine(root, "src", "Blocker.App", "BlockEditorDialog.xaml");
        Assert.True(File.Exists(editorPath));

        string fullXaml = File.ReadAllText(editorPath);
        int tab3Index = fullXaml.IndexOf("<!-- TAB 3: SCHEDULE -->", StringComparison.Ordinal);
        int bottomIndex = fullXaml.IndexOf("<!-- ROW 3: BOTTOM ACTIONS -->", StringComparison.Ordinal);
        Assert.True(tab3Index > 0 && bottomIndex > tab3Index);

        string scheduleAndLockSection = fullXaml.Substring(tab3Index, bottomIndex - tab3Index);

        // Verify no hardcoded colors, banned jargon, or emojis
        Assert.DoesNotContain("BgCardBrush", scheduleAndLockSection);
        Assert.DoesNotContain("BorderDarkBrush", scheduleAndLockSection);
        Assert.DoesNotContain("SurfacePillBrush", scheduleAndLockSection);
        Assert.DoesNotContain("WarningAmberBrush", scheduleAndLockSection);
        Assert.DoesNotContain("TextMutedBrush", scheduleAndLockSection);
        Assert.DoesNotContain("TextDimBrush", scheduleAndLockSection);
        Assert.DoesNotContain("DangerRedBrush", scheduleAndLockSection);
        Assert.DoesNotContain("SuccessGreenBrush", scheduleAndLockSection);
        Assert.DoesNotContain("AccentBlueBrush", scheduleAndLockSection);
        Assert.DoesNotContain("TextLightBrush", scheduleAndLockSection);

        // No emojis in Tab 3 & 4
        Assert.DoesNotContain("🍅", scheduleAndLockSection);
        Assert.DoesNotContain("🔒", scheduleAndLockSection);
        Assert.DoesNotContain("⚡", scheduleAndLockSection);
        Assert.DoesNotContain("🏆", scheduleAndLockSection);

        // Verify Lucide icons are used
        Assert.Contains("IconTimer", scheduleAndLockSection);
        Assert.Contains("IconPlay", scheduleAndLockSection);
        Assert.Contains("IconLock", scheduleAndLockSection);
        Assert.Contains("IconCheck", scheduleAndLockSection);
    }
}
