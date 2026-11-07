using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
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
        // 1. Determine DPI Scaling
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            _dpiX = source.CompositionTarget.TransformToDevice.M11;
            _dpiY = source.CompositionTarget.TransformToDevice.M22;
            if (_dpiX <= 0) _dpiX = 1.0;
            if (_dpiY <= 0) _dpiY = 1.0;
        }

        // 2. Position accurately over the Virtual Screen across all displays
        int vLeft = SystemInformation.VirtualScreen.Left;
        int vTop = SystemInformation.VirtualScreen.Top;
        int vWidth = SystemInformation.VirtualScreen.Width;
        int vHeight = SystemInformation.VirtualScreen.Height;

        Left = vLeft / _dpiX;
        Top = vTop / _dpiY;
        Width = vWidth / _dpiX;
        Height = vHeight / _dpiY;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            SetWindowPos(handle, HWND_TOPMOST, vLeft, vTop, vWidth, vHeight, SWP_SHOWWINDOW);
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
            // Fullscreen capture
            CommitCrop(new Rect(0, 0, ActualWidth, ActualHeight));
        }
    }

    private void RootGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(RootGrid);
        _isSelecting = true;
        RootGrid.CaptureMouse();
        UpdateSelection(new Rect(_startPoint, new System.Windows.Size(0, 0)));
    }

    private void RootGrid_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isSelecting) return;

        var currentPoint = e.GetPosition(RootGrid);
        double x = Math.Min(_startPoint.X, currentPoint.X);
        double y = Math.Min(_startPoint.Y, currentPoint.Y);
        double w = Math.Abs(currentPoint.X - _startPoint.X);
        double h = Math.Abs(currentPoint.Y - _startPoint.Y);

        UpdateSelection(new Rect(x, y, w, h));
    }

    private void RootGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
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
        var fullRect = new RectangleGeometry(new Rect(0, 0, actW, actH));

        if (rect.Width > 0 && rect.Height > 0)
        {
            var cutout = new RectangleGeometry(rect);
            MaskPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, fullRect, cutout);

            SelectionBorder.Visibility = Visibility.Visible;
            System.Windows.Controls.Canvas.SetLeft(SelectionBorder, rect.Left);
            System.Windows.Controls.Canvas.SetTop(SelectionBorder, rect.Top);
            SelectionBorder.Width = rect.Width;
            SelectionBorder.Height = rect.Height;

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
            MaskPath.Data = fullRect;
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
