using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Blocker.Core;

namespace Blocker.App;

public class BrowserDialogItemViewModel : ViewModelBase
{
    public string DisplayName { get; set; } = string.Empty;
    public string ExeName { get; set; } = string.Empty;

    public string DetectedText { get; set; } = "Not detected";
    public string DetectedBackground { get; set; } = "#262626";
    public string DetectedForeground { get; set; } = "#888888";

    public string ConnectedText { get; set; } = "Not connected";
    public string ConnectedBackground { get; set; } = "#422006";
    public string ConnectedForeground { get; set; } = "#FCD34D";

    public string IncognitoText { get; set; } = "Incognito off";
    public string IncognitoBackground { get; set; } = "#262626";
    public string IncognitoForeground { get; set; } = "#888888";
}

public partial class ExtensionDialog : Window
{
    private readonly StatusData? _status;
    public ObservableCollection<BrowserDialogItemViewModel> Browsers { get; } = new();

    public ExtensionDialog(StatusData? status = null)
    {
        InitializeComponent();
        _status = status;
        BrowsersList.ItemsSource = Browsers;

        string repoRoot = FindRepoRoot();
        string extensionFolder = Path.Combine(repoRoot, "extension");
        FolderPathBox.Text = extensionFolder;
        ExtensionIdBox.Text = ExtensionHelper.DefaultExtensionId;
        FirefoxIdBox.Text = ExtensionHelper.DefaultFirefoxExtensionId;

        RefreshBrowserStatuses();
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "Blocker.sln")) || Directory.Exists(Path.Combine(dir, "extension")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return AppContext.BaseDirectory;
    }

    private void RefreshBrowserStatuses()
    {
        Browsers.Clear();
        var detectedList = BrowserCatalog.GetDetectedBrowsers();
        var activeBrowsers = _status?.ExtensionStatus?.Browsers ?? new List<BrowserStatusInfo>();

        foreach (var browser in BrowserCatalog.SupportedBrowsers)
        {
            bool isDetected = detectedList.Any(b => b.Id == browser.Id);
            var activeInfo = activeBrowsers.FirstOrDefault(b =>
                string.Equals(b.ProcessName, browser.ExeName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(b.Name, browser.DisplayName, StringComparison.OrdinalIgnoreCase));

            bool isConnected = activeInfo?.Connected == true;
            bool incognitoAllowed = activeInfo?.IncognitoAllowed == true;
            bool isLight = !ThemeManager.IsDark;

            var item = new BrowserDialogItemViewModel
            {
                DisplayName = browser.DisplayName,
                ExeName = browser.ExeName,
                DetectedText = isDetected ? "Installed" : "Not detected",
                DetectedBackground = isDetected 
                    ? (isLight ? "#DCFCE7" : "#14532D") 
                    : (isLight ? "#F3F4F6" : "#262626"),
                DetectedForeground = isDetected 
                    ? (isLight ? "#15803D" : "#86EFAC") 
                    : (isLight ? "#6B7280" : "#888888"),

                ConnectedText = isConnected ? "Connected" : "Not connected",
                ConnectedBackground = isConnected 
                    ? (isLight ? "#DCFCE7" : "#14532D") 
                    : (isLight ? "#FEF3C7" : "#422006"),
                ConnectedForeground = isConnected 
                    ? (isLight ? "#15803D" : "#86EFAC") 
                    : (isLight ? "#B45309" : "#FCD34D"),

                IncognitoText = !isConnected ? "Incognito unknown" : (incognitoAllowed ? "Incognito allowed" : "Incognito off"),
                IncognitoBackground = !isConnected 
                    ? (isLight ? "#F3F4F6" : "#262626") 
                    : (incognitoAllowed ? (isLight ? "#DCFCE7" : "#14532D") : (isLight ? "#FEE2E2" : "#7F1D1D")),
                IncognitoForeground = !isConnected 
                    ? (isLight ? "#6B7280" : "#888888") 
                    : (incognitoAllowed ? (isLight ? "#15803D" : "#86EFAC") : (isLight ? "#B91C1C" : "#FCA5A5"))
            };

            Browsers.Add(item);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string path = FolderPathBox.Text;
        if (Directory.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(ExtensionIdBox.Text);
        System.Windows.MessageBox.Show("Chromium Extension ID copied to clipboard.", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CopyFirefoxId_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(FirefoxIdBox.Text);
        System.Windows.MessageBox.Show("Firefox Gecko ID copied to clipboard.", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenEdgeExtensions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("msedge.exe", "edge://extensions") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not launch Edge: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenChromeExtensions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("chrome.exe", "chrome://extensions") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not launch Chrome: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFirefoxDebugging_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("firefox.exe", "-url about:debugging#/runtime/this-firefox") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not launch Firefox: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFirefoxAddons_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("firefox.exe", "-url about:addons") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not launch Firefox: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RegisterHost_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? extId = ExtensionIdBox.Text?.Trim();
            string? ffId = FirefoxIdBox.Text?.Trim();
            var result = ExtensionHelper.RegisterHost(
                extensionId: string.IsNullOrWhiteSpace(extId) ? null : extId,
                firefoxExtensionId: string.IsNullOrWhiteSpace(ffId) ? null : ffId);
            RegistrationStatusTextBlock.Text = result.FormatSummary();
            RegistrationStatusTextBlock.Foreground = result.Success
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.Coral;
            RegistrationStatusTextBlock.Visibility = Visibility.Visible;
            RefreshBrowserStatuses();
        }
        catch (Exception ex)
        {
            RegistrationStatusTextBlock.Text = $"Error: {ex.Message}";
            RegistrationStatusTextBlock.Foreground = System.Windows.Media.Brushes.Coral;
            RegistrationStatusTextBlock.Visibility = Visibility.Visible;
        }
    }

    private void UnregisterHost_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ExtensionHelper.UnregisterHost();
            RegistrationStatusTextBlock.Text = "Unregistered from all browsers.";
            RegistrationStatusTextBlock.Foreground = System.Windows.Media.Brushes.Orange;
            RegistrationStatusTextBlock.Visibility = Visibility.Visible;
            RefreshBrowserStatuses();
        }
        catch (Exception ex)
        {
            RegistrationStatusTextBlock.Text = $"Error: {ex.Message}";
            RegistrationStatusTextBlock.Foreground = System.Windows.Media.Brushes.Coral;
            RegistrationStatusTextBlock.Visibility = Visibility.Visible;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
