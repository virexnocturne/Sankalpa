using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UserControl = System.Windows.Controls.UserControl;

namespace Blocker.App;

public partial class NotesDrawerControl : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty IsDrawerOpenProperty =
        DependencyProperty.Register(
            nameof(IsDrawerOpen),
            typeof(bool),
            typeof(NotesDrawerControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsDrawerOpenChanged));

    public bool IsDrawerOpen
    {
        get => (bool)GetValue(IsDrawerOpenProperty);
        set => SetValue(IsDrawerOpenProperty, value);
    }

    private static void OnIsDrawerOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NotesDrawerControl control && e.NewValue is bool isOpen)
        {
            control.ApplyDrawerState(isOpen);
        }
    }

    public NotesDrawerControl()
    {
        InitializeComponent();
    }

    private void DrawerTabButton_Click(object sender, RoutedEventArgs e)
    {
        IsDrawerOpen = !IsDrawerOpen;
    }

    private void CloseDrawerButton_Click(object sender, RoutedEventArgs e)
    {
        IsDrawerOpen = false;
    }

    private void ApplyDrawerState(bool open)
    {
        if (TabChevronPath != null)
        {
            TabChevronPath.Data = open
                ? (Geometry)FindResource("IconChevronRight")
                : (Geometry)FindResource("IconChevronLeft");
        }

        if (DrawerContainer == null) return;

        if (open)
        {
            DrawerContainer.Visibility = Visibility.Visible;
        }

        var anim = new DoubleAnimation
        {
            From = DrawerContainer.ActualWidth,
            To = open ? 340.0 : 0.0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        if (!open)
        {
            anim.Completed += (s, e) =>
            {
                if (!IsDrawerOpen)
                {
                    DrawerContainer.Visibility = Visibility.Collapsed;
                    DrawerContainer.Width = 0;
                }
            };
        }

        DrawerContainer.BeginAnimation(WidthProperty, anim);
    }
}
