using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;

namespace Blocker.App;

/// <summary>
/// High-performance, lightweight in-house screen capture and cropping engine.
/// Runs completely in-process within Sankalpa without external OS UWP tool dependencies.
/// </summary>
public static class ZenScreenSnipper
{
    /// <summary>
    /// Captures the full virtual desktop (all monitors combined) into an immutable BitmapSource.
    /// Uses high-performance direct buffer locking without intermediate GDI bitmap handle allocations.
    /// </summary>
    public static BitmapSource? CaptureVirtualScreen()
    {
        try
        {
            int left = SystemInformation.VirtualScreen.Left;
            int top = SystemInformation.VirtualScreen.Top;
            int width = SystemInformation.VirtualScreen.Width;
            int height = SystemInformation.VirtualScreen.Height;

            if (width <= 0 || height <= 0)
            {
                return null;
            }

            using var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height), System.Drawing.CopyPixelOperation.SourceCopy);
            }

            var bmpData = bmp.LockBits(
                new System.Drawing.Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                var source = BitmapSource.Create(
                    width,
                    height,
                    96,
                    96,
                    System.Windows.Media.PixelFormats.Bgr32,
                    null,
                    bmpData.Scan0,
                    bmpData.Stride * height,
                    bmpData.Stride);
                source.Freeze();
                return source;
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZenScreenSnipper] Error capturing screen: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pure functional crop of a BitmapSource based on DIP coordinates and DPI scale factors.
    /// Automatically clamps boundaries and verifies non-zero area.
    /// </summary>
    public static BitmapSource? CropBitmap(BitmapSource? source, Rect cropRectDips, double dpiScaleX = 1.0, double dpiScaleY = 1.0)
    {
        if (source == null) return null;
        if (cropRectDips.Width <= 0 || cropRectDips.Height <= 0) return null;

        if (dpiScaleX <= 0) dpiScaleX = 1.0;
        if (dpiScaleY <= 0) dpiScaleY = 1.0;

        int pixelX = (int)Math.Round(cropRectDips.X * dpiScaleX);
        int pixelY = (int)Math.Round(cropRectDips.Y * dpiScaleY);
        int pixelW = (int)Math.Round(cropRectDips.Width * dpiScaleX);
        int pixelH = (int)Math.Round(cropRectDips.Height * dpiScaleY);

        // Clamp left and top
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

        if (pixelX >= source.PixelWidth || pixelY >= source.PixelHeight)
        {
            return null;
        }

        // Clamp width and height
        if (pixelX + pixelW > source.PixelWidth)
        {
            pixelW = source.PixelWidth - pixelX;
        }
        if (pixelY + pixelH > source.PixelHeight)
        {
            pixelH = source.PixelHeight - pixelY;
        }

        if (pixelW <= 0 || pixelH <= 0)
        {
            return null;
        }

        var rect = new Int32Rect(pixelX, pixelY, pixelW, pixelH);
        var cropped = new CroppedBitmap(source, rect);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>
    /// Safely sets the clipboard image with retry logic to avoid transient COMException locks.
    /// </summary>
    public static bool SetClipboardImageSafe(BitmapSource image)
    {
        if (image == null) return false;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetImage(image);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ZenScreenSnipper] Clipboard error: {ex.Message}");
                break;
            }
        }
        return false;
    }

    /// <summary>
    /// Initiates a screen snip session using the in-process overlay.
    /// </summary>
    public static void StartSnip(Window? owner, Action<string, string>? onCaptured)
    {
        try
        {
            var captured = CaptureVirtualScreen();
            if (captured == null) return;

            var overlay = new ZenSnipOverlayWindow(captured);
            if (owner != null && owner.IsLoaded && owner.IsVisible)
            {
                overlay.Owner = owner;
            }

            overlay.OnSnipCaptured = (filePath, markdownTag) =>
            {
                onCaptured?.Invoke(filePath, markdownTag);
            };

            overlay.Show();
            overlay.Activate();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZenScreenSnipper] Error starting snip: {ex.Message}");
        }
    }
}
