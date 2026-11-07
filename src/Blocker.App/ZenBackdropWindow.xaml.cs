using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Forms;

namespace Blocker.App;

/// <summary>
/// An opaque, distraction-free backdrop covering secondary monitors during a Zen focus session.
/// </summary>
public partial class ZenBackdropWindow : Window
{
    public ZenBackdropWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Spawns a fullscreen, topmost backdrop window for each secondary display.
    /// Iterates through System.Windows.Forms.Screen.AllScreens and skips Screen.PrimaryScreen.
    /// </summary>
    public static List<ZenBackdropWindow> SpawnForSecondaryScreens()
    {
        var backdrops = new List<ZenBackdropWindow>();

        try
        {
            foreach (var screen in Screen.AllScreens)
            {
                if (screen.Primary)
                {
                    continue;
                }

                var bounds = screen.Bounds;
                var backdrop = new ZenBackdropWindow
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = bounds.X,
                    Top = bounds.Y,
                    Width = bounds.Width,
                    Height = bounds.Height
                };

                // Ensure bounds stay mapped after window source initialization
                backdrop.Loaded += (s, e) =>
                {
                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(backdrop);
                    backdrop.Left = bounds.X / dpi.DpiScaleX;
                    backdrop.Top = bounds.Y / dpi.DpiScaleY;
                    backdrop.Width = bounds.Width / dpi.DpiScaleX;
                    backdrop.Height = bounds.Height / dpi.DpiScaleY;
                };

                backdrop.Show();
                backdrops.Add(backdrop);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ZenBackdrop] Failed to spawn secondary screen backdrops: {ex.Message}");
        }

        return backdrops;
    }
}
