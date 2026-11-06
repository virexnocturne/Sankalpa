using System;
using System.IO;
using Blocker.Core;
using Xunit;

namespace Blocker.Tests;

public class MeditationStartWarningTests
{
    private static string GetRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Blocker.sln")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }
        throw new InvalidOperationException("Could not locate repository root containing Blocker.sln");
    }

    [Fact]
    public void UiSettingsStore_SuppressMeditationStartWarning_RoundTrip()
    {
        var settings = new UiSettings { SuppressMeditationStartWarning = true };
        string json = UiSettingsStore.ToJson(settings);
        var restored = UiSettingsStore.FromJson(json);
        Assert.True(restored.SuppressMeditationStartWarning);

        var def = new UiSettings();
        Assert.False(def.SuppressMeditationStartWarning);
    }

    [Fact]
    public void UiSettingsStore_Instance_AllowsAccessAndSave()
    {
        Assert.NotNull(UiSettingsStore.Instance);
        bool orig = UiSettingsStore.Instance.SuppressMeditationStartWarning;
        try
        {
            UiSettingsStore.Instance.SuppressMeditationStartWarning = true;
            Assert.True(UiSettingsStore.Instance.SuppressMeditationStartWarning);
            Assert.True(UiSettingsStore.SuppressMeditationStartWarning);
            UiSettingsStore.Instance.Save();
        }
        finally
        {
            UiSettingsStore.Instance.SuppressMeditationStartWarning = orig;
            UiSettingsStore.Instance.Save();
        }
    }

    [Fact]
    public void MeditationStartWarningDialog_Xaml_MeetsRequirements()
    {
        string root = GetRepoRoot();
        string xamlPath = Path.Combine(root, "src", "Blocker.App", "MeditationStartWarningDialog.xaml");
        Assert.True(File.Exists(xamlPath));

        string xaml = File.ReadAllText(xamlPath);
        Assert.Contains("Enter Focus Lockdown?", xaml);
        Assert.Contains("Session Duration", xaml);
        Assert.Contains("Lock Rigor", xaml);
        Assert.Contains("Allowed Apps", xaml);
        Assert.Contains("0 allowed apps — Full Screen Lockout", xaml);
        Assert.Contains("You are about to enter full-screen lockdown. Desktop access, task switching, and background applications will be blocked until the session concludes.", xaml);
        Assert.Contains("Do not show this warning again", xaml);
        Assert.Contains("Start Focus Session", xaml);
        Assert.Contains("Cancel", xaml);
        Assert.Contains("PrimaryButton", xaml);
        Assert.Contains("SecondaryButton", xaml);
    }

    [Fact]
    public void MeditationStartWarningDialog_CodeBehind_MeetsRequirements()
    {
        string root = GetRepoRoot();
        string csPath = Path.Combine(root, "src", "Blocker.App", "MeditationStartWarningDialog.xaml.cs");
        Assert.True(File.Exists(csPath));

        string code = File.ReadAllText(csPath);
        Assert.Contains("DoNotShowAgain", code);
        Assert.Contains("FormatLockRigor", code);
        Assert.Contains("Complete Lock", code);
        Assert.Contains("Password", code);
        Assert.Contains("Standard", code);
        Assert.Contains("SetDuration", code);
        Assert.Contains("SetLockRigor", code);
        Assert.Contains("SetAllowedApps", code);
        Assert.Contains("StartSessionButton_Click", code);
        Assert.Contains("CancelButton_Click", code);
    }

    [Fact]
    public void MeditationViewModel_StartMeditation_ContainsPreFlightWarningContract()
    {
        string root = GetRepoRoot();
        string vmPath = Path.Combine(root, "src", "Blocker.App", "MeditationViewModel.cs");
        Assert.True(File.Exists(vmPath));

        string vmCode = File.ReadAllText(vmPath);
        Assert.Contains("UiSettingsStore.Instance.SuppressMeditationStartWarning", vmCode);
        Assert.Contains("MeditationStartWarningDialog", vmCode);
        Assert.Contains("dlg.ShowDialog()", vmCode);
        Assert.Contains("dlg.DoNotShowAgain", vmCode);
        Assert.Contains("UiSettingsStore.Instance.Save()", vmCode);
    }

    [Fact]
    public void AppearanceDialog_ContainsWarningToggleContract()
    {
        string root = GetRepoRoot();
        string xamlPath = Path.Combine(root, "src", "Blocker.App", "AppearanceDialog.xaml");
        string csPath = Path.Combine(root, "src", "Blocker.App", "AppearanceDialog.xaml.cs");
        Assert.True(File.Exists(xamlPath));
        Assert.True(File.Exists(csPath));

        string xamlCode = File.ReadAllText(xamlPath);
        Assert.Contains("Show warning before starting Meditation &amp; Lockdown", xamlCode);
        Assert.Contains("MeditationWarningToggle", xamlCode);
        Assert.Contains("ShowMeditationStartWarning", xamlCode);

        string csCode = File.ReadAllText(csPath);
        Assert.Contains("ShowMeditationStartWarning", csCode);
        Assert.Contains("SuppressMeditationStartWarning", csCode);
        Assert.Contains("MeditationWarningToggle_Click", csCode);
    }
}
