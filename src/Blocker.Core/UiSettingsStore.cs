using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    public bool SuppressMeditationStartWarning { get; set; } = false;
    public string Theme { get; set; } = "black";
    public string AccentColor { get; set; } = "Emerald";
    public string CardDensity { get; set; } = "Comfortable";
    public string TimeFormat { get; set; } = "24h";
    public bool AreNotificationsEnabled { get; set; } = true;
    public bool ShowBrowserAlerts { get; set; } = true;
    public bool ShowAppAlerts { get; set; } = true;
    public bool ShowDnsAlerts { get; set; } = true;
    public int MeditationDurationMinutes { get; set; } = 20;
    public string MeditationLockMode { get; set; } = "Locked";
    private string _meditationSoundscape = "Rain";
    public string MeditationSoundscape
    {
        get => _meditationSoundscape;
        set
        {
            if (string.Equals(value, "Custom", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(LastSelectedCustomSoundscapeId))
            {
                _meditationSoundscape = LastSelectedCustomSoundscapeId;
            }
            else
            {
                _meditationSoundscape = value ?? "Rain";
                if (!string.IsNullOrEmpty(value) && value.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
                {
                    LastSelectedCustomSoundscapeId = value;
                }
            }
        }
    }
    public static string? LastSelectedCustomSoundscapeId { get; set; }
    public List<CustomSoundtrackMetadata> CustomSoundtracks { get; set; } = new();
    public string? MeditationPasswordHash { get; set; }
    public bool IsKeyboardShortcutsExpanded { get; set; } = false;

    // Backward compatibility helper properties for legacy extension sync callers if needed
    [JsonIgnore]
    public string ExtensionTheme => Theme;

    public void Save()
    {
        UiSettingsStore.Save(this);
    }
}

public static class UiSettingsStore
{
    private static UiSettings? _instance;

    public static UiSettings Instance
    {
        get => _instance ??= Load();
        set => _instance = value;
    }

    public static bool SuppressMeditationStartWarning
    {
        get => Instance.SuppressMeditationStartWarning;
        set => Instance.SuppressMeditationStartWarning = value;
    }

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
        if (string.IsNullOrWhiteSpace(theme))
        {
            return "black";
        }

        string trimmed = theme.Trim().ToLowerInvariant();
        if (trimmed == "white" || trimmed == "light" || trimmed == "minimalwhite" || trimmed == "minimal-white")
        {
            return "white";
        }
        if (trimmed == "grey" || trimmed == "gray")
        {
            return "grey";
        }
        return "black";
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
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var parsed = JsonSerializer.Deserialize<UiSettings>(json, JsonOptions) ?? new UiSettings();

            if (parsed.ProfileSettings == null)
            {
                parsed.ProfileSettings = new Dictionary<string, SessionSettings>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                parsed.ProfileSettings = new Dictionary<string, SessionSettings>(parsed.ProfileSettings, StringComparer.OrdinalIgnoreCase);
            }

            // Check if Theme is explicitly set in JSON
            bool hasExplicitTheme = false;
            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, "Theme", StringComparison.OrdinalIgnoreCase))
                {
                    parsed.Theme = NormalizeTheme(prop.Value.GetString());
                    hasExplicitTheme = true;
                    break;
                }
            }

            // If Theme was not explicitly set, migrate from legacy keys:
            // Old "dark" or "Classic" becomes "black"; old "light" becomes "white"; missing becomes "black".
            if (!hasExplicitTheme)
            {
                string? legacyThemeMode = null;
                string? legacyDesignTheme = null;
                string? legacyExtensionTheme = null;

                foreach (var prop in root.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "ThemeMode", StringComparison.OrdinalIgnoreCase))
                    {
                        legacyThemeMode = prop.Value.GetString();
                    }
                    else if (string.Equals(prop.Name, "DesignTheme", StringComparison.OrdinalIgnoreCase))
                    {
                        legacyDesignTheme = prop.Value.GetString();
                    }
                    else if (string.Equals(prop.Name, "ExtensionTheme", StringComparison.OrdinalIgnoreCase))
                    {
                        legacyExtensionTheme = prop.Value.GetString();
                    }
                }

                if (string.Equals(legacyThemeMode, "Light", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(legacyExtensionTheme, "MinimalWhite", StringComparison.OrdinalIgnoreCase))
                {
                    parsed.Theme = "white";
                }
                else if (string.Equals(legacyThemeMode, "Dark", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(legacyDesignTheme, "Classic", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(legacyDesignTheme, "Obsidian", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(legacyExtensionTheme, "DeepBlack", StringComparison.OrdinalIgnoreCase))
                {
                    parsed.Theme = "black";
                }
                else
                {
                    parsed.Theme = "black";
                }
            }

            if (string.IsNullOrWhiteSpace(parsed.AccentColor)) parsed.AccentColor = "Emerald";
            if (string.IsNullOrWhiteSpace(parsed.CardDensity)) parsed.CardDensity = "Comfortable";
            if (string.IsNullOrWhiteSpace(parsed.TimeFormat)) parsed.TimeFormat = "24h";

            if (parsed.CustomSoundtracks == null)
            {
                parsed.CustomSoundtracks = new List<CustomSoundtrackMetadata>();
            }
            else
            {
                parsed.CustomSoundtracks.RemoveAll(item => item == null || string.IsNullOrWhiteSpace(item.Id));
            }

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

    public static void Save()
    {
        Save(Instance);
    }

    public static void Save(UiSettings settings)
    {
        _instance = settings;
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
            string tempFile = filePath + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, filePath, overwrite: true);
        }
        catch
        {
            // Best effort save
        }
    }
}

