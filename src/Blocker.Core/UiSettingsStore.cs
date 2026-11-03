using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Blocker.Core;

public class SessionSettings
{
    public int DurationMinutes { get; set; } = 25;
    public bool Locked { get; set; } = true;
}

public class UiSettings
{
    public Dictionary<string, SessionSettings> ProfileSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool SuppressAlwaysActiveWarning { get; set; }
    public string ThemeMode { get; set; } = "Dark";
    public string AccentColor { get; set; } = "Emerald";
    public string CardDensity { get; set; } = "Comfortable";
    public string TimeFormat { get; set; } = "24h";
    public string DesignTheme { get; set; } = "Obsidian";
    public bool HideExtensionTab { get; set; } = false;
    public bool MinimiseSettingsToGear { get; set; } = false;
    public bool HideDesignThemePill { get; set; } = false;
    public bool HideQuickThemeToggle { get; set; } = false;
    public bool AreNotificationsEnabled { get; set; } = true;
    public bool ShowBrowserAlerts { get; set; } = true;
    public bool ShowAppAlerts { get; set; } = true;
    public bool ShowDnsAlerts { get; set; } = true;
    public string ExtensionTheme { get; set; } = "Current";
}

public static class UiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string GetDefaultFilePath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Blocker", "ui-settings.json");
    }

    public static string GetSharedDataFilePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "data", "ui-settings.json");
    }

    public static string NormalizeTheme(string? theme)
    {
        if (string.Equals(theme, "DeepBlack", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(theme, "deep-black", StringComparison.OrdinalIgnoreCase))
        {
            return "DeepBlack";
        }
        if (string.Equals(theme, "MinimalWhite", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(theme, "minimal-white", StringComparison.OrdinalIgnoreCase))
        {
            return "MinimalWhite";
        }
        return "Current";
    }

    public static SessionSettings GetSettings(UiSettings? settings, string? profileId)
    {
        if (settings != null && !string.IsNullOrWhiteSpace(profileId) &&
            settings.ProfileSettings.TryGetValue(profileId, out var found) && found != null)
        {
            return new SessionSettings
            {
                DurationMinutes = found.DurationMinutes > 0 ? found.DurationMinutes : 25,
                Locked = found.Locked
            };
        }

        return new SessionSettings { DurationMinutes = 25, Locked = true };
    }

    public static void SetSettings(UiSettings settings, string profileId, int durationMinutes, bool locked)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return;

        settings.ProfileSettings[profileId] = new SessionSettings
        {
            DurationMinutes = durationMinutes > 0 ? durationMinutes : 25,
            Locked = locked
        };
    }

    public static string ToJson(UiSettings settings)
    {
        return JsonSerializer.Serialize(settings, JsonOptions);
    }

    public static UiSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new UiSettings();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<UiSettings>(json, JsonOptions);
            if (parsed == null) return new UiSettings();

            if (parsed.ProfileSettings == null)
            {
                parsed.ProfileSettings = new Dictionary<string, SessionSettings>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                parsed.ProfileSettings = new Dictionary<string, SessionSettings>(parsed.ProfileSettings, StringComparer.OrdinalIgnoreCase);
            }

            if (string.IsNullOrWhiteSpace(parsed.ThemeMode)) parsed.ThemeMode = "Dark";
            if (string.IsNullOrWhiteSpace(parsed.AccentColor)) parsed.AccentColor = "Emerald";
            if (string.IsNullOrWhiteSpace(parsed.CardDensity)) parsed.CardDensity = "Comfortable";
            if (string.IsNullOrWhiteSpace(parsed.TimeFormat)) parsed.TimeFormat = "24h";
            if (string.IsNullOrWhiteSpace(parsed.DesignTheme)) parsed.DesignTheme = "Obsidian";
            if (string.IsNullOrWhiteSpace(parsed.ExtensionTheme)) parsed.ExtensionTheme = "Current";

            return parsed;
        }
        catch
        {
            return new UiSettings();
        }
    }

    public static UiSettings Load()
    {
        string sharedPath = GetSharedDataFilePath();
        if (File.Exists(sharedPath))
        {
            return LoadFromFile(sharedPath);
        }

        string userPath = GetDefaultFilePath();
        if (File.Exists(userPath))
        {
            return LoadFromFile(userPath);
        }

        // If running in Session 0 (SYSTEM), search active user AppData
        try
        {
            string usersDir = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? @"C:\Users";
            if (Directory.Exists(usersDir))
            {
                foreach (var uDir in Directory.GetDirectories(usersDir))
                {
                    string candidate = Path.Combine(uDir, "AppData", "Local", "Blocker", "ui-settings.json");
                    if (File.Exists(candidate))
                    {
                        return LoadFromFile(candidate);
                    }
                }
            }
        }
        catch { }

        return new UiSettings();
    }

    public static void Save(UiSettings settings)
    {
        SaveToFile(settings, GetDefaultFilePath());
        try
        {
            string sharedPath = GetSharedDataFilePath();
            string? dir = Path.GetDirectoryName(sharedPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                SaveToFile(settings, sharedPath);
            }
        }
        catch { }
    }

    public static UiSettings LoadFromFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new UiSettings();
            }

            string json = File.ReadAllText(filePath);
            return FromJson(json);
        }
        catch
        {
            return new UiSettings();
        }
    }

    public static void SaveToFile(UiSettings settings, string filePath)
    {
        try
        {
            string dir = Path.GetDirectoryName(filePath) ?? string.Empty;
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = ToJson(settings);
            string tempFile = filePath + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, filePath, overwrite: true);
        }
        catch
        {
            // Best effort save
        }
    }
}
