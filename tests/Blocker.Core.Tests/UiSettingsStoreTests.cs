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
    public void ExtensionTheme_DefaultsToCurrent()
    {
        var settings = new UiSettings();
        Assert.Equal("Current", settings.ExtensionTheme);

        var fromEmpty = UiSettingsStore.FromJson("{}");
        Assert.Equal("Current", fromEmpty.ExtensionTheme);
    }

    [Theory]
    [InlineData("Current")]
    [InlineData("DeepBlack")]
    [InlineData("MinimalWhite")]
    public void RoundTrip_ExtensionTheme_Preserved(string theme)
    {
        var settings = new UiSettings { ExtensionTheme = theme };
        string json = UiSettingsStore.ToJson(settings);
        var restored = UiSettingsStore.FromJson(json);
        Assert.Equal(theme, restored.ExtensionTheme);
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
}
