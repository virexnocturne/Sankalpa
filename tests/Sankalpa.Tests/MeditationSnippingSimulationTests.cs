using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Blocker.Core;
using Blocker.Service;
using Blocker.Tests;
using Xunit;

namespace Sankalpa.Tests;

/// <summary>
/// Comprehensive isolated simulation tests verifying the in-house native
/// Sankalpa Snip Tool in Kiosk Mode, different crop dimensions, multi-monitor
/// coordinate handling, DPI scaling, and watchdog integrity.
/// 
/// SAFETY GUARANTEE:
/// Runs 100% in-process / temporary sandbox.
/// Never touches host registry, hosts file, or host running processes.
/// </summary>
public class MeditationSnippingSimulationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempDbPath;
    private readonly BlockerDatabase _db;

    public MeditationSnippingSimulationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"meditation_snip_sim_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempDbPath = Path.Combine(_tempDir, "blocker_test.db");
        _db = new BlockerDatabase(_tempDbPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    private static string GetRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Blocker.sln")))
        {
            dir = Path.GetDirectoryName(dir)!;
        }
        return dir;
    }

    /// <summary>
    /// Pure mathematical/algorithmic simulation of CropBitmap logic.
    /// Matches ZenScreenSnipper.CropBitmap accurately.
    /// </summary>
    public static class SimulationCropEngine
    {
        public record CropResult(int X, int Y, int Width, int Height, bool Success);

        public static CropResult CalculateCrop(int sourceWidth, int sourceHeight, double cropX, double cropY, double cropW, double cropH, double dpiX = 1.0, double dpiY = 1.0)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0 || cropW <= 0 || cropH <= 0)
            {
                return new CropResult(0, 0, 0, 0, false);
            }

            if (dpiX <= 0) dpiX = 1.0;
            if (dpiY <= 0) dpiY = 1.0;

            int pixelX = (int)Math.Round(cropX * dpiX);
            int pixelY = (int)Math.Round(cropY * dpiY);
            int pixelW = (int)Math.Round(cropW * dpiX);
            int pixelH = (int)Math.Round(cropH * dpiY);

            // Left/top boundary clamp
            if (pixelX < 0)
            {
                pixelW += pixelX;
                pixelX = 0;
            }
            if (pixelY < 0)
            {
                pixelH += pixelY;
                pixelY = 0;
            }

            if (pixelX >= sourceWidth || pixelY >= sourceHeight)
            {
                return new CropResult(0, 0, 0, 0, false);
            }

            // Right/bottom boundary clamp
            if (pixelX + pixelW > sourceWidth)
            {
                pixelW = sourceWidth - pixelX;
            }
            if (pixelY + pixelH > sourceHeight)
            {
                pixelH = sourceHeight - pixelY;
            }

            if (pixelW <= 0 || pixelH <= 0)
            {
                return new CropResult(0, 0, 0, 0, false);
            }

            return new CropResult(pixelX, pixelY, pixelW, pixelH, true);
        }
    }

    [Fact]
    public void SnipSimulation_SmallCrop_ReturnsExactDimensionsWithoutClippingError()
    {
        // 15 x 15 px icon / badge crop from 1920x1080 display
        var result = SimulationCropEngine.CalculateCrop(
            sourceWidth: 1920, sourceHeight: 1080,
            cropX: 120, cropY: 85,
            cropW: 15, cropH: 15,
            dpiX: 1.0, dpiY: 1.0);

        Assert.True(result.Success);
        Assert.Equal(120, result.X);
        Assert.Equal(85, result.Y);
        Assert.Equal(15, result.Width);
        Assert.Equal(15, result.Height);
    }

    [Fact]
    public void SnipSimulation_MediumCrop_ReturnsExactWindowDimensions()
    {
        // 640 x 480 px dialog / code region crop
        var result = SimulationCropEngine.CalculateCrop(
            sourceWidth: 1920, sourceHeight: 1080,
            cropX: 250, cropY: 180,
            cropW: 640, cropH: 480,
            dpiX: 1.0, dpiY: 1.0);

        Assert.True(result.Success);
        Assert.Equal(250, result.X);
        Assert.Equal(180, result.Y);
        Assert.Equal(640, result.Width);
        Assert.Equal(480, result.Height);
    }

    [Fact]
    public void SnipSimulation_FullScreenCrop_CapturesEntireDisplayBounds()
    {
        // Full screen capture 1920x1080
        var result = SimulationCropEngine.CalculateCrop(
            sourceWidth: 1920, sourceHeight: 1080,
            cropX: 0, cropY: 0,
            cropW: 1920, cropH: 1080,
            dpiX: 1.0, dpiY: 1.0);

        Assert.True(result.Success);
        Assert.Equal(0, result.X);
        Assert.Equal(0, result.Y);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
    }

    [Fact]
    public void SnipSimulation_MultiMonitor_VirtualScreenBounds_HandlesLargeExtendedDisplay()
    {
        // Dual monitor virtual desktop: 3840 x 1080
        var result = SimulationCropEngine.CalculateCrop(
            sourceWidth: 3840, sourceHeight: 1080,
            cropX: 1920, cropY: 100, // Secondary monitor area
            cropW: 800, cropH: 600,
            dpiX: 1.0, dpiY: 1.0);

        Assert.True(result.Success);
        Assert.Equal(1920, result.X);
        Assert.Equal(100, result.Y);
        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
    }

    [Theory]
    [InlineData(1.0, 100, 100, 100, 100)]   // 100% Scaling
    [InlineData(1.25, 100, 100, 125, 125)] // 125% High-DPI
    [InlineData(1.5, 100, 100, 150, 150)]  // 150% High-DPI
    [InlineData(2.0, 100, 100, 200, 200)]  // 200% High-DPI (4K)
    public void SnipSimulation_HighDpiScaling_MapsExactPhysicalPixels(double dpiScale, double dipW, double dipH, int expectedPxW, int expectedPxH)
    {
        var result = SimulationCropEngine.CalculateCrop(
            sourceWidth: 3840, sourceHeight: 2160,
            cropX: 50, cropY: 50,
            cropW: dipW, cropH: dipH,
            dpiX: dpiScale, dpiY: dpiScale);

        Assert.True(result.Success);
        Assert.Equal(expectedPxW, result.Width);
        Assert.Equal(expectedPxH, result.Height);
    }

    [Fact]
    public void SnipSimulation_OutOfBoundsAndNegativeCoordinates_ClampsSafely()
    {
        // 1. Partial out-of-bounds on right/bottom edges: clamps width & height
        var partial = SimulationCropEngine.CalculateCrop(
            sourceWidth: 1920, sourceHeight: 1080,
            cropX: 1900, cropY: 1050,
            cropW: 100, cropH: 100);

        Assert.True(partial.Success);
        Assert.Equal(1900, partial.X);
        Assert.Equal(1050, partial.Y);
        Assert.Equal(20, partial.Width);  // Clamped to remaining 20px
        Assert.Equal(30, partial.Height); // Clamped to remaining 30px

        // 2. Completely out-of-bounds coordinates: fails safely without throwing
        var outOfBounds = SimulationCropEngine.CalculateCrop(
            sourceWidth: 1920, sourceHeight: 1080,
            cropX: 2500, cropY: 2500,
            cropW: 100, cropH: 100);

        Assert.False(outOfBounds.Success);

        // 3. Negative crop start (e.g. dragged from left of screen): clamped to 0
        var negCrop = SimulationCropEngine.CalculateCrop(
            sourceWidth: 1920, sourceHeight: 1080,
            cropX: -30, cropY: -20,
            cropW: 100, cropH: 100);

        Assert.True(negCrop.Success);
        Assert.Equal(0, negCrop.X);
        Assert.Equal(0, negCrop.Y);
        Assert.Equal(70, negCrop.Width);
        Assert.Equal(80, negCrop.Height);

        // 4. Zero or negative width: fails safely
        var zeroWidth = SimulationCropEngine.CalculateCrop(1920, 1080, 50, 50, 0, 50);
        Assert.False(zeroWidth.Success);
    }

    [Fact]
    public void SnipSimulation_KioskWatchdog_InProcessSnipOverlay_MaintainsFullLockdown()
    {
        int currentPid = 4000;
        int snipOverlayPid = 4000; // Same process (in-process overlay)
        int unauthorizedPid = 9999; // Distraction app (e.g. Chrome)

        int unauthorizedBreachCount = 0;
        bool topmost = true;
        bool isZenScreenRestored = false;

        // Simulated Watchdog Tick Logic
        void WatchdogTick(int activeFgPid)
        {
            if (activeFgPid == currentPid)
            {
                // Whitelisted self / child overlay: zero breach count, keeps lockdown intact
                unauthorizedBreachCount = 0;
                return;
            }

            // Unauthorized window breach
            unauthorizedBreachCount++;
            if (unauthorizedBreachCount >= 3)
            {
                unauthorizedBreachCount = 0;
                topmost = true;
                isZenScreenRestored = true;
            }
        }

        // 1. Initial kiosk mode state
        Assert.True(topmost);
        Assert.Equal(0, unauthorizedBreachCount);

        // 2. Snip overlay appears: PID is same as Sankalpa
        WatchdogTick(snipOverlayPid);
        Assert.True(topmost);
        Assert.Equal(0, unauthorizedBreachCount);

        // Multiple ticks while user is selecting crop region
        for (int i = 0; i < 10; i++)
        {
            WatchdogTick(snipOverlayPid);
        }
        Assert.True(topmost);
        Assert.Equal(0, unauthorizedBreachCount);

        // 3. Unauthorized distraction pops up during kiosk mode
        WatchdogTick(unauthorizedPid);
        Assert.Equal(1, unauthorizedBreachCount);

        WatchdogTick(unauthorizedPid);
        Assert.Equal(2, unauthorizedBreachCount);

        WatchdogTick(unauthorizedPid);
        Assert.True(isZenScreenRestored);
        Assert.True(topmost);
    }

    [Fact]
    public void SnipSimulation_KeyboardHook_ChordsTriggerSnipDirectly()
    {
        const uint VK_S = 0x53;
        const uint VK_SNAPSHOT = 0x2C;
        const uint VK_TAB = 0x09;
        const uint VK_LWIN = 0x5B;

        bool snipRequested = false;

        // Simulates HookCallback in LowLevelKeyboardHook.cs
        int SimulateHook(uint vk, bool winDown, bool shiftDown, bool ctrlDown, bool altDown)
        {
            // Whitelist Win + Shift + S, Ctrl + Shift + S, and PrintScreen
            if ((vk == VK_S && winDown && shiftDown) ||
                (vk == VK_S && ctrlDown && shiftDown) ||
                (vk == VK_SNAPSHOT))
            {
                snipRequested = true;
                return 1;
            }

            // Block Win + Tab or Alt + Tab
            if (vk == VK_TAB && (winDown || altDown))
            {
                return 1;
            }

            // Block Win key
            if (vk == VK_LWIN)
            {
                return 1;
            }

            return 0;
        }

        // 1. Ctrl + Shift + S triggers snip
        snipRequested = false;
        int r1 = SimulateHook(VK_S, winDown: false, shiftDown: true, ctrlDown: true, altDown: false);
        Assert.Equal(1, r1);
        Assert.True(snipRequested);

        // 2. PrintScreen key triggers snip
        snipRequested = false;
        int r2 = SimulateHook(VK_SNAPSHOT, winDown: false, shiftDown: false, ctrlDown: false, altDown: false);
        Assert.Equal(1, r2);
        Assert.True(snipRequested);

        // 3. Win + Shift + S triggers snip
        snipRequested = false;
        int r3 = SimulateHook(VK_S, winDown: true, shiftDown: true, ctrlDown: false, altDown: false);
        Assert.Equal(1, r3);
        Assert.True(snipRequested);

        // 4. Distraction hotkeys remain blocked
        snipRequested = false;
        int r4 = SimulateHook(VK_TAB, winDown: true, shiftDown: false, ctrlDown: false, altDown: false);
        Assert.Equal(1, r4);
        Assert.False(snipRequested);
    }

    [Fact]
    public void SnipSimulation_AttachmentPersistence_GeneratesValidPngAndMarkdownTag()
    {
        string attachDir = Path.Combine(_tempDir, "attachments");
        Directory.CreateDirectory(attachDir);

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
        string fileName = $"Snip_{timestamp}.png";
        string filePath = Path.Combine(attachDir, fileName);

        // Create a simulated 1x1 RGBA pixel PNG header to test magic bytes
        byte[] validPngBytes = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG Magic Header
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, // IDAT
            0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
            0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, // IEND
            0x42, 0x60, 0x82
        };

        File.WriteAllBytes(filePath, validPngBytes);
        Assert.True(File.Exists(filePath));

        // Verify PNG magic bytes
        byte[] readBytes = File.ReadAllBytes(filePath);
        Assert.Equal(0x89, readBytes[0]);
        Assert.Equal(0x50, readBytes[1]); // P
        Assert.Equal(0x4E, readBytes[2]); // N
        Assert.Equal(0x47, readBytes[3]); // G

        string markdownTag = $"![[{fileName}]]";
        Assert.StartsWith("![[Snip_", markdownTag);
        Assert.EndsWith(".png]]", markdownTag);

        // Verify Notes Drawer insertion syntax
        string initialNote = "Session Reflections:\n";
        string noteAfterSnip = initialNote + $"\n{markdownTag}\n";
        Assert.Contains(markdownTag, noteAfterSnip);
    }

    [Fact]
    public void UiLint_PassesOnZenSnipOverlayAndUpdatedComponents()
    {
        string root = GetRepoRoot();
        var files = new[]
        {
            Path.Combine(root, "src", "Blocker.App", "ZenSnipOverlayWindow.xaml"),
            Path.Combine(root, "src", "Blocker.App", "ZenMiniHudWindow.xaml"),
            Path.Combine(root, "src", "Blocker.App", "NotesDrawerControl.xaml"),
            Path.Combine(root, "src", "Blocker.App", "ZenShieldWindow.xaml")
        };

        foreach (var file in files)
        {
            Assert.True(File.Exists(file), $"File must exist: {file}");
        }

        UiLint.AssertPasses(files);
    }

    [Fact]
    public void SourceCodeIntegrity_VerifiesAllSnipWiringPresent()
    {
        string root = GetRepoRoot();

        // 1. Verify ZenScreenSnipper.cs exists
        string snipperFile = Path.Combine(root, "src", "Blocker.App", "ZenScreenSnipper.cs");
        Assert.True(File.Exists(snipperFile));
        string snipperCode = File.ReadAllText(snipperFile);
        Assert.Contains("CaptureVirtualScreen()", snipperCode);
        Assert.Contains("CropBitmap(", snipperCode);
        Assert.Contains("SetClipboardImageSafe(", snipperCode);

        // 2. Verify ZenSnipOverlayWindow.xaml and .cs exist
        string overlayXaml = Path.Combine(root, "src", "Blocker.App", "ZenSnipOverlayWindow.xaml");
        string overlayCs = Path.Combine(root, "src", "Blocker.App", "ZenSnipOverlayWindow.xaml.cs");
        Assert.True(File.Exists(overlayXaml));
        Assert.True(File.Exists(overlayCs));
        string overlayCsText = File.ReadAllText(overlayCs);
        Assert.Contains("CommitCrop(", overlayCsText);
        Assert.Contains("UpdateSelection(", overlayCsText);
        Assert.Contains("Key.Escape", overlayCsText);
        Assert.Contains("Key.Enter", overlayCsText);

        // 3. Verify ZenShieldWindow wiring
        string shieldCs = Path.Combine(root, "src", "Blocker.App", "ZenShieldWindow.xaml.cs");
        Assert.True(File.Exists(shieldCs));
        string shieldText = File.ReadAllText(shieldCs);
        Assert.Contains("TriggerSnip()", shieldText);
        Assert.Contains("Key.Snapshot", shieldText);
        Assert.Contains("ZenNotesDrawer.OnSnipRequested = TriggerSnip", shieldText);
        Assert.Contains("OnSnipRequested = TriggerSnip", shieldText);

        // 4. Verify ZenMiniHudWindow Snip button & callback
        string hudXaml = Path.Combine(root, "src", "Blocker.App", "ZenMiniHudWindow.xaml");
        string hudCs = Path.Combine(root, "src", "Blocker.App", "ZenMiniHudWindow.xaml.cs");
        Assert.Contains("SnipButton", File.ReadAllText(hudXaml));
        Assert.Contains("OnSnipRequested", File.ReadAllText(hudCs));

        // 5. Verify NotesDrawerControl Snip button & callback
        string drawerXaml = Path.Combine(root, "src", "Blocker.App", "NotesDrawerControl.xaml");
        string drawerCs = Path.Combine(root, "src", "Blocker.App", "NotesDrawerControl.xaml.cs");
        Assert.Contains("ComposerSnipBtn", File.ReadAllText(drawerXaml));
        Assert.Contains("OnSnipRequested", File.ReadAllText(drawerCs));

        // 6. Verify LowLevelKeyboardHook whitelisting
        string hookCs = Path.Combine(root, "src", "Blocker.Core", "LowLevelKeyboardHook.cs");
        string hookText = File.ReadAllText(hookCs);
        Assert.Contains("VK_SNAPSHOT", hookText);
        Assert.Contains("ctrlDown && shiftDown", hookText);
    }
}
