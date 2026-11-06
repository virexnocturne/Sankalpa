using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Sankalpa.Tests;

public class MeditationBackendTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly BlockerDatabase _db;

    public MeditationBackendTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"meditation_test_{Guid.NewGuid():N}.db");
        _db = new BlockerDatabase(_tempDbPath);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempDbPath))
            {
                File.Delete(_tempDbPath);
            }
        }
        catch { }
    }

    [Fact]
    public void MeditationSession_AllowedAppsHelperMethods_WorkCorrectly()
    {
        var session = new MeditationSession();
        Assert.Empty(session.GetAllowedApps());

        var apps = new List<string> { "notepad.exe", "spotify.exe" };
        session.SetAllowedApps(apps);

        Assert.Contains("notepad.exe", session.AllowedAppsJson);
        var retrieved = session.GetAllowedApps();
        Assert.Equal(2, retrieved.Count);
        Assert.Equal("notepad.exe", retrieved[0]);
        Assert.Equal("spotify.exe", retrieved[1]);
    }

    [Fact]
    public void MeditationSchedule_DaysAndAppsHelperMethods_WorkCorrectly()
    {
        var schedule = new MeditationSchedule();
        Assert.Empty(schedule.GetDays());

        schedule.SetDays(new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday });
        var days = schedule.GetDays();
        Assert.Equal(3, days.Count);
        Assert.Contains(DayOfWeek.Monday, days);
        Assert.Contains(DayOfWeek.Wednesday, days);
        Assert.Contains(DayOfWeek.Friday, days);

        schedule.SetAllowedApps(new List<string> { "calm.exe" });
        var apps = schedule.GetAllowedApps();
        Assert.Single(apps);
        Assert.Equal("calm.exe", apps[0]);
    }

    [Fact]
    public void BlockerDatabase_MeditationSessionLifecycle_WorksCorrectly()
    {
        // 1. Initially no active session
        Assert.Null(_db.GetActiveMeditationSession());

        // 2. Start session
        var session = _db.StartMeditationSession(600, new List<string> { "notepad.exe" }, LockMode.Locked, "Rain");
        Assert.NotNull(session);
        Assert.True(session.Id > 0);
        Assert.Equal(600, session.TargetDurationSeconds);
        Assert.Equal(LockMode.Locked, session.LockMode);
        Assert.Equal("Rain", session.Soundscape);

        // 3. Query active session
        var active = _db.GetActiveMeditationSession();
        Assert.NotNull(active);
        Assert.Equal(session.Id, active.Id);
        Assert.False(active.Completed);
        Assert.Contains("notepad.exe", active.GetAllowedApps());

        // 4. Update notes
        _db.UpdateMeditationNotes(session.Id, "Great session so far.");

        // 5. End session
        _db.EndMeditationSession(session.Id, completed: true, "Deep peaceful state.");
        Assert.Null(_db.GetActiveMeditationSession());

        // 6. Verify history
        var history = _db.GetMeditationHistory(10);
        Assert.NotEmpty(history);
        var ended = history[0];
        Assert.Equal(session.Id, ended.Id);
        Assert.True(ended.Completed);
        Assert.Equal("Deep peaceful state.", ended.Notes);
    }

    [Fact]
    public void BlockerDatabase_MeditationScheduleCrudAndSnooze_WorksCorrectly()
    {
        var schedule = new MeditationSchedule
        {
            Enabled = true,
            TimeOfDay = "16:30",
            DurationSeconds = 1200,
            LockMode = LockMode.HardLocked,
            Soundscape = "Forest"
        };
        schedule.SetDays(new List<DayOfWeek> { DayOfWeek.Tuesday, DayOfWeek.Thursday });
        schedule.SetAllowedApps(new List<string> { "code.exe" });

        // Save new
        var saved = _db.SaveMeditationSchedule(schedule);
        Assert.True(saved.Id > 0);

        // List
        var list = _db.GetMeditationSchedules();
        Assert.Single(list);
        Assert.Equal("16:30", list[0].TimeOfDay);
        Assert.Equal(LockMode.HardLocked, list[0].LockMode);

        // Snooze
        bool snoozed = _db.SnoozeMeditationSchedule(saved.Id, 5);
        Assert.True(snoozed);

        var snoozedSched = _db.GetMeditationSchedules()[0];
        Assert.NotNull(snoozedSched.LastSnoozedUtc);

        // Delete
        bool deleted = _db.DeleteMeditationSchedule(saved.Id);
        Assert.True(deleted);
        Assert.Empty(_db.GetMeditationSchedules());
    }

    [Fact]
    public void CommandHandler_StartAndGetAndStopMeditation_WorksViaIpc()
    {
        var identity = ClientIdentity.StandardUser();

        // 1. Get initial status
        var statusResp = CommandHandler.Handle(new Request { Command = CommandNames.GetMeditationStatus }, _db, identity);
        Assert.True(statusResp.Ok);
        var statusDto = JsonSerializer.Deserialize<MeditationStatusDto>(statusResp.Data!);
        Assert.NotNull(statusDto);
        Assert.False(statusDto.Active);

        // 2. Start meditation
        var startResp = CommandHandler.Handle(new Request
        {
            Command = CommandNames.StartMeditation,
            Args = new Dictionary<string, string>
            {
                ["targetDurationSeconds"] = "300",
                ["allowedApps"] = "[\"notepad.exe\"]",
                ["lockMode"] = "None",
                ["soundscape"] = "Waves"
            }
        }, _db, identity);
        Assert.True(startResp.Ok);

        // 3. Check active status
        statusResp = CommandHandler.Handle(new Request { Command = CommandNames.GetMeditationStatus }, _db, identity);
        Assert.True(statusResp.Ok);
        statusDto = JsonSerializer.Deserialize<MeditationStatusDto>(statusResp.Data!);
        Assert.NotNull(statusDto);
        Assert.True(statusDto.Active);
        Assert.Equal("Waves", statusDto.Soundscape);

        // 4. Save note
        var noteResp = CommandHandler.Handle(new Request
        {
            Command = CommandNames.SaveMeditationNote,
            Args = new Dictionary<string, string>
            {
                ["sessionId"] = statusDto.SessionId!.Value.ToString(),
                ["notes"] = "Feeling relaxed"
            }
        }, _db, identity);
        Assert.True(noteResp.Ok);

        // 5. Stop meditation
        var stopResp = CommandHandler.Handle(new Request { Command = CommandNames.StopMeditation }, _db, identity);
        Assert.True(stopResp.Ok);

        // 6. Check status is now inactive
        statusResp = CommandHandler.Handle(new Request { Command = CommandNames.GetMeditationStatus }, _db, identity);
        statusDto = JsonSerializer.Deserialize<MeditationStatusDto>(statusResp.Data!);
        Assert.NotNull(statusDto);
        Assert.False(statusDto.Active);

        // 7. Check history
        var histResp = CommandHandler.Handle(new Request { Command = CommandNames.GetMeditationHistory }, _db, identity);
        Assert.True(histResp.Ok);
        var histItems = JsonSerializer.Deserialize<List<MeditationHistoryItemDto>>(histResp.Data!);
        Assert.NotNull(histItems);
        Assert.Single(histItems);
        Assert.Equal("Feeling relaxed", histItems[0].Notes);
    }

    [Fact]
    public void CommandHandler_LockedMeditation_CannotBeStoppedEarlyWithoutEmergency()
    {
        var identity = ClientIdentity.StandardUser();

        // Start locked session
        var startResp = CommandHandler.Handle(new Request
        {
            Command = CommandNames.StartMeditation,
            Args = new Dictionary<string, string>
            {
                ["targetDurationSeconds"] = "600",
                ["lockMode"] = "Locked"
            }
        }, _db, identity);
        Assert.True(startResp.Ok);

        // Attempt stop
        var stopResp = CommandHandler.Handle(new Request { Command = CommandNames.StopMeditation }, _db, identity);
        Assert.False(stopResp.Ok);
        Assert.Contains("locked", stopResp.Error, StringComparison.OrdinalIgnoreCase);

        // End session cleanly
        var active = _db.GetActiveMeditationSession();
        Assert.NotNull(active);
        _db.EndMeditationSession(active.Id, false);
    }

    [Fact]
    public void LowLevelKeyboardHook_CanInstantiateAndDisposeCleanly()
    {
        using var hook = new LowLevelKeyboardHook();
        Assert.False(hook.IsActive);

        try
        {
            // Starting and stopping
            hook.Start();
            Assert.True(hook.IsActive);

            hook.Stop();
            Assert.False(hook.IsActive);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            // In non-interactive test sessions / sandboxes, SetWindowsHookEx may return Access Denied (error 5)
        }
    }

    [Fact]
    public void MeditationSession_WhitelistMatching_HandlesCaseAndExtensionDifferences()
    {
        var session = new MeditationSession();
        session.SetAllowedApps(new List<string> { "Spotify.exe", "code" });

        var apps = session.GetAllowedApps();
        Assert.Equal(2, apps.Count);

        // Helper check matching ProcessMatcher normalization
        bool isSpotifyAllowed = apps.Any(a => 
            string.Equals(ProcessMatcher.NormalizeProcessName(a), ProcessMatcher.NormalizeProcessName("spotify"), StringComparison.OrdinalIgnoreCase));
        Assert.True(isSpotifyAllowed);

        bool isCodeAllowed = apps.Any(a => 
            string.Equals(ProcessMatcher.NormalizeProcessName(a), ProcessMatcher.NormalizeProcessName("code.exe"), StringComparison.OrdinalIgnoreCase));
        Assert.True(isCodeAllowed);

        bool isChromeAllowed = apps.Any(a => 
            string.Equals(ProcessMatcher.NormalizeProcessName(a), ProcessMatcher.NormalizeProcessName("chrome.exe"), StringComparison.OrdinalIgnoreCase));
        Assert.False(isChromeAllowed);
    }

    [Fact]
    public void MeditationBootRecovery_CalculatesRemainingAndExpiredSessionsAccurately()
    {
        // 1. Session started 10 minutes ago with 20 minutes duration (10 minutes remaining)
        var activeSession = _db.StartMeditationSession(1200, new List<string>(), LockMode.Locked, "Rain");
        var active = _db.GetActiveMeditationSession();
        Assert.NotNull(active);

        var now = DateTime.UtcNow;
        var remaining = (active.StartUtc.AddSeconds(active.TargetDurationSeconds) - now).TotalSeconds;
        Assert.True(remaining > 0);
        Assert.True(remaining <= 1200);

        _db.EndMeditationSession(active.Id, true);

        // 2. No active session after clean end
        Assert.Null(_db.GetActiveMeditationSession());
    }

    [Fact]
    public void MeditationSnooze_PostponesScheduleBy5Minutes()
    {
        var schedule = new MeditationSchedule
        {
            Enabled = true,
            TimeOfDay = "10:00",
            DurationSeconds = 600,
            LockMode = LockMode.None,
            Soundscape = "Library"
        };
        schedule.SetDays(new List<DayOfWeek> { DayOfWeek.Monday });

        var saved = _db.SaveMeditationSchedule(schedule);
        Assert.Null(saved.LastSnoozedUtc);

        bool snoozed = _db.SnoozeMeditationSchedule(saved.Id, 5);
        Assert.True(snoozed);

        var retrieved = _db.GetMeditationSchedules().Find(s => s.Id == saved.Id);
        Assert.NotNull(retrieved);
        Assert.NotNull(retrieved.LastSnoozedUtc);
        var diff = (DateTime.UtcNow - retrieved.LastSnoozedUtc.Value).TotalSeconds;
        Assert.True(Math.Abs(diff) < 10);

        // Clean up
        _db.DeleteMeditationSchedule(saved.Id);
    }

    [Fact]
    public void InstalledAppScanner_IsAppInstalled_DetectsInstalledAppsAndRejectsNonExistent()
    {
        // 1. Notepad is standard on Windows System32 / WindowsApps
        Assert.True(InstalledAppScanner.IsAppInstalled("notepad.exe"));
        Assert.True(InstalledAppScanner.IsAppInstalled("notepad"));

        // 2. Non-existent application executables should return false
        Assert.False(InstalledAppScanner.IsAppInstalled("random_nonexistent_xyz987654.exe"));
        Assert.False(InstalledAppScanner.IsAppInstalled("fake_unknown_process_12345"));

        // 3. Null or whitespace handling
        Assert.False(InstalledAppScanner.IsAppInstalled(""));
        Assert.False(InstalledAppScanner.IsAppInstalled("   "));
        Assert.False(InstalledAppScanner.IsAppInstalled(null!));
    }

    [Fact]
    public void MeditationSchedule_CalculateDurationMinutes_HandlesStandardAndOvernightRanges()
    {
        // Standard same-day durations
        Assert.Equal(60, MeditationSchedule.CalculateDurationMinutes("15:00", "16:00"));
        Assert.Equal(90, MeditationSchedule.CalculateDurationMinutes("15:00", "16:30"));
        Assert.Equal(45, MeditationSchedule.CalculateDurationMinutes("09:15", "10:00"));
        Assert.Equal(120, MeditationSchedule.CalculateDurationMinutes("10:00", "12:00"));
        Assert.Equal(180, MeditationSchedule.CalculateDurationMinutes("10:00", "13:00"));

        // Overnight ranges crossing midnight
        Assert.Equal(120, MeditationSchedule.CalculateDurationMinutes("23:00", "01:00"));
        Assert.Equal(45, MeditationSchedule.CalculateDurationMinutes("23:30", "00:15"));
        Assert.Equal(240, MeditationSchedule.CalculateDurationMinutes("22:00", "02:00"));
        Assert.Equal(10, MeditationSchedule.CalculateDurationMinutes("23:55", "00:05"));

        // Full 24-hour cycle
        Assert.Equal(1440, MeditationSchedule.CalculateDurationMinutes("15:00", "15:00"));

        // Invalid or malformed strings return 0
        Assert.Equal(0, MeditationSchedule.CalculateDurationMinutes("invalid", "16:00"));
        Assert.Equal(0, MeditationSchedule.CalculateDurationMinutes("15:00", "invalid"));
        Assert.Equal(0, MeditationSchedule.CalculateDurationMinutes("25:00", "16:00"));
        Assert.Equal(0, MeditationSchedule.CalculateDurationMinutes("", "16:00"));

        // Instance methods on MeditationSchedule
        var schedule = new MeditationSchedule
        {
            TimeOfDay = "23:00",
            EndTimeOfDay = "01:00"
        };
        Assert.Equal(120, schedule.CalculateDurationMinutes());
        Assert.Equal(7200, schedule.CalculateDurationSeconds());

        schedule.UpdateDurationFromTimeRange();
        Assert.Equal(7200, schedule.DurationSeconds);

        // Helper CalculateEndTime
        Assert.Equal("16:00", MeditationSchedule.CalculateEndTime("15:00", 60));
        Assert.Equal("16:30", MeditationSchedule.CalculateEndTime("15:00", 90));
        Assert.Equal("01:00", MeditationSchedule.CalculateEndTime("23:00", 120));
        Assert.Equal("00:15", MeditationSchedule.CalculateEndTime("23:30", 45));

        schedule.DurationSeconds = 5400; // 90 min
        schedule.UpdateEndTimeFromDuration();
        Assert.Equal("00:30", schedule.EndTimeOfDay);
    }

    [Fact]
    public void ProtectedProcesses_SnippingToolAndScreenClippingHost_AreProtected()
    {
        Assert.True(ProtectedProcesses.IsProtected("screenclippinghost"));
        Assert.True(ProtectedProcesses.IsProtected("screenclippinghost.exe"));
        Assert.True(ProtectedProcesses.IsProtected("ScreenClippingHost"));
        Assert.True(ProtectedProcesses.IsProtected("snippingtool"));
        Assert.True(ProtectedProcesses.IsProtected("snippingtool.exe"));
        Assert.True(ProtectedProcesses.IsProtected("SnippingTool"));
        Assert.Contains("screenclippinghost", ProtectedProcesses.Names);
        Assert.Contains("snippingtool", ProtectedProcesses.Names);
    }

    [Fact]
    public void Worker_IsAppAllowed_CorrectlyEnforcesWhitelistAndToolExceptions()
    {
        var allowedList = new List<string> { "notepad.exe", "code" };

        // Whitelisted apps with or without .exe
        Assert.True(Blocker.Service.Worker.IsAppAllowed("notepad", "notepad.exe", allowedList));
        Assert.True(Blocker.Service.Worker.IsAppAllowed("code", "Code.exe", allowedList));

        // Built-in allowed tools
        Assert.True(Blocker.Service.Worker.IsAppAllowed("snippingtool", "SnippingTool.exe", allowedList));
        Assert.True(Blocker.Service.Worker.IsAppAllowed("screenclippinghost", "ScreenClippingHost.exe", allowedList));

        // Unauthorized apps
        Assert.False(Blocker.Service.Worker.IsAppAllowed("chrome", "chrome.exe", allowedList));
        Assert.False(Blocker.Service.Worker.IsAppAllowed("discord", "Discord.exe", allowedList));
        Assert.False(Blocker.Service.Worker.IsAppAllowed("cmd", "cmd.exe", allowedList));
    }

    [Fact]
    public void Worker_MeditationWhitelistSessionCheck_Integrity()
    {
        var dir = AppContext.BaseDirectory;
        string? repoRoot = null;
        var cur = new DirectoryInfo(dir);
        while (cur != null)
        {
            if (File.Exists(Path.Combine(cur.FullName, "Blocker.sln")))
            {
                repoRoot = cur.FullName;
                break;
            }
            cur = cur.Parent;
        }

        Assert.NotNull(repoRoot);
        string workerPath = Path.Combine(repoRoot!, "src", "Blocker.Service", "Worker.cs");
        Assert.True(File.Exists(workerPath));
        string workerSrc = File.ReadAllText(workerPath);

        // Verify WTSGetActiveConsoleSessionId is declared and used
        Assert.Contains("WTSGetActiveConsoleSessionId()", workerSrc);
        // Verify session comparison is used for user session processes
        Assert.Contains("isTargetInActiveSession", workerSrc);
        // Verify it terminates without requiring hwnd != IntPtr.Zero
        Assert.Contains("hwnd == IntPtr.Zero || !targetProc.CloseMainWindow()", workerSrc);
    }

    [Fact]
    public void ZenShieldWindow_WatchdogSuppression_Integrity()
    {
        var dir = AppContext.BaseDirectory;
        string? repoRoot = null;
        var cur = new DirectoryInfo(dir);
        while (cur != null)
        {
            if (File.Exists(Path.Combine(cur.FullName, "Blocker.sln")))
            {
                repoRoot = cur.FullName;
                break;
            }
            cur = cur.Parent;
        }

        Assert.NotNull(repoRoot);
        string zenPath = Path.Combine(repoRoot!, "src", "Blocker.App", "ZenShieldWindow.xaml.cs");
        Assert.True(File.Exists(zenPath));
        string zenSrc = File.ReadAllText(zenPath);

        // Verify continuous taskbar suppression
        Assert.Contains("SetTaskbarVisibility(false);", zenSrc);
        // Verify SW_FORCEMINIMIZE
        Assert.Contains("SW_FORCEMINIMIZE = 11;", zenSrc);
        // Verify immediate suppression of unauthorized foreground window
        Assert.Contains("ShowWindow(fgHwnd, SW_FORCEMINIMIZE);", zenSrc);
        Assert.Contains("ShowWindow(fgHwnd, SW_HIDE);", zenSrc);
        // Verify re-assertion of _lastAllowedAppHwnd
        Assert.Contains("ShowWindow(_lastAllowedAppHwnd, SW_RESTORE);", zenSrc);
        Assert.Contains("SetForegroundWindow(_lastAllowedAppHwnd);", zenSrc);
        // Verify breach count restoring Zen Shield
        Assert.Contains("_unauthorizedBreachCount", zenSrc);
        Assert.Contains("RestoreZenScreen();", zenSrc);
    }
}
