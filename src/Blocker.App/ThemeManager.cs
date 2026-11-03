using System;
using System.Windows;
using System.Windows.Media;
using Blocker.Core;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace Blocker.App;

public enum AccentColorType
{
    Emerald,
    Indigo,
    Violet,
    Amber,
    Rose,
    Slate
}

// Backward compatibility enums for legacy test harnesses
public enum AppThemeMode
{
    Dark,
    Light,
    System
}

public enum DesignThemeType
{
    Classic,
    Obsidian
}

public static class ThemeManager
{
    public static string CurrentTheme { get; private set; } = "black";
    public static AccentColorType CurrentAccent { get; private set; } = AccentColorType.Emerald;
    public static bool IsDark => !string.Equals(CurrentTheme, "white", StringComparison.OrdinalIgnoreCase);

    // Compatibility properties
    public static AppThemeMode CurrentThemeMode => IsDark ? AppThemeMode.Dark : AppThemeMode.Light;
    public static DesignThemeType CurrentDesignTheme => DesignThemeType.Obsidian;

    public static event Action? ThemeChanged;

    public static void Initialize(UiSettings? settings)
    {
        if (settings != null)
        {
            CurrentTheme = UiSettingsStore.NormalizeTheme(settings.Theme);
            if (Enum.TryParse<AccentColorType>(settings.AccentColor, true, out var parsedAccent))
            {
                CurrentAccent = parsedAccent;
            }
        }

        ApplyCurrentTheme();
    }

    public static void SetTheme(string theme, UiSettings? settingsToSave = null)
    {
        CurrentTheme = UiSettingsStore.NormalizeTheme(theme);
        ApplyCurrentTheme();
        if (settingsToSave != null)
        {
            settingsToSave.Theme = CurrentTheme;
            UiSettingsStore.Save(settingsToSave);
        }
        ThemeChanged?.Invoke();
    }

    // Compatibility methods
    public static void SetThemeMode(AppThemeMode mode, UiSettings? settingsToSave = null)
    {
        SetTheme(mode == AppThemeMode.Light ? "white" : "black", settingsToSave);
    }

    public static void SetDesignTheme(DesignThemeType designTheme, UiSettings? settingsToSave = null)
    {
        SetTheme(designTheme == DesignThemeType.Classic ? "black" : CurrentTheme, settingsToSave);
    }

    public static void SetAccentColor(AccentColorType accent, UiSettings? settingsToSave = null)
    {
        CurrentAccent = accent;
        ApplyCurrentTheme();
        if (settingsToSave != null)
        {
            settingsToSave.AccentColor = accent.ToString();
            UiSettingsStore.Save(settingsToSave);
        }
        ThemeChanged?.Invoke();
    }

    public static void ApplyCurrentTheme()
    {
        var app = Application.Current;
        if (app == null) return;

        string themeFileName = CurrentTheme switch
        {
            "white" => "Theme.White.xaml",
            "grey" => "Theme.Grey.xaml",
            _ => "Theme.Black.xaml"
        };

        var mergedDicts = app.Resources.MergedDictionaries;
        ResourceDictionary? existingThemeDict = null;

        foreach (var dict in mergedDicts)
        {
            if (dict.Source != null)
            {
                string s = dict.Source.OriginalString;
                if (s.Contains("Theme.Black.xaml") || s.Contains("Theme.White.xaml") || s.Contains("Theme.Grey.xaml")
                    || s.Contains("DarkTheme.xaml") || s.Contains("ObsidianTheme.xaml"))
                {
                    existingThemeDict = dict;
                    break;
                }
            }
        }

        try
        {
            ResourceDictionary newThemeDict;
            try
            {
                newThemeDict = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/Sankalpa;component/{themeFileName}", UriKind.Absolute)
                };
            }
            catch
            {
                newThemeDict = new ResourceDictionary
                {
                    Source = new Uri(themeFileName, UriKind.Relative)
                };
            }

            if (existingThemeDict != null)
            {
                int index = mergedDicts.IndexOf(existingThemeDict);
                mergedDicts.Remove(existingThemeDict);
                mergedDicts.Insert(index, newThemeDict);
            }
            else
            {
                mergedDicts.Insert(0, newThemeDict);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ThemeManager] Failed to load theme {themeFileName}: {ex}");
        }

        // Apply accent color & TextOnAccent dynamically
        var (accentColor, textOnAccentColor) = GetAccentColors(CurrentAccent, CurrentTheme);
        SetResourceColorAndBrush(app, "Accent", accentColor);
        SetResourceColorAndBrush(app, "TextOnAccent", textOnAccentColor);

        // Legacy compatibility keys
        SetResourceColorAndBrush(app, "AccentPrimary", accentColor);
        SetResourceColorAndBrush(app, "AccentBlue", accentColor);
        SetResourceColorAndBrush(app, "AccentBlueHover", accentColor);
        SetResourceColorAndBrush(app, "AccentPrimaryHover", accentColor);

        // Update all open windows title bars
        try
        {
            foreach (Window window in app.Windows)
            {
                WindowHelper.UpdateTitleBarTheme(window, IsDark);
            }
        }
        catch { }
    }

    public static (Color accent, Color textOnAccent) GetAccentColors(AccentColorType accent, string theme)
    {
        string normTheme = UiSettingsStore.NormalizeTheme(theme);
        if (normTheme == "white")
        {
            Color acc = accent switch
            {
                AccentColorType.Emerald => Color.FromRgb(0x04, 0x78, 0x57), // #047857
                AccentColorType.Indigo => Color.FromRgb(0x43, 0x38, 0xCA),  // #4338CA
                AccentColorType.Violet => Color.FromRgb(0x6D, 0x28, 0xD9),  // #6D28D9
                AccentColorType.Amber => Color.FromRgb(0xB4, 0x53, 0x09),   // #B45309
                AccentColorType.Rose => Color.FromRgb(0xBE, 0x12, 0x3C),    // #BE123C
                AccentColorType.Slate => Color.FromRgb(0x33, 0x41, 0x55),   // #334155
                _ => Color.FromRgb(0x04, 0x78, 0x57)
            };
            return (acc, Color.FromRgb(0xFF, 0xFF, 0xFF));
        }
        else if (normTheme == "grey")
        {
            Color acc = accent switch
            {
                AccentColorType.Emerald => Color.FromRgb(0x10, 0xB9, 0x81), // #10B981
                AccentColorType.Indigo => Color.FromRgb(0x81, 0x8C, 0xF8),  // #818CF8
                AccentColorType.Violet => Color.FromRgb(0xA7, 0x8B, 0xFA),  // #A78BFA
                AccentColorType.Amber => Color.FromRgb(0xFB, 0xBF, 0x24),   // #FBBF24
                AccentColorType.Rose => Color.FromRgb(0xFB, 0x71, 0x85),    // #FB7185
                AccentColorType.Slate => Color.FromRgb(0xCB, 0xD5, 0xE1),   // #CBD5E1
                _ => Color.FromRgb(0x10, 0xB9, 0x81)
            };
            return (acc, Color.FromRgb(0x00, 0x00, 0x00));
        }
        else // black
        {
            Color acc = accent switch
            {
                AccentColorType.Emerald => Color.FromRgb(0x10, 0xB9, 0x81), // #10B981
                AccentColorType.Indigo => Color.FromRgb(0x63, 0x66, 0xF1),  // #6366F1
                AccentColorType.Violet => Color.FromRgb(0x8B, 0x5C, 0xF6),  // #8B5CF6
                AccentColorType.Amber => Color.FromRgb(0xF5, 0x9E, 0x0B),   // #F59E0B
                AccentColorType.Rose => Color.FromRgb(0xF4, 0x3F, 0x5E),    // #F43F5E
                AccentColorType.Slate => Color.FromRgb(0x94, 0xA3, 0xB8),   // #94A3B8
                _ => Color.FromRgb(0x10, 0xB9, 0x81)
            };
            return (acc, Color.FromRgb(0x00, 0x00, 0x00));
        }
    }

    private static void SetResourceColorAndBrush(Application app, string key, Color color)
    {
        string colorKey = key.StartsWith("Color") ? key : "Color" + key;
        app.Resources[colorKey] = color;

        string brushKey = key.EndsWith("Brush") ? key : key;
        if (app.Resources[brushKey] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
        }
        else
        {
            var newBrush = new SolidColorBrush(color);
            newBrush.Freeze();
            app.Resources[brushKey] = newBrush;
        }

        // Also set legacy key + "Brush" if needed
        if (!key.EndsWith("Brush"))
        {
            string aliasKey = key + "Brush";
            var aliasBrush = new SolidColorBrush(color);
            aliasBrush.Freeze();
            app.Resources[aliasKey] = aliasBrush;
        }
    }
}
