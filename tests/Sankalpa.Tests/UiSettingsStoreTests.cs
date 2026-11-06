using System;
using System.IO;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class UiSettingsStoreTests
{
    [Fact]
    public void GetSettings_ReturnsDefault_WhenProfileNotFound()
    {
        var settings = new UiSettings();
        var s = UiSettingsStore.GetSettings(settings, "nonexistent");
        Assert.Equal(25, s.DurationMinutes);
        Assert.True(s.Locked);
    }

    [Fact]
    public void GetSettings_ReturnsStoredValues_WhenProfileExists()
    {
        var settings = new UiSettings();
        UiSettingsStore.SetSettings(settings, "prof1", 45, false);

        var s = UiSettingsStore.GetSettings(settings, "prof1");
        Assert.Equal(45, s.DurationMinutes);
        Assert.False(s.Locked);

        // Case-insensitivity check
        var s2 = UiSettingsStore.GetSettings(settings, "PROF1");
        Assert.Equal(45, s2.DurationMinutes);
        Assert.False(s2.Locked);
    }

    [Fact]
    public void FromJson_ReturnsDefaults_OnInvalidOrEmptyJson()
    {
        var empty = UiSettingsStore.FromJson("");
        Assert.Empty(empty.ProfileSettings);

        var corrupt = UiSettingsStore.FromJson("{ not valid json !!!");
        Assert.Empty(corrupt.ProfileSettings);
    }

    [Fact]
    public void RoundTrip_JsonSerialization_PreservesData()
    {
        var settings = new UiSettings();
        UiSettingsStore.SetSettings(settings, "work", 50, true);
        UiSettingsStore.SetSettings(settings, "study", 15, false);

        string json = UiSettingsStore.ToJson(settings);
        var restored = UiSettingsStore.FromJson(json);

        var work = UiSettingsStore.GetSettings(restored, "work");
        Assert.Equal(50, work.DurationMinutes);
        Assert.True(work.Locked);

        var study = UiSettingsStore.GetSettings(restored, "study");
        Assert.Equal(15, study.DurationMinutes);
        Assert.False(study.Locked);
    }

    [Fact]
    public void FileSaveAndLoad_WorksEndToEnd()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "BlockerUiSettingsTest_" + Guid.NewGuid().ToString("N"));
        string filePath = Path.Combine(tempDir, "subfolder", "ui-settings.json");

        try
        {
            var settings = new UiSettings();
            UiSettingsStore.SetSettings(settings, "p1", 60, false);

            UiSettingsStore.SaveToFile(settings, filePath);
            Assert.True(File.Exists(filePath));

            var loaded = UiSettingsStore.LoadFromFile(filePath);
            var p1 = UiSettingsStore.GetSettings(loaded, "p1");
            Assert.Equal(60, p1.DurationMinutes);
            Assert.False(p1.Locked);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void RoundTrip_SuppressAlwaysActiveWarning_Preserved()
    {
        var settings = new UiSettings { SuppressAlwaysActiveWarning = true };
        string json = UiSettingsStore.ToJson(settings);
        var restored = UiSettingsStore.FromJson(json);
        Assert.True(restored.SuppressAlwaysActiveWarning);
    }

    [Fact]
    public void TimeFormat_DefaultsTo24h()
    {
        var settings = new UiSettings();
        Assert.Equal("24h", settings.TimeFormat);

        var fromEmpty = UiSettingsStore.FromJson("{}");
        Assert.Equal("24h", fromEmpty.TimeFormat);
    }

    [Theory]
    [InlineData("12h")]
    [InlineData("24h")]
    public void RoundTrip_TimeFormat_Preserved(string timeFormat)
    {
        var settings = new UiSettings { TimeFormat = timeFormat };
        string json = UiSettingsStore.ToJson(settings);
        var restored = UiSettingsStore.FromJson(json);
        Assert.Equal(timeFormat, restored.TimeFormat);
    }

    [Fact]
    public void Theme_DefaultsToBlack()
    {
        var settings = new UiSettings();
        Assert.Equal("black", settings.Theme);

        var fromEmpty = UiSettingsStore.FromJson("{}");
        Assert.Equal("black", fromEmpty.Theme);
    }

    [Theory]
    [InlineData("black")]
    [InlineData("white")]
    [InlineData("grey")]
    public void RoundTrip_Theme_Preserved(string theme)
    {
        var settings = new UiSettings { Theme = theme };
        string json = UiSettingsStore.ToJson(settings);
        var restored = UiSettingsStore.FromJson(json);
        Assert.Equal(theme, restored.Theme);
    }

    [Fact]
    public void Migration_OldDarkOrClassic_BecomesBlack()
    {
        var fromDark = UiSettingsStore.FromJson("{\"ThemeMode\": \"Dark\"}");
        Assert.Equal("black", fromDark.Theme);

        var fromClassic = UiSettingsStore.FromJson("{\"DesignTheme\": \"Classic\"}");
        Assert.Equal("black", fromClassic.Theme);

        var fromObsidian = UiSettingsStore.FromJson("{\"DesignTheme\": \"Obsidian\"}");
        Assert.Equal("black", fromObsidian.Theme);
    }

    [Fact]
    public void Migration_OldLight_BecomesWhite()
    {
        var fromLight = UiSettingsStore.FromJson("{\"ThemeMode\": \"Light\"}");
        Assert.Equal("white", fromLight.Theme);

        var fromExtWhite = UiSettingsStore.FromJson("{\"ExtensionTheme\": \"MinimalWhite\"}");
        Assert.Equal("white", fromExtWhite.Theme);
    }

    [Fact]
    public void Migration_Missing_BecomesBlack()
    {
        var fromMissing = UiSettingsStore.FromJson("{\"CardDensity\": \"Compact\"}");
        Assert.Equal("black", fromMissing.Theme);
    }

    [Fact]
    public void Migration_UnknownKeysIgnored()
    {
        var jsonWithOldKeys = "{\"HideExtensionTab\": true, \"MinimiseSettingsToGear\": true, \"UnknownKey\": 12345}";
        var parsed = UiSettingsStore.FromJson(jsonWithOldKeys);
        Assert.NotNull(parsed);
        Assert.Equal("black", parsed.Theme);
    }

    [Fact]
    public void NotificationSettings_RoundTrip_Preserved()
    {
        var settings = new UiSettings
        {
            AreNotificationsEnabled = false,
            ShowBrowserAlerts = false,
            ShowAppAlerts = true,
            ShowDnsAlerts = false
        };
        string json = UiSettingsStore.ToJson(settings);
        var restored = UiSettingsStore.FromJson(json);
        Assert.False(restored.AreNotificationsEnabled);
        Assert.False(restored.ShowBrowserAlerts);
        Assert.True(restored.ShowAppAlerts);
        Assert.False(restored.ShowDnsAlerts);
    }

    [Fact]
    public void SuppressMeditationStartWarning_DefaultsToFalse()
    {
        var settings = new UiSettings();
        Assert.False(settings.SuppressMeditationStartWarning);

        var fromEmpty = UiSettingsStore.FromJson("{}");
        Assert.False(fromEmpty.SuppressMeditationStartWarning);
    }

    [Fact]
    public void RoundTrip_SuppressMeditationStartWarning_Preserved()
    {
        var settings = new UiSettings { SuppressMeditationStartWarning = true };
        string json = UiSettingsStore.ToJson(settings);
        var restored = UiSettingsStore.FromJson(json);
        Assert.True(restored.SuppressMeditationStartWarning);
    }

    [Fact]
    public void UiSettingsStore_Instance_ReturnsValidSingleton()
    {
        Assert.NotNull(UiSettingsStore.Instance);
    }
}
