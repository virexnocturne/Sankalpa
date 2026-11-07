using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Blocker.App;

public partial class ZenSnipOverlayWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly BitmapSource _capturedSource;
    private System.Windows.Point _startPoint;
    private bool _isSelecting;
    private Rect _currentRect;
    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    public Action<string, string>? OnSnipCaptured { get; set; }

    public ZenSnipOverlayWindow(BitmapSource capturedSource)
    {
        InitializeComponent();
        _capturedSource = capturedSource;
        BackgroundImage.Source = _capturedSource;

        Loaded += ZenSnipOverlayWindow_Loaded;
        KeyDown += ZenSnipOverlayWindow_KeyDown;
    }

    private void ZenSnipOverlayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 1. Determine accurate monitor DPI scaling
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        _dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        // 2. Position accurately over Virtual Screen (all monitors combined) in WPF DIPs
        int vLeft = SystemInformation.VirtualScreen.Left;
        int vTop = SystemInformation.VirtualScreen.Top;
        int vWidth = SystemInformation.VirtualScreen.Width;
        int vHeight = SystemInformation.VirtualScreen.Height;

        Left = vLeft / _dpiX;
        Top = vTop / _dpiY;
        Width = vWidth / _dpiX;
        Height = vHeight / _dpiY;

        // 3. Size BackgroundImage strictly to match overlay DIP dimensions
        // When rendered at screen DPI, this maps 1:1 with physical desktop pixels (Zero Zoom)
        BackgroundImage.Width = Width;
        BackgroundImage.Height = Height;
        BackgroundImage.Stretch = Stretch.Fill;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        }

        UpdateSelection(Rect.Empty);
    }

    private void ZenSnipOverlayWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            e.Handled = true;
            if (_currentRect.Width > 5 && _currentRect.Height > 5)
            {
                CommitCrop(_currentRect);
            }
            else
            {
                double actW = ActualWidth > 0 ? ActualWidth : Width;
                double actH = ActualHeight > 0 ? ActualHeight : Height;
                CommitCrop(new Rect(0, 0, actW, actH));
            }
        }
    }

    private void RootGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(RootGrid);
        _isSelecting = true;
        RootGrid.CaptureMouse();
        UpdateSelection(new Rect(_startPoint, new System.Windows.Size(0, 0)));
        e.Handled = true;
    }

    private void RootGrid_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isSelecting) return;

        // If mouse/touchpad button was released during drag, commit immediately
        if (e.LeftButton == MouseButtonState.Released)
        {
            FinishSelection();
            return;
        }

        var currentPoint = e.GetPosition(RootGrid);
        double x = Math.Min(_startPoint.X, currentPoint.X);
        double y = Math.Min(_startPoint.Y, currentPoint.Y);
        double w = Math.Abs(currentPoint.X - _startPoint.X);
        double h = Math.Abs(currentPoint.Y - _startPoint.Y);

        UpdateSelection(new Rect(x, y, w, h));
    }

    private void RootGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting) return;
        FinishSelection();
        e.Handled = true;
    }

    private void RootGrid_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isSelecting)
        {
            FinishSelection();
        }
    }

    private void FinishSelection()
    {
        if (!_isSelecting) return;
        _isSelecting = false;
        RootGrid.ReleaseMouseCapture();

        if (_currentRect.Width > 5 && _currentRect.Height > 5)
        {
            CommitCrop(_currentRect);
        }
        else
        {
            UpdateSelection(Rect.Empty);
        }
    }

    private void UpdateSelection(Rect rect)
    {
        _currentRect = rect;

        double actW = ActualWidth > 0 ? ActualWidth : Width;
        double actH = ActualHeight > 0 ? ActualHeight : Height;

        if (rect.Width > 0 && rect.Height > 0)
        {
            // Top mask
            System.Windows.Controls.Canvas.SetLeft(MaskTop, 0);
            System.Windows.Controls.Canvas.SetTop(MaskTop, 0);
            MaskTop.Width = actW;
            MaskTop.Height = Math.Max(0, rect.Top);

            // Bottom mask
            System.Windows.Controls.Canvas.SetLeft(MaskBottom, 0);
            System.Windows.Controls.Canvas.SetTop(MaskBottom, rect.Bottom);
            MaskBottom.Width = actW;
            MaskBottom.Height = Math.Max(0, actH - rect.Bottom);

            // Left mask
            System.Windows.Controls.Canvas.SetLeft(MaskLeft, 0);
            System.Windows.Controls.Canvas.SetTop(MaskLeft, rect.Top);
            MaskLeft.Width = Math.Max(0, rect.Left);
            MaskLeft.Height = rect.Height;

            // Right mask
            System.Windows.Controls.Canvas.SetLeft(MaskRight, rect.Right);
            System.Windows.Controls.Canvas.SetTop(MaskRight, rect.Top);
            MaskRight.Width = Math.Max(0, actW - rect.Right);
            MaskRight.Height = rect.Height;

            // Selection border
            SelectionBorder.Visibility = Visibility.Visible;
            System.Windows.Controls.Canvas.SetLeft(SelectionBorder, rect.Left);
            System.Windows.Controls.Canvas.SetTop(SelectionBorder, rect.Top);
            SelectionBorder.Width = rect.Width;
            SelectionBorder.Height = rect.Height;

            // Info badge
            InfoBadge.Visibility = Visibility.Visible;
            int pxW = (int)Math.Round(rect.Width * _dpiX);
            int pxH = (int)Math.Round(rect.Height * _dpiY);
            DimensionText.Text = $"{pxW} × {pxH}";

            double badgeTop = rect.Bottom + 10;
            if (badgeTop + 36 > actH)
            {
                badgeTop = Math.Max(10, rect.Top - 38);
            }
            double badgeLeft = Math.Max(10, Math.Min(rect.Left, actW - 320));
            System.Windows.Controls.Canvas.SetLeft(InfoBadge, badgeLeft);
            System.Windows.Controls.Canvas.SetTop(InfoBadge, badgeTop);
        }
        else
        {
            System.Windows.Controls.Canvas.SetLeft(MaskTop, 0);
            System.Windows.Controls.Canvas.SetTop(MaskTop, 0);
            MaskTop.Width = actW;
            MaskTop.Height = actH;

            MaskBottom.Width = 0;
            MaskBottom.Height = 0;
            MaskLeft.Width = 0;
            MaskLeft.Height = 0;
            MaskRight.Width = 0;
            MaskRight.Height = 0;

            SelectionBorder.Visibility = Visibility.Collapsed;
            InfoBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void CommitCrop(Rect cropRectDips)
    {
        try
        {
            var cropped = ZenScreenSnipper.CropBitmap(_capturedSource, cropRectDips, _dpiX, _dpiY);
            if (cropped != null)
            {
                // 1. Copy image to Windows clipboard safely
                ZenScreenSnipper.SetClipboardImageSafe(cropped);

                // 2. Save image attachment
                var saved = NotesAttachmentManager.SaveBitmap(cropped, "Snip");
                if (saved != null)
                {
                    OnSnipCaptured?.Invoke(saved.Value.filePath, saved.Value.markdownTag);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZenSnipOverlay] CommitCrop error: {ex.Message}");
        }
        finally
        {
            Close();
        }
    }
}
