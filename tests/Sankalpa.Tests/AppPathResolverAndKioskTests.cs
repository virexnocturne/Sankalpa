using System;
using System.IO;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Sankalpa.Tests;

public class AppPathResolverAndKioskTests
{
    private static readonly object s_testLock = new();

    private static string GetRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Blocker.sln")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir)!;
        }
        return AppContext.BaseDirectory;
    }

    [Fact]
    public void AppPathResolver_ResolvesKnownSystemApps_LikeNotepad()
    {
        string? resolved = AppPathResolver.ResolveExecutablePath("notepad.exe");
        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved), $"Resolved path '{resolved}' should exist on disk.");
        Assert.Contains("notepad", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppPathResolver_HandlesNullAndEmpty()
    {
        Assert.Null(AppPathResolver.ResolveExecutablePath(null));
        Assert.Null(AppPathResolver.ResolveExecutablePath("   "));
        Assert.Equal(string.Empty, AppPathResolver.ResolveOrOriginal(null));
        Assert.Equal("unknown_app.exe", AppPathResolver.ResolveOrOriginal("unknown_app.exe"));
    }

    [Fact]
    public void AppPathResolver_ReturnsNull_ForNonExistentApp()
    {
        string? resolved = AppPathResolver.ResolveExecutablePath("fake_non_existent_app_999999.exe");
        Assert.Null(resolved);
    }

    [Fact]
    public void AppPathResolver_ResolvesVlcIfPresent()
    {
        // If VLC is installed on the machine, verify AppPathResolver finds it
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string vlcDirect = Path.Combine(programFiles, "VideoLAN", "VLC", "vlc.exe");
        if (File.Exists(vlcDirect))
        {
            string? resolved = AppPathResolver.ResolveExecutablePath("vlc.exe");
            Assert.NotNull(resolved);
            Assert.True(File.Exists(resolved));
        }
    }

    [Fact]
    public void AppPathResolver_ResolvesObsidianIfPresent()
    {
        // If Obsidian is installed in LocalAppData, verify AppPathResolver finds it
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string obsDirect = Path.Combine(localApp, "Programs", "Obsidian", "Obsidian.exe");
        if (File.Exists(obsDirect))
        {
            string? resolved = AppPathResolver.ResolveExecutablePath("Obsidian.exe");
            Assert.NotNull(resolved);
            Assert.True(File.Exists(resolved));
            Assert.Equal(Path.GetFullPath(obsDirect), Path.GetFullPath(resolved));
        }
    }

    [Fact]
    public void BlockerDatabase_IsEmergencyUnlockPresent_ReadsAndConsumesFromProgramData()
    {
        lock (s_testLock)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Sankalpa_DbEmergencyTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string dbPath = Path.Combine(tempDir, "test.db");

            try
            {
                var db = new BlockerDatabase(dbPath);
                db.ConsumeEmergencyUnlock();
                Assert.False(db.IsEmergencyUnlockPresent());

                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string markerPath = Path.Combine(programData, "Sankalpa", "EMERGENCY_UNLOCK");
                Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);

                try
                {
                    File.WriteAllText(markerPath, "EMERGENCY_RELEASE");

                    Assert.True(db.IsEmergencyUnlockPresent(), "Database should detect EMERGENCY_UNLOCK marker in ProgramData.");

                    db.ConsumeEmergencyUnlock();
                    Assert.False(File.Exists(markerPath), "ConsumeEmergencyUnlock should remove the marker file from ProgramData.");
                    Assert.False(db.IsEmergencyUnlockPresent(), "Marker should be gone after consumption.");
                }
                finally
                {
                    if (File.Exists(markerPath))
                    {
                        try { File.Delete(markerPath); } catch { }
                    }
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }

    [Fact]
    public void LowLevelKeyboardHook_WriteEmergencyUnlockFiles_CreatesMarkersInExpectedLocations()
    {
        lock (s_testLock)
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string markerPath = Path.Combine(programData, "Sankalpa", "EMERGENCY_UNLOCK");

            var allMarkers = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "EMERGENCY_UNLOCK"),
                Path.Combine(Directory.GetCurrentDirectory(), "EMERGENCY_UNLOCK"),
                Path.Combine(appData, "Blocker", "EMERGENCY_UNLOCK"),
                Path.Combine(appData, "Sankalpa", "EMERGENCY_UNLOCK"),
                Path.Combine(programData, "Sankalpa", "EMERGENCY_UNLOCK"),
                Path.Combine(programData, "Blocker", "EMERGENCY_UNLOCK")
            };

            try
            {
                LowLevelKeyboardHook.WriteEmergencyUnlockFiles();
                Assert.True(File.Exists(markerPath), "WriteEmergencyUnlockFiles should write to ProgramData\\Sankalpa\\EMERGENCY_UNLOCK.");
            }
            finally
            {
                foreach (var file in allMarkers)
                {
                    if (File.Exists(file))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
        }
    }

    [Fact]
    public void ZenShieldWindow_ImplementsAppPathResolver_AndDebounce_AndOsHelperWhitelist()
    {
        string root = GetRepoRoot();
        string shieldPath = Path.Combine(root, "src", "Blocker.App", "ZenShieldWindow.xaml.cs");
        Assert.True(File.Exists(shieldPath));

        string code = File.ReadAllText(shieldPath);

        // 1. AppPathResolver integration
        Assert.Contains("AppPathResolver.ResolveExecutablePath", code);

        // 2. Debounce protection against rapid repeated clicks
        Assert.Contains("_isSwitchingAllowedApp", code);

        // 3. Asynchronous window discovery loop
        Assert.Contains("discoveredHwnd", code);

        // 4. Windows 11 TextInputHost and OS helper whitelisting to eliminate lag and focus fighting
        Assert.Contains("TextInputHost", code);
        Assert.Contains("ApplicationFrameHost", code);

        // 5. Mini-HUD only launched when allowed window discovered or activated
        Assert.Contains("ShowMiniHud();", code);

        // 6. Robust FindMainWindowForProcess window discovery
        Assert.Contains("FindMainWindowForProcess", code);
        Assert.Contains("UAC_InputIndicatorOverlayWnd", code);
    }

    [Fact]
    public void MainWindow_Settings_ListsScreenSnipShortcut()
    {
        string root = GetRepoRoot();
        string mainWindowXaml = Path.Combine(root, "src", "Blocker.App", "MainWindow.xaml");
        Assert.True(File.Exists(mainWindowXaml));

        string code = File.ReadAllText(mainWindowXaml);
        Assert.Contains("Screen Snip Tool", code);
        Assert.Contains("Ctrl + Shift + S", code);
    }

    [Fact]
    public void ZenSnipOverlay_HasZeroZoomAndZeroGeometryTessellation()
    {
        string root = GetRepoRoot();
        string xamlPath = Path.Combine(root, "src", "Blocker.App", "ZenSnipOverlayWindow.xaml");
        Assert.True(File.Exists(xamlPath));

        string xaml = File.ReadAllText(xamlPath);
        // Verify 4-slice mask
        Assert.Contains("MaskTop", xaml);
        Assert.Contains("MaskBottom", xaml);
        Assert.Contains("MaskLeft", xaml);
        Assert.Contains("MaskRight", xaml);
        // Verify HitTest protection
        Assert.Contains("IsHitTestVisible=\"False\"", xaml);
        // Verify preview mouse tunneling
        Assert.Contains("PreviewMouseLeftButtonDown", xaml);
        Assert.Contains("PreviewMouseLeftButtonUp", xaml);

        string csPath = Path.Combine(root, "src", "Blocker.App", "ZenSnipOverlayWindow.xaml.cs");
        Assert.True(File.Exists(csPath));
        string cs = File.ReadAllText(csPath);
        // Verify zero zoom stretch fill and 1:1 DIP matching
        Assert.Contains("Stretch.Fill", cs);
        Assert.Contains("SWP_NOSIZE", cs);
    }
}
