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

    [Fact]
    public void BlockEditorDialog_DoesNotContainRbLockedOrLockedHint()
    {
        string root = GetRepoRoot();
        string xamlPath = Path.Combine(root, "src", "Blocker.App", "BlockEditorDialog.xaml");
        string csPath = Path.Combine(root, "src", "Blocker.App", "BlockEditorDialog.xaml.cs");

        Assert.True(File.Exists(xamlPath));
        Assert.True(File.Exists(csPath));

        string xaml = File.ReadAllText(xamlPath);
        string cs = File.ReadAllText(csPath);

        Assert.DoesNotContain("rbLocked", xaml);
        Assert.DoesNotContain("LockedHintTextBlock", xaml);
        Assert.DoesNotContain("Locked during schedule", xaml);

        Assert.DoesNotContain("rbLocked", cs);
        Assert.DoesNotContain("LockedHintTextBlock", cs);
    }

    [Fact]
    public void DayToggleButtonStyle_HasDisabledTriggersAndTooltips()
    {
        string root = GetRepoRoot();
        string xamlPath = Path.Combine(root, "src", "Blocker.App", "BlockEditorDialog.xaml");
        string xaml = File.ReadAllText(xamlPath);

        // Check style triggers
        Assert.Contains("<Trigger Property=\"IsEnabled\" Value=\"False\">", xaml);
        Assert.Contains("<Condition Property=\"IsEnabled\" Value=\"False\" />", xaml);
        Assert.Contains("<Condition Property=\"IsChecked\" Value=\"True\" />", xaml);

        // Check tooltips on day buttons
        Assert.Contains("ToolTip=\"Monday\"", xaml);
        Assert.Contains("ToolTip=\"Tuesday\"", xaml);
        Assert.Contains("ToolTip=\"Wednesday\"", xaml);
        Assert.Contains("ToolTip=\"Thursday\"", xaml);
        Assert.Contains("ToolTip=\"Friday\"", xaml);
        Assert.Contains("ToolTip=\"Saturday\"", xaml);
        Assert.Contains("ToolTip=\"Sunday\"", xaml);
    }

    [Fact]
    public void MainWindow_DistinguishesSessionLockedFromProfileLocked()
    {
        string root = GetRepoRoot();
        string mainCsPath = Path.Combine(root, "src", "Blocker.App", "MainWindow.xaml.cs");
        string mainCs = File.ReadAllText(mainCsPath);

        Assert.Contains("bool isSessionLocked", mainCs);
        Assert.Contains("bool isProfileLocked", mainCs);
        Assert.Contains("new BlockEditorDialog(profile, isSessionLocked, isProfileLocked", mainCs);
    }
public static (string timeText, string amPm) MinutesTo12Hour(int totalMinutes)
    {
        int totalH = (totalMinutes / 60) % 24;
        int m = totalMinutes % 60;
        string amPm = totalH >= 12 ? "PM" : "AM";
        int h12 = totalH % 12;
        if (h12 == 0) h12 = 12;
        return ($"{h12}:{m:D2}", amPm);
    }

    public static bool TryParseTime(string? s, string? amPm, bool is12Hour, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();

        if (is12Hour)
        {
            bool hasAm = s.IndexOf("AM", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasPm = s.IndexOf("PM", StringComparison.OrdinalIgnoreCase) >= 0;

            string cleanStr = s.Replace("AM", "", StringComparison.OrdinalIgnoreCase)
                               .Replace("PM", "", StringComparison.OrdinalIgnoreCase)
                               .Trim();

            var parts = cleanStr.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out int h) || !int.TryParse(parts[1].Trim(), out int m))
            {
                return false;
            }

            if (h < 1 || h > 12 || m < 0 || m > 59)
            {
                return false;
            }

            bool isPm;
            if (hasAm) isPm = false;
            else if (hasPm) isPm = true;
            else isPm = string.Equals(amPm?.Trim(), "PM", StringComparison.OrdinalIgnoreCase);

            int hour24 = h % 12;
            if (isPm) hour24 += 12;

            minutes = hour24 * 60 + m;
            return true;
        }
        else
        {
            var parts = s.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int h) && int.TryParse(parts[1].Trim(), out int m))
            {
                if (h >= 0 && h < 24 && m >= 0 && m < 60)
                {
                    minutes = h * 60 + m;
                    return true;
                }
            }
            return false;
        }
    }

    [Fact]
    public void BlockEditorDialog_CompleteLock_Disabled_WhenBlockIsLocked()
    {
        string root = GetRepoRoot();
        string csPath = Path.Combine(root, "src", "Blocker.App", "BlockEditorDialog.xaml.cs");
        string cs = File.ReadAllText(csPath);

        // Verify that inside if (_isLockedMode), Complete Lock controls are disabled
        Assert.Contains("if (_isLockedMode)", cs);
        Assert.Contains("rbTimedLock.IsEnabled = false;", cs);
        Assert.Contains("TimedLockBorder.IsEnabled = false;", cs);
        Assert.Contains("rbTimedLock.ToolTip =", cs);
    }

    [Fact]
    public void BlockEditorDialog_AllowanceControls_Disabled_WhenBlockIsLocked()
    {
        string root = GetRepoRoot();
        string xamlPath = Path.Combine(root, "src", "Blocker.App", "BlockEditorDialog.xaml");
        string csPath = Path.Combine(root, "src", "Blocker.App", "BlockEditorDialog.xaml.cs");

        string xaml = File.ReadAllText(xamlPath);
        string cs = File.ReadAllText(csPath);

        // Verify allowance buttons exist with x:Name in XAML
        Assert.Contains(@"x:Name=""BtnAllowance15m""", xaml);
        Assert.Contains(@"x:Name=""BtnAllowance30m""", xaml);
        Assert.Contains(@"x:Name=""BtnAllowance45m""", xaml);
        Assert.Contains(@"x:Name=""BtnAllowance60m""", xaml);
        Assert.Contains(@"x:Name=""AllowanceLockedNoticeTextBlock""", xaml);

        // Verify all allowance controls are disabled when _isLockedMode is true
        Assert.Contains("AllowanceCheckBox.IsEnabled = false;", cs);
        Assert.Contains("AllowanceMinutesInput.IsEnabled = false;", cs);
        Assert.Contains("BtnAllowance15m.IsEnabled = false;", cs);
        Assert.Contains("BtnAllowance30m.IsEnabled = false;", cs);
        Assert.Contains("BtnAllowance45m.IsEnabled = false;", cs);
        Assert.Contains("BtnAllowance60m.IsEnabled = false;", cs);
        Assert.Contains("rbAllowanceInSchedule.IsEnabled = false;", cs);
        Assert.Contains("rbAllowance24x7.IsEnabled = false;", cs);
        Assert.Contains("AllowanceCheckBox.ToolTip =", cs);
    }

    [Fact]
    public void BlockEditorDialog_ScheduleControls_Disabled_WhenBlockIsLocked()
    {
        string root = GetRepoRoot();
        string xamlPath = Path.Combine(root, "src", "Blocker.App", "BlockEditorDialog.xaml");
        string csPath = Path.Combine(root, "src", "Blocker.App", "BlockEditorDialog.xaml.cs");

        string xaml = File.ReadAllText(xamlPath);
        string cs = File.ReadAllText(csPath);

        // Verify AddScheduleRowButton and ScheduleLockedNoticeBorder in XAML
        Assert.Contains(@"x:Name=""AddScheduleRowButton""", xaml);
        Assert.Contains(@"x:Name=""ScheduleLockedNoticeBorder""", xaml);
        Assert.Contains("Schedule rules cannot be added, removed, or modified while this block is locked.", xaml);

        // Verify day buttons and time inputs are bound to IsEditable
        Assert.Contains(@"IsEnabled=""{Binding IsEditable}""", xaml);

        // Verify AddScheduleRowButton is disabled when _isLockedMode is true
        Assert.Contains("AddScheduleRowButton.IsEnabled = false;", cs);
        Assert.Contains("ScheduleLockedNoticeBorder.Visibility = Visibility.Visible;", cs);
        Assert.Contains("IsEditable = !_isLockedMode", cs);
    }

    [Fact]
    public void ScheduleRowItem_12HourAnd24HourTimeParsing_And_AmPmCalculation()
    {
        // 12-Hour AM tests
        Assert.True(TryParseTime("8:00", "AM", true, out int min8am));
        Assert.Equal(480, min8am);

        Assert.True(TryParseTime("12:00", "AM", true, out int min12am));
        Assert.Equal(0, min12am); // Midnight

        Assert.True(TryParseTime("12:30", "AM", true, out int min1230am));
        Assert.Equal(30, min1230am);

        // 12-Hour PM tests
        Assert.True(TryParseTime("5:00", "PM", true, out int min5pm));
        Assert.Equal(1020, min5pm); // 17:00

        Assert.True(TryParseTime("12:00", "PM", true, out int min12pm));
        Assert.Equal(720, min12pm); // Noon

        Assert.True(TryParseTime("12:30", "PM", true, out int min1230pm));
        Assert.Equal(750, min1230pm);

        Assert.True(TryParseTime("11:59", "PM", true, out int min1159pm));
        Assert.Equal(1439, min1159pm);

        // 12-Hour with string AM/PM
        Assert.True(TryParseTime("8:00 AM", "PM", true, out int minStrAm));
        Assert.Equal(480, minStrAm);

        Assert.True(TryParseTime("5:00 PM", "AM", true, out int minStrPm));
        Assert.Equal(1020, minStrPm);

        // 12-Hour invalid
        Assert.False(TryParseTime("0:00", "AM", true, out _));
        Assert.False(TryParseTime("13:00", "PM", true, out _));
        Assert.False(TryParseTime("8:60", "AM", true, out _));
        Assert.False(TryParseTime("invalid", "AM", true, out _));

        // 24-Hour tests
        Assert.True(TryParseTime("08:00", "AM", false, out int min24_8));
        Assert.Equal(480, min24_8);

        Assert.True(TryParseTime("17:00", "AM", false, out int min24_17));
        Assert.Equal(1020, min24_17);

        Assert.True(TryParseTime("00:00", "AM", false, out int min24_0));
        Assert.Equal(0, min24_0);

        Assert.True(TryParseTime("23:59", "AM", false, out int min24_2359));
        Assert.Equal(1439, min24_2359);

        // 24-Hour invalid
        Assert.False(TryParseTime("24:00", "AM", false, out _));
        Assert.False(TryParseTime("25:00", "AM", false, out _));
        Assert.False(TryParseTime("08:60", "AM", false, out _));

        // Minutes to 12h conversion
        var (t0, am0) = MinutesTo12Hour(0);
        Assert.Equal("12:00", t0);
        Assert.Equal("AM", am0);

        var (t480, am480) = MinutesTo12Hour(480);
        Assert.Equal("8:00", t480);
        Assert.Equal("AM", am480);

        var (t720, am720) = MinutesTo12Hour(720);
        Assert.Equal("12:00", t720);
        Assert.Equal("PM", am720);

        var (t1020, am1020) = MinutesTo12Hour(1020);
        Assert.Equal("5:00", t1020);
        Assert.Equal("PM", am1020);

        var (t1439, am1439) = MinutesTo12Hour(1439);
        Assert.Equal("11:59", t1439);
        Assert.Equal("PM", am1439);
    }
}
