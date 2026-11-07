using System;
using System.IO;
using Blocker.Core;
using Xunit;

namespace Sankalpa.Tests;

public class NotificationSettingsSyncTests : IDisposable
{
    private readonly string _tempFile;

    public NotificationSettingsSyncTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"ui-settings-test-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempFile))
            {
                File.Delete(_tempFile);
            }
        }
        catch { }
    }

    [Fact]
    public void UiSettings_NotificationProperties_SerializeAndDeserializeCorrectly()
    {
        var settings = new UiSettings
        {
            AreNotificationsEnabled = false,
            ShowBrowserAlerts = false,
            ShowAppAlerts = true,
            ShowDnsAlerts = false
        };

        UiSettingsStore.SaveToFile(settings, _tempFile);
        var loaded = UiSettingsStore.LoadFromFile(_tempFile);

        Assert.False(loaded.AreNotificationsEnabled);
        Assert.False(loaded.ShowBrowserAlerts);
        Assert.True(loaded.ShowAppAlerts);
        Assert.False(loaded.ShowDnsAlerts);
    }

    [Fact]
    public void UiSettings_DefaultsToAllNotificationsEnabled()
    {
        var settings = new UiSettings();

        Assert.True(settings.AreNotificationsEnabled);
        Assert.True(settings.ShowBrowserAlerts);
        Assert.True(settings.ShowAppAlerts);
        Assert.True(settings.ShowDnsAlerts);
    }
}
