using System;
using System.Windows;
using System.Windows.Media;
using Blocker.Core;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace Blocker.App;

public enum AppThemeMode
{
    Dark,
    Light,
    System
}

public enum AccentColorType
{
    Indigo,
    Emerald,
    Violet,
    Amber,
    Rose,
    Slate
}

public enum DesignThemeType
{
    Classic,
    Obsidian
}

public static class ThemeManager
{
    public static AppThemeMode CurrentThemeMode { get; private set; } = AppThemeMode.Dark;
    public static AccentColorType CurrentAccent { get; private set; } = AccentColorType.Emerald;
    public static DesignThemeType CurrentDesignTheme { get; private set; } = DesignThemeType.Obsidian;
    public static bool IsDark => CurrentThemeMode != AppThemeMode.Light;

    public static event Action? ThemeChanged;

    public static void Initialize(UiSettings? settings)
    {
        if (settings != null)
        {
            if (Enum.TryParse<AppThemeMode>(settings.ThemeMode, true, out var parsedMode))
            {
                CurrentThemeMode = parsedMode;
            }
            if (Enum.TryParse<AccentColorType>(settings.AccentColor, true, out var parsedAccent))
            {
                CurrentAccent = parsedAccent;
            }
            if (Enum.TryParse<DesignThemeType>(settings.DesignTheme, true, out var parsedDesign))
            {
                CurrentDesignTheme = parsedDesign;
            }
        }

        ApplyDesignTheme();
        ApplyCurrentTheme();
    }

    public static void SetDesignTheme(DesignThemeType designTheme, UiSettings? settingsToSave = null)
    {
        CurrentDesignTheme = designTheme;
        ApplyDesignTheme();
        ApplyCurrentTheme();
        if (settingsToSave != null)
        {
            settingsToSave.DesignTheme = designTheme.ToString();
            UiSettingsStore.Save(settingsToSave);
        }
        ThemeChanged?.Invoke();
    }

    public static void SetThemeMode(AppThemeMode mode, UiSettings? settingsToSave = null)
    {
        CurrentThemeMode = mode;
        ApplyCurrentTheme();
        if (settingsToSave != null)
        {
            settingsToSave.ThemeMode = mode.ToString();
            UiSettingsStore.Save(settingsToSave);
        }
        ThemeChanged?.Invoke();
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

    public static void ToggleThemeMode(UiSettings? settingsToSave = null)
    {
        var newMode = CurrentThemeMode == AppThemeMode.Dark ? AppThemeMode.Light : AppThemeMode.Dark;
        SetThemeMode(newMode, settingsToSave);
    }

    public static void ApplyDesignTheme()
    {
        var app = Application.Current;
        if (app == null) return;

        string themeName = CurrentDesignTheme == DesignThemeType.Obsidian ? "ObsidianTheme" : "DarkTheme";
        var newThemeUri = new Uri($"{themeName}.xaml", UriKind.Relative);

        // Find and replace the theme ResourceDictionary
        var mergedDicts = app.Resources.MergedDictionaries;
        ResourceDictionary? existingTheme = null;
        foreach (var dict in mergedDicts)
        {
            if (dict.Source != null &&
                (dict.Source.OriginalString.Contains("DarkTheme.xaml") ||
                 dict.Source.OriginalString.Contains("ObsidianTheme.xaml")))
            {
                existingTheme = dict;
                break;
            }
        }

        try
        {
            ResourceDictionary newTheme;
            try
            {
                newTheme = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Sankalpa;component/{themeName}.xaml", UriKind.Absolute) };
            }
            catch
            {
                newTheme = new ResourceDictionary { Source = newThemeUri };
            }

            if (existingTheme != null)
            {
                mergedDicts.Remove(existingTheme);
            }
            mergedDicts.Insert(0, newTheme);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ThemeManager] Failed to apply design theme {themeName}: {ex}");
        }
    }

    public static void ApplyCurrentTheme()
    {
        var app = Application.Current;
        if (app == null) return;

        bool isLight = CurrentThemeMode == AppThemeMode.Light;
        bool isObsidian = CurrentDesignTheme == DesignThemeType.Obsidian;

        // Accent Colors
        var (accentColor, accentHoverColor) = GetAccentColors(CurrentAccent, isLight);

        // Core Palette
        Color bgDark, bgCard, bgCardHover, bgInput, borderDark, borderSubtle;
        Color textPrimary, textMuted, textDim;
        Color successGreen, dangerRed, dangerRedHover, warningAmber;
        Color surfacePill, surfaceHighlight;

        Color dangerSubtleBg, dangerSubtleBorder, dangerSubtleText;

        if (isLight)
        {
            if (isObsidian)
            {
                // Obsidian Light: warm, papery, elegant
                bgDark = Color.FromRgb(0xF8, 0xF7, 0xF4);          // #F8F7F4 warm paper
                bgCard = Color.FromRgb(0xFF, 0xFF, 0xFE);          // #FFFFFE
                bgCardHover = Color.FromRgb(0xF5, 0xF4, 0xF1);     // #F5F4F1
                bgInput = Color.FromRgb(0xFF, 0xFF, 0xFF);         // #FFFFFF
                borderDark = Color.FromRgb(0xE2, 0xE0, 0xDB);      // #E2E0DB warm border
                borderSubtle = Color.FromRgb(0xEC, 0xEA, 0xE6);    // #ECEAE6
                textPrimary = Color.FromRgb(0x1A, 0x1A, 0x22);     // #1A1A22 deep ink
                textMuted = Color.FromRgb(0x55, 0x55, 0x66);       // #555566
                textDim = Color.FromRgb(0x7A, 0x7A, 0x8C);         // #7A7A8C
                successGreen = Color.FromRgb(0x0D, 0x9B, 0x68);    // #0D9B68
                dangerRed = Color.FromRgb(0xE1, 0x1D, 0x48);       // #E11D48 rose
                dangerRedHover = Color.FromRgb(0xBE, 0x12, 0x3C);  // #BE123C
                dangerSubtleBg = Color.FromRgb(0xFF, 0xE4, 0xE6);  // #FFE4E6
                dangerSubtleBorder = Color.FromRgb(0xFD, 0xA4, 0xAF);// #FDA4AF
                dangerSubtleText = Color.FromRgb(0xBE, 0x12, 0x3C);// #BE123C
                warningAmber = Color.FromRgb(0xD9, 0x77, 0x06);    // #D97706
                surfacePill = Color.FromRgb(0xF0, 0xEE, 0xEA);     // #F0EEEA
                surfaceHighlight = Color.FromRgb(0xE5, 0xE3, 0xDE);
            }
            else
            {
                // Classic Light (unchanged)
                bgDark = Color.FromRgb(0xF4, 0xF5, 0xF7);
                bgCard = Color.FromRgb(0xFF, 0xFF, 0xFF);
                bgCardHover = Color.FromRgb(0xFA, 0xFA, 0xFC);
                bgInput = Color.FromRgb(0xFF, 0xFF, 0xFF);
                borderDark = Color.FromRgb(0xE4, 0xE4, 0xE7);
                borderSubtle = Color.FromRgb(0xEE, 0xEE, 0xF2);
                textPrimary = Color.FromRgb(0x18, 0x18, 0x1B);
                textMuted = Color.FromRgb(0x52, 0x52, 0x5B);
                textDim = Color.FromRgb(0x71, 0x71, 0x7A);
                successGreen = Color.FromRgb(0x15, 0x80, 0x3D);
                dangerRed = Color.FromRgb(0xB9, 0x1C, 0x1C);
                dangerRedHover = Color.FromRgb(0x99, 0x1B, 0x1B);
                dangerSubtleBg = Color.FromRgb(0xFE, 0xE2, 0xE2);
                dangerSubtleBorder = Color.FromRgb(0xFC, 0xA5, 0xA5);
                dangerSubtleText = Color.FromRgb(0xB9, 0x1C, 0x1C);
                warningAmber = Color.FromRgb(0xB4, 0x53, 0x09);
                surfacePill = Color.FromRgb(0xEA, 0xEB, 0xF0);
                surfaceHighlight = Color.FromRgb(0xDF, 0xE1, 0xE8);
            }
        }
        else
        {
            if (isObsidian)
            {
                // Obsidian Dark: deep ink with blue-purple undertone
                bgDark = Color.FromRgb(0x0F, 0x0F, 0x14);          // #0F0F14 deep void
                bgCard = Color.FromRgb(0x17, 0x17, 0x1F);          // #17171F frosted card
                bgCardHover = Color.FromRgb(0x1E, 0x1E, 0x28);     // #1E1E28
                bgInput = Color.FromRgb(0x0C, 0x0C, 0x10);         // #0C0C10 sunken well
                borderDark = Color.FromRgb(0x25, 0x25, 0x30);      // #252530
                borderSubtle = Color.FromRgb(0x1C, 0x1C, 0x25);    // #1C1C25
                textPrimary = Color.FromRgb(0xED, 0xED, 0xF0);     // #EDEDF0
                textMuted = Color.FromRgb(0x8B, 0x8B, 0x9E);       // #8B8B9E blue-grey
                textDim = Color.FromRgb(0x5C, 0x5C, 0x72);         // #5C5C72
                successGreen = Color.FromRgb(0x34, 0xD3, 0x99);    // #34D399
                dangerRed = Color.FromRgb(0xF4, 0x3F, 0x5E);       // #F43F5E rose-red
                dangerRedHover = Color.FromRgb(0xE1, 0x1D, 0x48);  // #E11D48
                dangerSubtleBg = Color.FromRgb(0x1F, 0x15, 0x20);  // #1F1520
                dangerSubtleBorder = Color.FromRgb(0x3D, 0x1F, 0x30);// #3D1F30
                dangerSubtleText = Color.FromRgb(0xFD, 0xA4, 0xAF);// #FDA4AF
                warningAmber = Color.FromRgb(0xFB, 0xBF, 0x24);    // #FBBF24 gold
                surfacePill = Color.FromRgb(0x14, 0x14, 0x19);     // #141419
                surfaceHighlight = Color.FromRgb(0x22, 0x22, 0x2E); // #22222E
            }
            else
            {
                // Classic Dark (unchanged)
                bgDark = Color.FromRgb(0x18, 0x18, 0x1B);
                bgCard = Color.FromRgb(0x23, 0x23, 0x27);
                bgCardHover = Color.FromRgb(0x2B, 0x2B, 0x30);
                bgInput = Color.FromRgb(0x13, 0x13, 0x16);
                borderDark = Color.FromRgb(0x2E, 0x2E, 0x34);
                borderSubtle = Color.FromRgb(0x25, 0x25, 0x2A);
                textPrimary = Color.FromRgb(0xF4, 0xF4, 0xF5);
                textMuted = Color.FromRgb(0xA1, 0xA1, 0xAA);
                textDim = Color.FromRgb(0x71, 0x71, 0x7A);
                successGreen = Color.FromRgb(0x22, 0x9B, 0x62);
                dangerRed = Color.FromRgb(0xDC, 0x26, 0x26);
                dangerRedHover = Color.FromRgb(0xB9, 0x1C, 0x1C);
                dangerSubtleBg = Color.FromRgb(0x2C, 0x16, 0x18);
                dangerSubtleBorder = Color.FromRgb(0x4A, 0x1E, 0x22);
                dangerSubtleText = Color.FromRgb(0xFC, 0xA5, 0xA5);
                warningAmber = Color.FromRgb(0xD9, 0x77, 0x06);
                surfacePill = Color.FromRgb(0x1E, 0x1E, 0x22);
                surfaceHighlight = Color.FromRgb(0x2B, 0x2B, 0x32);
            }
        }

        SetResourceColorAndBrush(app, "BgDark", bgDark);
        SetResourceColorAndBrush(app, "BgCard", bgCard);
        SetResourceColorAndBrush(app, "BgCardHover", bgCardHover);
        SetResourceColorAndBrush(app, "BgInput", bgInput);
        SetResourceColorAndBrush(app, "BorderDark", borderDark);
        SetResourceColorAndBrush(app, "BorderSubtle", borderSubtle);

        // Keep legacy TextLight key name for compatibility, but update with high-contrast primary text
        SetResourceColorAndBrush(app, "TextLight", textPrimary);
        SetResourceColorAndBrush(app, "TextPrimary", textPrimary);
        SetResourceColorAndBrush(app, "TextMuted", textMuted);
        SetResourceColorAndBrush(app, "TextDim", textDim);

        SetResourceColorAndBrush(app, "AccentBlue", accentColor);
        SetResourceColorAndBrush(app, "AccentBlueHover", accentHoverColor);
        SetResourceColorAndBrush(app, "AccentPrimary", accentColor);
        SetResourceColorAndBrush(app, "AccentPrimaryHover", accentHoverColor);

        SetResourceColorAndBrush(app, "SuccessGreen", successGreen);
        SetResourceColorAndBrush(app, "DangerRed", dangerRed);
        SetResourceColorAndBrush(app, "DangerRedHover", dangerRedHover);
        SetResourceColorAndBrush(app, "DangerSubtleBg", dangerSubtleBg);
        SetResourceColorAndBrush(app, "DangerSubtleBorder", dangerSubtleBorder);
        SetResourceColorAndBrush(app, "DangerSubtleText", dangerSubtleText);
        SetResourceColorAndBrush(app, "WarningAmber", warningAmber);

        SetResourceColorAndBrush(app, "SurfacePill", surfacePill);
        SetResourceColorAndBrush(app, "SurfaceHighlight", surfaceHighlight);

        Color dangerButtonBg = isLight
            ? (isObsidian ? Color.FromRgb(0xBE, 0x12, 0x3C) : Color.FromRgb(0xB9, 0x1C, 0x1C))
            : (isObsidian ? Color.FromRgb(0x88, 0x13, 0x37) : Color.FromRgb(0x7F, 0x1D, 0x1D));
        Color dangerButtonHover = isLight
            ? (isObsidian ? Color.FromRgb(0x9F, 0x12, 0x39) : Color.FromRgb(0x99, 0x1B, 0x1B))
            : (isObsidian ? Color.FromRgb(0xBE, 0x12, 0x3C) : Color.FromRgb(0xB9, 0x1C, 0x1C));

        SetResourceColorAndBrush(app, "DangerButtonBg", dangerButtonBg);
        SetResourceColorAndBrush(app, "DangerButtonHover", dangerButtonHover);

        // Buttons: Secondary/Pill buttons have rich dark background and pure white text in both themes & modes
        Color btnSecondaryBg = isLight
            ? Color.FromRgb(0x2A, 0x2A, 0x36)
            : (isObsidian ? Color.FromRgb(0x22, 0x22, 0x2E) : Color.FromRgb(0x2D, 0x2D, 0x36));
        Color btnSecondaryHoverBg = isLight
            ? Color.FromRgb(0x38, 0x38, 0x48)
            : (isObsidian ? Color.FromRgb(0x2D, 0x2D, 0x3D) : Color.FromRgb(0x38, 0x38, 0x44));

        SetResourceColorAndBrush(app, "ButtonSecondaryBg", btnSecondaryBg);
        SetResourceColorAndBrush(app, "ButtonSecondaryHoverBg", btnSecondaryHoverBg);
    }

    private static (Color baseColor, Color hoverColor) GetAccentColors(AccentColorType accent, bool isLight)
    {
        return accent switch
        {
            AccentColorType.Emerald => isLight
                ? (Color.FromRgb(0x0D, 0x9B, 0x68), Color.FromRgb(0x05, 0x7A, 0x55))  // #0D9B68 deep emerald
                : (Color.FromRgb(0x05, 0x96, 0x69), Color.FromRgb(0x04, 0x78, 0x57)), // #059669 rich emerald with crisp white text
            AccentColorType.Indigo => isLight
                ? (Color.FromRgb(0x43, 0x38, 0xCA), Color.FromRgb(0x37, 0x30, 0xA3))
                : (Color.FromRgb(0x4F, 0x46, 0xE5), Color.FromRgb(0x43, 0x38, 0xCA)), // #4F46E5 rich indigo
            AccentColorType.Violet => isLight
                ? (Color.FromRgb(0x6D, 0x28, 0xD9), Color.FromRgb(0x5B, 0x21, 0xB6))
                : (Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x6D, 0x28, 0xD9)), // #7C3AED rich violet
            AccentColorType.Amber => isLight
                ? (Color.FromRgb(0xB4, 0x53, 0x09), Color.FromRgb(0x92, 0x40, 0x0E))
                : (Color.FromRgb(0xD9, 0x77, 0x06), Color.FromRgb(0xB4, 0x53, 0x09)), // #D97706 warm gold
            AccentColorType.Rose => isLight
                ? (Color.FromRgb(0xBE, 0x12, 0x3C), Color.FromRgb(0x9F, 0x12, 0x39))
                : (Color.FromRgb(0xE1, 0x1D, 0x48), Color.FromRgb(0xBE, 0x12, 0x3C)), // #E11D48 rich rose
            AccentColorType.Slate => isLight
                ? (Color.FromRgb(0x33, 0x41, 0x55), Color.FromRgb(0x1E, 0x29, 0x3B))
                : (Color.FromRgb(0x47, 0x55, 0x69), Color.FromRgb(0x33, 0x41, 0x55)), // #475569 slate
            _ => isLight
                ? (Color.FromRgb(0x0D, 0x9B, 0x68), Color.FromRgb(0x05, 0x7A, 0x55))
                : (Color.FromRgb(0x05, 0x96, 0x69), Color.FromRgb(0x04, 0x78, 0x57)),
        };
    }

    private static void SetResourceColorAndBrush(Application app, string key, Color color)
    {
        app.Resources[key] = color;
        var brushKey = key + "Brush";
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
    }
}
