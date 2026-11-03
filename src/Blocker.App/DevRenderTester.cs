using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Blocker.Core;
using Application = System.Windows.Application;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;

namespace Blocker.App;

public static class DevRenderTester
{
    public static int RunRenderTest(string[] args)
    {
        string outputDir = "screenshots";
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--render-test", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                outputDir = args[i + 1];
                break;
            }
        }

        Directory.CreateDirectory(outputDir);
        Console.WriteLine($"[RenderTest] Output directory: {Path.GetFullPath(outputDir)}");

        // Prevent closing temporary render windows from shutting down the application
        if (Application.Current != null)
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        // 0. Render Obsidian Theme screenshots (New Design)
        ThemeManager.SetDesignTheme(DesignThemeType.Obsidian);
        ThemeManager.SetThemeMode(AppThemeMode.Dark);
        RenderMainWindow(Path.Combine(outputDir, "main-window-obsidian-dark.png"), isSettingsActive: false);
        RenderMainWindow(Path.Combine(outputDir, "main-window-settings-obsidian-dark.png"), isSettingsActive: true);
        ThemeManager.SetThemeMode(AppThemeMode.Light);
        RenderMainWindow(Path.Combine(outputDir, "main-window-obsidian-light.png"), isSettingsActive: false);
        ThemeManager.SetThemeMode(AppThemeMode.Dark);

        // 1. Classic Theme (Original Design)
        ThemeManager.SetDesignTheme(DesignThemeType.Classic);
        ThemeManager.SetThemeMode(AppThemeMode.Dark);

        // 1a. Main window (Dark Mode)
        RenderMainWindow(Path.Combine(outputDir, "main-window.png"), isSettingsActive: false);

        // 1a-edit. Main window with profile in inline edit mode
        RenderMainWindow(Path.Combine(outputDir, "main-window-editing.png"), isSettingsActive: false, isEditingProfile: true);

        // 1b. Main window (Light Mode)
        ThemeManager.SetThemeMode(AppThemeMode.Light);
        RenderMainWindow(Path.Combine(outputDir, "main-window-light.png"), isSettingsActive: false);
        ThemeManager.SetThemeMode(AppThemeMode.Dark);

        // 1c. In-Window Settings (Dark Mode)
        RenderMainWindow(Path.Combine(outputDir, "main-window-settings.png"), isSettingsActive: true);

        // 1d. In-Window Settings (Light Mode)
        ThemeManager.SetThemeMode(AppThemeMode.Light);
        RenderMainWindow(Path.Combine(outputDir, "main-window-settings-light.png"), isSettingsActive: true);
        ThemeManager.SetThemeMode(AppThemeMode.Dark);

        // 2. Block editor (Apps tab)
        RenderBlockEditorCustom(Path.Combine(outputDir, "block-editor.png"), LockMode.None, withSchedule: true, systemWide: true, tab: "Apps");
        RenderBlockEditorCustom(Path.Combine(outputDir, "block-editor-apps.png"), LockMode.None, withSchedule: true, systemWide: true, tab: "Apps");

        // 2b. Block editor (Websites tab)
        RenderBlockEditorCustom(Path.Combine(outputDir, "block-editor-websites.png"), LockMode.None, withSchedule: true, systemWide: false, tab: "Websites");

        // 2c. Block editor (Schedule tab - Dark & Light)
        RenderBlockEditorCustom(Path.Combine(outputDir, "block-editor-schedule.png"), LockMode.None, withSchedule: true, tab: "Schedule");
        ThemeManager.SetThemeMode(AppThemeMode.Light);
        RenderBlockEditorCustom(Path.Combine(outputDir, "block-editor-schedule-light.png"), LockMode.None, withSchedule: true, tab: "Schedule");
        ThemeManager.SetThemeMode(AppThemeMode.Dark);

        // 2d. Block editor - Unlocked radio selected (with schedule)
        RenderBlockEditorCustom(Path.Combine(outputDir, "editor-unlocked.png"), LockMode.None, withSchedule: true, tab: "Lock");

        // 2e. Block editor - Timed Lock (Complete lock for duration)
        RenderBlockEditorCustom(Path.Combine(outputDir, "editor-timed-lock.png"), LockMode.None, withSchedule: false, tab: "TimedLock");
        ThemeManager.SetThemeMode(AppThemeMode.Light);
        RenderBlockEditorCustom(Path.Combine(outputDir, "editor-timed-lock-light.png"), LockMode.None, withSchedule: false, tab: "TimedLock");
        ThemeManager.SetThemeMode(AppThemeMode.Dark);

        // 3. Extension dialog (Dark & Light)
        RenderExtensionDialog(Path.Combine(outputDir, "extension-dialog.png"));
        ThemeManager.SetThemeMode(AppThemeMode.Light);
        RenderExtensionDialog(Path.Combine(outputDir, "extension-dialog-light.png"));
        ThemeManager.SetThemeMode(AppThemeMode.Dark);

        // 3. Block editor - Locked radio selected (with schedule)
        RenderBlockEditorCustom(Path.Combine(outputDir, "editor-locked.png"), LockMode.Locked, withSchedule: true, tab: "Lock");

        // 4. Block editor - Typing challenge radio selected
        RenderBlockEditorCustom(Path.Combine(outputDir, "editor-typing.png"), LockMode.TypingChallenge, withSchedule: true, tab: "Lock");

        // 5. Block editor - Password radio selected (with password fields)
        RenderBlockEditorCustom(Path.Combine(outputDir, "editor-password.png"), LockMode.Password, withSchedule: true, tab: "Lock");

        // 6. Block editor - Locked radio DISABLED (no schedule rows)
        RenderBlockEditorCustom(Path.Combine(outputDir, "editor-locked-disabled.png"), LockMode.None, withSchedule: false, tab: "Lock");

        // 7. Block editor with inline validation errors
        RenderBlockEditorCustom(Path.Combine(outputDir, "block-editor-errors.png"), LockMode.Locked, withSchedule: true, hasErrors: true, tab: "Apps");

        // 8. Pick apps dialog
        RenderPickAppsDialog(Path.Combine(outputDir, "pick-apps.png"), query: null);

        // 9. Pick apps dialog with search filter applied
        RenderPickAppsDialog(Path.Combine(outputDir, "pick-apps-filtered.png"), query: "st");

        // 10. Start-session dialog (Sprint mode)
        RenderStartSessionDialog(Path.Combine(outputDir, "start-session.png"), pomodoroMode: false);

        // 10b. Start-session dialog (Pomodoro Cycles mode)
        RenderStartSessionDialog(Path.Combine(outputDir, "start-session-pomodoro.png"), pomodoroMode: true);

        // 11. Dark confirm dialog
        RenderDarkConfirmDialog(Path.Combine(outputDir, "dark-dialog.png"));

        // 11b. Always-active confirm dialog with Don't show again checkbox
        RenderAlwaysActiveConfirmDialog(Path.Combine(outputDir, "always-active-dialog.png"));

        // 12. Typing challenge unlock dialog
        RenderTypingUnlockDialog(Path.Combine(outputDir, "typing-unlock.png"));

        // 13. Password unlock dialog
        RenderPasswordUnlockDialog(Path.Combine(outputDir, "password-unlock.png"));

        Console.WriteLine("[RenderTest] All screenshots rendered successfully.");
        return 0;
    }

    private static void SaveWindowToPng(Window window, string filePath, int targetWidth, int targetHeight, double verticalScroll = 0)
    {
        window.WindowState = WindowState.Normal;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 10000;
        window.Top = 10000;
        window.Width = targetWidth;

        if (window.Content is FrameworkElement fe)
        {
            fe.Measure(new Size(targetWidth, double.PositiveInfinity));
            int desiredContentHeight = (int)Math.Ceiling(fe.DesiredSize.Height);
            if (desiredContentHeight > targetHeight)
            {
                targetHeight = desiredContentHeight;
            }
        }

        window.Height = targetHeight + 60;
        window.Show();

        if (verticalScroll > 0 && window is BlockEditorDialog bed)
        {
            bed.EditorScrollViewer.ScrollToVerticalOffset(verticalScroll);
        }

        window.Measure(new Size(targetWidth, targetHeight + 60));
        window.Arrange(new Rect(0, 0, targetWidth, targetHeight + 60));
        window.UpdateLayout();

        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

        Visual renderVisual = (window.Content as Visual) ?? window;
        int renderWidth = targetWidth;
        int renderHeight = targetHeight;
        if (window.Content is FrameworkElement contentElem && contentElem.ActualHeight > 0)
        {
            renderWidth = (int)Math.Ceiling(contentElem.ActualWidth);
            renderHeight = (int)Math.Ceiling(contentElem.ActualHeight);
        }

        var rtb = new RenderTargetBitmap(renderWidth, renderHeight, 96, 96, PixelFormats.Pbgra32);
        
        // Draw solid background matching window background to ensure no alpha transparency leaks
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bgBrush = window.Background ?? (ThemeManager.IsDark 
                ? (Brush)new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B)) 
                : (Brush)new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7)));
            dc.DrawRectangle(bgBrush, null, new Rect(0, 0, renderWidth, renderHeight));
        }
        rtb.Render(dv);
        rtb.Render(renderVisual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(filePath))
        {
            encoder.Save(fs);
        }

        window.Close();
        Console.WriteLine($"[RenderTest] Rendered: {Path.GetFileName(filePath)} ({renderWidth}x{renderHeight})");
    }

    private static void RenderMainWindow(string filePath, bool isSettingsActive = false, bool isEditingProfile = false)
    {
        var vm = new MainViewModel();
        vm.IsSettingsViewActive = isSettingsActive;
        vm.IsServiceConnected = true;
        vm.IsServiceElevated = true;
        vm.HostsBlockingOk = true;

        vm.IsSessionActive = true;
        vm.ActiveSessionProfile = "Deep Work";
        vm.ActiveSessionLocked = true;
        vm.ActiveSessionRemainingSeconds = 1485;
        vm.ActiveSessionRemainingFormatted = "24:45";

        // Extension status and warning banners
        vm.ExtensionStatus = new ExtensionStatusData
        {
            Connected = false,
            Browsers = new List<BrowserStatusInfo>
            {
                new() { Name = "Google Chrome", ProcessName = "chrome.exe", Connected = false, IncognitoAllowed = false }
            }
        };
        vm.WarningBanners.Clear();
        vm.WarningBanners.Add("The browser extension is missing in Google Chrome. Websites in active blocks are blocked system-wide until it is restored.");

        // Top strip chips
        vm.ActiveChips.Clear();
        vm.ActiveChips.Add(new ActiveBlockChipItem
        {
            Name = "Deep Work",
            Text = "Deep Work [Manual 24:45 🔒]",
            IsManual = true,
            IsLocked = true,
            BackgroundColor = "#1E293B",
            ForegroundColor = "#93C5FD"
        });
        vm.ActiveChips.Add(new ActiveBlockChipItem
        {
            Name = "Study",
            Text = "Study until 14:00",
            IsManual = false,
            IsLocked = true,
            BackgroundColor = "#14532D",
            ForegroundColor = "#86EFAC"
        });

        vm.Profiles.Clear();

        var p1 = new ProfileViewModel
        {
            Name = "Deep Work",
            Enabled = true,
            IsActive = true,
            ActiveReason = "Manual",
            LockMode = LockMode.Locked,
            LockedNow = true,
            IsReadOnly = true,
            ReadOnlyReason = "Active locked session"
        };
        p1.BlockedProcesses.Add("discord.exe");
        p1.BlockedProcesses.Add("slack.exe");
        p1.WebRules.Add(new WebRule { Pattern = "example.com", Allow = false });
        p1.WebRules.Add(new WebRule { Pattern = "example.org", Allow = false });
        p1.WebRules.Add(new WebRule { Pattern = "example.com/login", Allow = true });
        p1.Schedule.Add(new ScheduleRule { Days = DaysOfWeek.Weekdays, StartMinute = 480, EndMinute = 840 });

        if (isEditingProfile)
        {
            p1.IsEditingName = true;
            p1.EditingName = "Deep Work (Focused)";
        }

        vm.Profiles.Add(p1);

        var p2 = new ProfileViewModel
        {
            Name = "Study",
            Enabled = true,
            IsActive = true,
            ActiveReason = "Schedule",
            LockMode = LockMode.TypingChallenge,
            NextChangeLocal = DateTime.Today.AddHours(14),
            LockedNow = true
        };
        p2.BlockedProcesses.Add("telegram.exe");
        p2.WebRules.Add(new WebRule { Pattern = "example.net", Allow = false });
        p2.WebRules.Add(new WebRule { Pattern = "*.example.edu", Allow = false });
        p2.Schedule.Add(new ScheduleRule { Days = DaysOfWeek.Weekdays, StartMinute = 540, EndMinute = 840 });
        vm.Profiles.Add(p2);

        var p3 = new ProfileViewModel
        {
            Name = "Gaming",
            Enabled = false,
            IsActive = false,
            LockMode = LockMode.Password,
            NextChangeLocal = DateTime.Today.AddDays(2).AddHours(10)
        };
        p3.BlockedProcesses.Add("steam.exe");
        p3.BlockedProcesses.Add("epicgameslauncher.exe");
        p3.WebRules.Add(new WebRule { Pattern = "example.com/games", Allow = false });
        p3.Schedule.Add(new ScheduleRule { Days = DaysOfWeek.Weekend, StartMinute = 600, EndMinute = 1320 });
        vm.Profiles.Add(p3);

        var p4 = new ProfileViewModel
        {
            Name = "Default",
            Enabled = false,
            IsActive = false,
            LockMode = LockMode.None
        };
        p4.BlockedProcesses.Add("notepad.exe");
        vm.Profiles.Add(p4);

        var win = new MainWindow(vm, disableServiceSync: true);
        SaveWindowToPng(win, filePath, 760, 680);
    }

    private static void RenderAppearanceDialog(string filePath)
    {
        var dlg = new AppearanceDialog();
        SaveWindowToPng(dlg, filePath, 500, 520);
    }

    private static void RenderBlockEditorCustom(string filePath, LockMode lockMode, bool withSchedule, bool hasErrors = false, bool systemWide = false, double scrollOffset = 0, string? tab = null)
    {
        var prof = new ProfileViewModel
        {
            Name = "Deep Work",
            LockMode = lockMode,
            SystemWide = systemWide
        };
        prof.BlockedProcesses.Add("discord.exe");
        prof.BlockedProcesses.Add("slack.exe");
        prof.BlockedProcesses.Add("steam.exe");

        if (systemWide)
        {
            prof.WebRules.Add(new WebRule { Pattern = "example.com", Allow = false });
            prof.WebRules.Add(new WebRule { Pattern = "example.org", Allow = false });
        }
        else
        {
            prof.WebRules.Add(new WebRule { Pattern = "example.com", Allow = false });
            prof.WebRules.Add(new WebRule { Pattern = "example.com/news", Allow = false });
            prof.WebRules.Add(new WebRule { Pattern = "*.example.org", Allow = false });
            prof.WebRules.Add(new WebRule { Pattern = "example.com/allowed", Allow = true });
        }

        if (withSchedule)
        {
            prof.Schedule.Add(new ScheduleRule { Days = DaysOfWeek.Weekdays, StartMinute = 480, EndMinute = 1020 });
            prof.Schedule.Add(new ScheduleRule { Days = DaysOfWeek.Weekend, StartMinute = 600, EndMinute = 840 });
            prof.Allowance = new AllowanceConfig
            {
                Enabled = true,
                DailyAllowanceMinutes = 30,
                OnlyWithinSchedule = true
            };
        }

        int initialTab = string.Equals(tab, "Websites", StringComparison.OrdinalIgnoreCase) ? 1
            : (string.Equals(tab, "Schedule", StringComparison.OrdinalIgnoreCase) ? 2
            : (string.Equals(tab, "Lock", StringComparison.OrdinalIgnoreCase) ? 3 : 0));
        var editor = new BlockEditorDialog(prof, initialTab);

        if (!string.IsNullOrEmpty(tab))
        {
            if (string.Equals(tab, "TimedLock", StringComparison.OrdinalIgnoreCase))
            {
                editor.SelectTimedLockForTest(60);
            }
            else
            {
                editor.SelectTab(tab);
            }
        }

        if (hasErrors)
        {
            editor.SetValidationErrors(
                "A profile named 'Deep Work' already exists.",
                "'explorer.exe' is a protected system process and cannot be blocked.",
                "Invalid format. Enter a domain like 'example.com'.");
        }

        SaveWindowToPng(editor, filePath, 780, 840, scrollOffset);
    }

    private static void RenderExtensionDialog(string filePath)
    {
        var status = new StatusData
        {
            ExtensionStatus = new ExtensionStatusData
            {
                Connected = true,
                Browsers = new List<BrowserStatusInfo>
                {
                    new() { Name = "Google Chrome", ProcessName = "chrome.exe", Connected = true, IncognitoAllowed = true },
                    new() { Name = "Microsoft Edge", ProcessName = "msedge.exe", Connected = false, IncognitoAllowed = false }
                }
            }
        };

        var dlg = new ExtensionDialog(status);
        SaveWindowToPng(dlg, filePath, 680, 780);
    }

    private static void RenderPickAppsDialog(string filePath, string? query)
    {
        var sampleItems = new List<AppPickerItem>
        {
            // Popular distractions
            new() { DisplayName = "Discord", ExeName = "Discord.exe", Category = AppPickerLogic.CatPopular },
            new() { DisplayName = "Steam", ExeName = "steam.exe", Category = AppPickerLogic.CatPopular },
            new() { DisplayName = "Spotify", ExeName = "Spotify.exe", Category = AppPickerLogic.CatPopular },

            // Running apps
            new() { DisplayName = "Google Chrome", ExeName = "chrome.exe", Path = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", Category = AppPickerLogic.CatRunning },
            new() { DisplayName = "Visual Studio Code", ExeName = "Code.exe", Path = "C:\\Users\\virex\\AppData\\Local\\Programs\\Microsoft VS Code\\Code.exe", Category = AppPickerLogic.CatRunning },

            // Installed apps
            new() { DisplayName = "VLC Media Player", ExeName = "vlc.exe", Path = "C:\\Program Files\\VideoLAN\\VLC\\vlc.exe", Category = AppPickerLogic.CatInstalled },
            new() { DisplayName = "Telegram", ExeName = "Telegram.exe", Path = "C:\\Users\\virex\\AppData\\Roaming\\Telegram Desktop\\Telegram.exe", Category = AppPickerLogic.CatInstalled },
            new() { DisplayName = "7-Zip File Manager", ExeName = "7zFM.exe", Path = "C:\\Program Files\\7-Zip\\7zFM.exe", Category = AppPickerLogic.CatInstalled }
        };

        var alreadyBlocked = new[] { "Discord.exe" };
        var dlg = new AppPickerDialog(alreadyBlocked, sampleItems);

        if (!string.IsNullOrEmpty(query))
        {
            dlg.SetSearchQuery(query);
        }
        else
        {
            if (dlg.AppsListBox.ItemsSource is List<AppPickerRow> rows)
            {
                var steam = rows.FirstOrDefault(r => string.Equals(r.ExeName, "steam.exe", StringComparison.OrdinalIgnoreCase));
                if (steam != null) steam.IsSelected = true;

                var chrome = rows.FirstOrDefault(r => string.Equals(r.ExeName, "chrome.exe", StringComparison.OrdinalIgnoreCase));
                if (chrome != null) chrome.IsSelected = true;
            }
        }

        dlg.ShowActivated = false;
        dlg.WindowStartupLocation = WindowStartupLocation.Manual;
        dlg.Left = 10000;
        dlg.Top = 10000;
        dlg.Width = 640;
        dlg.Height = 640;
        dlg.Show();

        dlg.Measure(new Size(640, 640));
        dlg.Arrange(new Rect(0, 0, 640, 640));
        dlg.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

        int totalDataRows = dlg.AppsListBox.Items.Count;
        int realizedCount = 0;
        for (int i = 0; i < totalDataRows; i++)
        {
            var container = dlg.AppsListBox.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
            if (container != null)
            {
                realizedCount++;
            }
        }

        Console.WriteLine($"[RenderTest] Pick apps list (query='{query}'): total data rows={totalDataRows}, realized containers={realizedCount}");

        if (realizedCount == 0 || realizedCount != totalDataRows)
        {
            Console.Error.WriteLine($"[RenderTest ERROR] Assertion failed: Pick apps list realized {realizedCount} rows, expected {totalDataRows}.");
            Environment.Exit(1);
        }

        Visual renderVisual = (dlg.Content as Visual) ?? dlg;
        int renderWidth = 640;
        int renderHeight = 580;
        if (dlg.Content is FrameworkElement contentElem && contentElem.ActualHeight > 0)
        {
            renderWidth = (int)Math.Ceiling(contentElem.ActualWidth);
            renderHeight = (int)Math.Ceiling(contentElem.ActualHeight);
        }

        var rtb = new RenderTargetBitmap(renderWidth, renderHeight, 96, 96, PixelFormats.Pbgra32);
        
        // Draw solid background matching window background to ensure no alpha transparency leaks
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bgBrush = dlg.Background ?? (ThemeManager.IsDark 
                ? (Brush)new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B)) 
                : (Brush)new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7)));
            dc.DrawRectangle(bgBrush, null, new Rect(0, 0, renderWidth, renderHeight));
        }
        rtb.Render(dv);
        rtb.Render(renderVisual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(filePath))
        {
            encoder.Save(fs);
        }

        dlg.Close();
        Console.WriteLine($"[RenderTest] Rendered: {Path.GetFileName(filePath)} ({renderWidth}x{renderHeight})");
    }

    private static void RenderStartSessionDialog(string filePath, bool pomodoroMode = false)
    {
        var dlg = new StartSessionDialog("1", "Deep Work");
        if (pomodoroMode)
        {
            dlg.rbModePomodoro.IsChecked = true;
            dlg.PomoFocusInput.Text = "25";
            dlg.PomoBreakInput.Text = "5";
            dlg.PomoCyclesInput.Text = "4";
            dlg.PomoLongBreakInput.Text = "15";
            dlg.LockedCheckBox.IsChecked = true;
            SaveWindowToPng(dlg, filePath, 440, 480);
        }
        else
        {
            dlg.DurationInput.Text = "25";
            dlg.LockedCheckBox.IsChecked = true;
            SaveWindowToPng(dlg, filePath, 440, 360);
        }
    }

    private static void RenderDarkConfirmDialog(string filePath)
    {
        var dlg = DarkDialogWindow.CreateDialogWindow(
            "Are you sure you want to stop this session? It is currently LOCKED and will require an emergency unlock.",
            "Confirm Stop",
            isConfirmation: true,
            primaryText: "Stop Session",
            secondaryText: "Cancel");

        SaveWindowToPng(dlg, filePath, 440, 220);
    }

    private static void RenderAlwaysActiveConfirmDialog(string filePath)
    {
        var dlg = DarkDialogWindow.CreateDialogWindow(
            "Block 'Default' has no schedule rules.\n\nTurning it On will immediately block all configured apps and websites at all times until turned Off.\n\nTurn On now?",
            "Turn On Always-Active Block",
            isConfirmation: true,
            primaryText: "Turn On",
            secondaryText: "Cancel",
            showDontShowAgain: true);

        SaveWindowToPng(dlg, filePath, 440, 240);
    }

    private static void RenderTypingUnlockDialog(string filePath)
    {
        string challenge = "29b8c7e4f3a1d5h6j7k8m9n2p3q4r5s6t7u8v9w2x3y4z5A6B7C8D9E2F3G4H5J6K7L8M9N2P3Q4R5S6";
        var dlg = new TypingChallengeDialog(challenge);
        dlg.InputTextBox.Text = "29b8c7e4f3a1d5h6j7k8m9n2";
        SaveWindowToPng(dlg, filePath, 580, 380);
    }

    private static void RenderPasswordUnlockDialog(string filePath)
    {
        var dlg = new PasswordUnlockDialog("Deep Work");
        dlg.PasswordInput.Password = "supersecret";
        SaveWindowToPng(dlg, filePath, 440, 260);
    }
}
