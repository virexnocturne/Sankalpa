using System;
using System.Windows;
using System.Windows.Input;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Blocker.App;

/// <summary>
/// A sleek, compact floating HUD Pill that stays topmost at the top-center of the screen
/// when the user switches to an allowed whitelisted application.
/// Supports minimization to an ultra-compact timer pill, screen boundary clamping, and magnetic edge snapping.
/// </summary>
public partial class ZenMiniHudWindow : Window
{
    public const double SnapDistance = 25.0;

    public Action? OnReturnToZenRequested { get; set; }
    public Action? OnToggleAudioRequested { get; set; }
    public Action? OnToggleNotesRequested { get; set; }
    public Action? OnSnipRequested { get; set; }

    private bool _isCollapsed;
    private bool _hasBeenPositionedInitially;
    private bool _isAdjustingPosition;
    private string _rawFormattedTime = "18:45";

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (_isCollapsed != value)
            {
                _isCollapsed = value;
                ApplyCollapseState();
            }
        }
    }

    public ZenMiniHudWindow()
    {
        InitializeComponent();

        if (ReturnButton != null)
        {
            ReturnButton.Content = "Return to Zen Focus";
            ReturnButton.ToolTip = "Return to Fullscreen Zen Focus Kiosk immediately";
        }

        Loaded += (s, e) =>
        {
            if (ReturnButton != null)
            {
                ReturnButton.Content = "Return to Zen Focus";
                ReturnButton.ToolTip = "Return to Fullscreen Zen Focus Kiosk immediately";
            }
            Topmost = true;
            ApplyCollapseState();
            if (!_hasBeenPositionedInitially)
            {
                PositionAtTopCenter();
                _hasBeenPositionedInitially = true;
            }
            else
            {
                ClampToScreen();
            }
        };

        ContentRendered += (s, e) =>
        {
            if (!_hasBeenPositionedInitially)
            {
                PositionAtTopCenter();
                _hasBeenPositionedInitially = true;
            }
            else
            {
                ClampToScreen();
            }
        };

        SizeChanged += (s, e) =>
        {
            if (!_hasBeenPositionedInitially)
            {
                PositionAtTopCenter();
                _hasBeenPositionedInitially = true;
                return;
            }

            Rect workArea = GetCurrentWorkArea();
            if (e.PreviousSize.Height > 0)
            {
                double oldMaxTop = workArea.Bottom - e.PreviousSize.Height;
                // If it was snapped flush against the bottom edge, maintain flush alignment
                if (Math.Abs(Top - oldMaxTop) < 2.0)
                {
                    Top = workArea.Bottom - e.NewSize.Height;
                }
            }

            ClampToScreen();
        };
    }

    /// <summary>
    /// Positions the HUD window automatically at Top Center of the primary visible work area.
    /// </summary>
    private Rect GetCurrentWorkArea()
    {
        try
        {
            double w = ActualWidth > 0 ? ActualWidth : (Width > 0 && !double.IsNaN(Width) ? Width : 200);
            double h = ActualHeight > 0 ? ActualHeight : (Height > 0 && !double.IsNaN(Height) ? Height : 60);
            var centerPoint = new System.Drawing.Point((int)(Left + w / 2), (int)(Top + h / 2));
            var scr = System.Windows.Forms.Screen.FromPoint(centerPoint);
            var bounds = scr.WorkingArea;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            return new Rect(bounds.X / dpi.DpiScaleX, bounds.Y / dpi.DpiScaleY, bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY);
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }

    public void PositionAtTopCenter()
    {
        Topmost = true;
        Rect workArea = GetCurrentWorkArea();
        double currentWidth = ActualWidth > 0 ? ActualWidth : (Width > 0 && !double.IsNaN(Width) ? Width : 580);
        Left = workArea.Left + Math.Max(0, (workArea.Width - currentWidth) / 2.0);
        Top = workArea.Top + 20;
        ClampToScreen();
    }

    /// <summary>
    /// Clamps the HUD window strictly within the screen work area boundaries without snapping.
    /// </summary>
    public void ClampToScreen()
    {
        if (_isAdjustingPosition)
        {
            return;
        }

        try
        {
            _isAdjustingPosition = true;

            Rect workArea = GetCurrentWorkArea();
            double width = ActualWidth > 0 ? ActualWidth : (Width > 0 && !double.IsNaN(Width) ? Width : 200);
            double height = ActualHeight > 0 ? ActualHeight : (Height > 0 && !double.IsNaN(Height) ? Height : 60);

            double minLeft = workArea.Left;
            double maxLeft = Math.Max(minLeft, workArea.Right - width);
            double minTop = workArea.Top;
            double maxTop = Math.Max(minTop, workArea.Bottom - height);

            if (Left < minLeft) Left = minLeft;
            if (Left > maxLeft) Left = maxLeft;
            if (Top < minTop) Top = minTop;
            if (Top > maxTop) Top = maxTop;
        }
        finally
        {
            _isAdjustingPosition = false;
        }
    }

    /// <summary>
    /// Applies magnetic snapping to top and bottom screen edges within SnapDistance (25px),
    /// and ensures the window is strictly clamped within the work area boundaries.
    /// </summary>
    public void ApplyClampingAndSnapping()
    {
        if (_isAdjustingPosition)
        {
            return;
        }

        try
        {
            _isAdjustingPosition = true;

            Rect workArea = GetCurrentWorkArea();
            double width = ActualWidth > 0 ? ActualWidth : (Width > 0 && !double.IsNaN(Width) ? Width : 200);
            double height = ActualHeight > 0 ? ActualHeight : (Height > 0 && !double.IsNaN(Height) ? Height : 60);

            double minLeft = workArea.Left;
            double maxLeft = Math.Max(minLeft, workArea.Right - width);
            double minTop = workArea.Top;
            double maxTop = Math.Max(minTop, workArea.Bottom - height);

            double currentTop = Top;
            double currentLeft = Left;

            // 1. Magnetic Snapping to Top and Bottom
            // Snap to top edge if within SnapDistance (25px) or above top boundary
            if (currentTop <= minTop + SnapDistance)
            {
                currentTop = minTop;
            }
            // Snap to bottom edge if within SnapDistance (25px) or below bottom boundary
            else if (currentTop >= maxTop - SnapDistance)
            {
                currentTop = maxTop;
            }
            else
            {
                // Boundary clamping for Top
                if (currentTop < minTop) currentTop = minTop;
                if (currentTop > maxTop) currentTop = maxTop;
            }

            // 2. Boundary clamping for Left
            if (currentLeft < minLeft) currentLeft = minLeft;
            if (currentLeft > maxLeft) currentLeft = maxLeft;

            Left = currentLeft;
            Top = currentTop;
        }
        finally
        {
            _isAdjustingPosition = false;
        }
    }

    /// <summary>
    /// Toggles between collapsed pill mode and expanded full Mini-HUD mode.
    /// </summary>
    public void ToggleCollapse()
    {
        IsCollapsed = !IsCollapsed;
    }

    private void ApplyCollapseState()
    {
        if (!IsInitialized || CountdownTextBlock == null || ExpandedControlsPanel == null || HudBorder == null || LotusButton == null)
        {
            return;
        }

        if (_isCollapsed)
        {
            // Collapsed ultra-compact pill mode: [ 🪷 18:45 ]
            ExpandedControlsPanel.Visibility = Visibility.Collapsed;
            CountdownTextBlock.Margin = new Thickness(0, 0, 4, 0);
            HudBorder.Padding = new Thickness(12, 6, 12, 6);
            HudBorder.CornerRadius = new CornerRadius(16);
            HudBorder.ToolTip = "Click to expand Mini-HUD";
            HudBorder.Cursor = Cursors.Hand;
            LotusButton.ToolTip = "Click to expand Mini-HUD";
            CountdownTextBlock.ToolTip = "Click to expand Mini-HUD";
        }
        else
        {
            // Expanded full Mini-HUD mode
            ExpandedControlsPanel.Visibility = Visibility.Visible;
            CountdownTextBlock.Margin = new Thickness(0, 0, 12, 0);
            HudBorder.Padding = new Thickness(16, 8, 16, 8);
            HudBorder.CornerRadius = new CornerRadius(20);
            HudBorder.ToolTip = null;
            HudBorder.Cursor = Cursors.SizeAll;
            LotusButton.ToolTip = "Click to minimize Mini-HUD";
            CountdownTextBlock.ToolTip = null;
        }

        RefreshTimerDisplay();
    }

    /// <summary>
    /// Updates the countdown display text.
    /// </summary>
    public void UpdateTimer(string formattedTime)
    {
        if (string.IsNullOrWhiteSpace(formattedTime))
        {
            return;
        }

        _rawFormattedTime = formattedTime;
        RefreshTimerDisplay();
    }

    private void RefreshTimerDisplay()
    {
        if (CountdownTextBlock == null || string.IsNullOrWhiteSpace(_rawFormattedTime))
        {
            return;
        }

        string timeOnly = _rawFormattedTime
            .Replace("remaining", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (_isCollapsed)
        {
            CountdownTextBlock.Text = timeOnly;
        }
        else
        {
            CountdownTextBlock.Text = _rawFormattedTime.EndsWith("remaining", StringComparison.OrdinalIgnoreCase)
                ? _rawFormattedTime
                : $"{timeOnly} remaining";
        }
    }

    /// <summary>
    /// Updates the ambient sound toggle button label (e.g. "🌧 Rain [⏸]").
    /// </summary>
    public void UpdateAudioLabel(string label)
    {
        if (AudioToggleButton != null)
        {
            AudioToggleButton.Content = label;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            double startLeft = Left;
            double startTop = Top;

            try
            {
                DragMove();
            }
            catch
            {
                // Ignore if mouse was released rapidly
            }
            finally
            {
                ApplyClampingAndSnapping();
            }

            // In collapsed mode, clicking the pill (without dragging) expands it back to the full Mini-HUD
            if (_isCollapsed)
            {
                double deltaX = Math.Abs(Left - startLeft);
                double deltaY = Math.Abs(Top - startTop);
                if (deltaX < 4 && deltaY < 4)
                {
                    ToggleCollapse();
                }
            }
        }
    }

    private void LotusButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleCollapse();
    }

    private void ReturnButton_Click(object sender, RoutedEventArgs e)
    {
        OnReturnToZenRequested?.Invoke();
    }

    private void AudioToggle_Click(object sender, RoutedEventArgs e)
    {
        OnToggleAudioRequested?.Invoke();
    }

    private void NotesButton_Click(object sender, RoutedEventArgs e)
    {
        OnToggleNotesRequested?.Invoke();
    }

    private void SnipButton_Click(object sender, RoutedEventArgs e)
    {
        OnSnipRequested?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            OnToggleNotesRequested?.Invoke();
            e.Handled = true;
        }
        else if (((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                  (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift &&
                  e.Key == Key.S) || e.Key == Key.Snapshot)
        {
            OnSnipRequested?.Invoke();
            e.Handled = true;
        }
    }
}
