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
                    backdrop.Left = bounds.X;
                    backdrop.Top = bounds.Y;
                    backdrop.Width = bounds.Width;
                    backdrop.Height = bounds.Height;
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
