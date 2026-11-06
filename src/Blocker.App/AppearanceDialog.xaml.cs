using System;
using System.Windows;
using System.Windows.Controls;
using Blocker.Core;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Blocker.App;

public partial class AppearanceDialog : Window
{
    private readonly UiSettings _settings;
    private bool _isInitializing = true;

    public bool ShowMeditationStartWarning
    {
        get => !_settings.SuppressMeditationStartWarning;
        set
        {
            if (_settings.SuppressMeditationStartWarning != !value)
            {
                _settings.SuppressMeditationStartWarning = !value;
                UiSettingsStore.Save(_settings);
            }
        }
    }

    public AppearanceDialog(Window? owner = null)
    {
        InitializeComponent();
        if (owner != null)
        {
            Owner = owner;
        }

        _settings = UiSettingsStore.Load();
        DataContext = this;

        // Load current Theme Mode
        if (ThemeManager.CurrentThemeMode == AppThemeMode.Light)
        {
            ThemeLightRadio.IsChecked = true;
        }
        else
        {
            ThemeDarkRadio.IsChecked = true;
        }

        // Load current Accent Color
        switch (ThemeManager.CurrentAccent)
        {
            case AccentColorType.Emerald:
                AccentEmeraldRadio.IsChecked = true;
                break;
            case AccentColorType.Violet:
                AccentVioletRadio.IsChecked = true;
                break;
            case AccentColorType.Amber:
                AccentAmberRadio.IsChecked = true;
                break;
            case AccentColorType.Rose:
                AccentRoseRadio.IsChecked = true;
                break;
            case AccentColorType.Slate:
                AccentSlateRadio.IsChecked = true;
                break;
            default:
                AccentIndigoRadio.IsChecked = true;
                break;
        }

        // Load Card Density
        if (string.Equals(_settings.CardDensity, "Compact", StringComparison.OrdinalIgnoreCase))
        {
            DensityCompactRadio.IsChecked = true;
        }
        else
        {
            DensityComfortableRadio.IsChecked = true;
        }

        // Load Meditation & Lockdown Warning preference
        MeditationWarningToggle.IsChecked = !_settings.SuppressMeditationStartWarning;

        _isInitializing = false;
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        var mode = ThemeLightRadio.IsChecked == true ? AppThemeMode.Light : AppThemeMode.Dark;
        ThemeManager.SetThemeMode(mode, _settings);
    }

    private void AccentRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        if (sender is RadioButton rb && rb.Tag is string tagStr &&
            Enum.TryParse<AccentColorType>(tagStr, out var accent))
        {
            ThemeManager.SetAccentColor(accent, _settings);
        }
    }

    private void DensityRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.CardDensity = DensityCompactRadio.IsChecked == true ? "Compact" : "Comfortable";
        UiSettingsStore.Save(_settings);
    }

    private void MeditationWarningToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.SuppressMeditationStartWarning = MeditationWarningToggle.IsChecked != true;
        UiSettingsStore.Save(_settings);
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
