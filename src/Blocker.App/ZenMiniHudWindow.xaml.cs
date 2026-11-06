using System;
using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Blocker.App;

/// <summary>
/// A sleek, compact floating HUD Pill that stays topmost at the top-center of the screen
/// when the user switches to an allowed whitelisted application.
/// </summary>
public partial class ZenMiniHudWindow : Window
{
    public Action? OnReturnToZenRequested { get; set; }
    public Action? OnToggleAudioRequested { get; set; }
    public Action? OnToggleNotesRequested { get; set; }

    public ZenMiniHudWindow()
    {
        InitializeComponent();

        Loaded += (s, e) => PositionAtTopCenter();
        ContentRendered += (s, e) => PositionAtTopCenter();
        SizeChanged += (s, e) => PositionAtTopCenter();
    }

    /// <summary>
    /// Positions the HUD window automatically at Left = (PrimaryScreenWidth - Width) / 2, Top = 20.
    /// </summary>
    public void PositionAtTopCenter()
    {
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        double currentWidth = ActualWidth > 0 ? ActualWidth : (Width > 0 && !double.IsNaN(Width) ? Width : 580);
        Left = Math.Max(0, (screenWidth - currentWidth) / 2.0);
        Top = 20;
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

        CountdownTextBlock.Text = formattedTime.EndsWith("remaining", StringComparison.OrdinalIgnoreCase)
            ? formattedTime
            : $"{formattedTime} remaining";
    }

    /// <summary>
    /// Updates the ambient sound toggle button label (e.g. "🌧 Rain [⏸]").
    /// </summary>
    public void UpdateAudioLabel(string label)
    {
        AudioToggleButton.Content = label;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // Ignore if mouse was released rapidly
            }
        }
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            OnToggleNotesRequested?.Invoke();
            e.Handled = true;
        }
    }
}
