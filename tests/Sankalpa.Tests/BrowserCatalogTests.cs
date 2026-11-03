using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class BrowserCatalogTests
{
    [Fact]
    public void SupportedBrowsers_ContainsAllExpectedBrowsers()
    {
        var list = BrowserCatalog.SupportedBrowsers;
        Assert.Equal(12, list.Count);

        var ids = list.Select(b => b.Id).ToList();
        Assert.Contains("chrome", ids);
        Assert.Contains("msedge", ids);
        Assert.Contains("brave", ids);
        Assert.Contains("firefox", ids);
        Assert.Contains("chromium", ids);
        Assert.Contains("vivaldi", ids);
        Assert.Contains("opera", ids);
        Assert.Contains("operagx", ids);
        Assert.Contains("waterfox", ids);
        Assert.Contains("librewolf", ids);
        Assert.Contains("floorp", ids);
        Assert.Contains("zen", ids);

        var edge = list.First(b => b.Id == "msedge");
        Assert.Equal("msedge.exe", edge.ExeName);
        Assert.Contains("Microsoft", edge.RegistryParentPath);
        Assert.Contains("NativeMessagingHosts", edge.NativeMessagingHostsRegistryPath);

        var firefox = list.First(b => b.Id == "firefox");
        Assert.Equal("firefox.exe", firefox.ExeName);
        Assert.True(firefox.IsGecko);
        Assert.Contains("Mozilla", firefox.RegistryParentPath);
        Assert.Contains("NativeMessagingHosts", firefox.NativeMessagingHostsRegistryPath);
    }

    [Fact]
    public void GenerateHostManifestJson_GeneratesValidFormat()
    {
        string hostPath = @"C:\Path\To\Blocker.NativeHost.exe";
        string id = "gbkcfglfhihlgeghnkkackpklfjgccfh";

        string json = ExtensionHelper.GenerateHostManifestJson(hostPath, id);
        Assert.Contains("\"name\": \"com.blocker.host\"", json);
        Assert.Contains("\"type\": \"stdio\"", json);
        Assert.Contains($"\"chrome-extension://{id}/\"", json);
    }

    [Fact]
    public void GenerateFirefoxHostManifestJson_GeneratesValidGeckoFormat()
    {
        string hostPath = @"C:\Path\To\Blocker.NativeHost.exe";
        string id = "sankalpa-blocker@local";

        string json = ExtensionHelper.GenerateFirefoxHostManifestJson(hostPath, id);
        Assert.Contains("\"name\": \"com.blocker.host\"", json);
        Assert.Contains("\"type\": \"stdio\"", json);
        Assert.Contains("\"allowed_extensions\"", json);
        Assert.Contains($"\"{id}\"", json);
        Assert.DoesNotContain("chrome-extension://", json);
    }

    [Fact]
    public void RegistryOperations_WriteReadDelete_WorksOnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        string testSubKey = @"Software\BlockerTestRegistry";
        string testValName = "TestHost";
        string testValData = @"C:\Test\host.json";

        try
        {
            BrowserCatalog.SetRegistryStringValue(testSubKey, testValName, testValData);
            Assert.True(BrowserCatalog.RegistryKeyExists(testSubKey));

            string? readBack = BrowserCatalog.GetRegistryStringValue(testSubKey, testValName);
            Assert.Equal(testValData, readBack);

            // Default value
            BrowserCatalog.SetRegistryStringValue(testSubKey, null, testValData);
            string? readDefault = BrowserCatalog.GetRegistryStringValue(testSubKey, null);
            Assert.Equal(testValData, readDefault);
        }
        finally
        {
            BrowserCatalog.DeleteRegistrySubKey(testSubKey);
            Assert.False(BrowserCatalog.RegistryKeyExists(testSubKey));
        }
    }

    [Fact]
    public void TryResolveNativeHostExePath_WhenExeNotFound_FailsWithClearMessage()
    {
        bool ok = ExtensionHelper.TryResolveNativeHostExePath(
            customPath: null,
            baseDirectory: @"C:\FakeAppDir",
            localAppDataDirectory: @"C:\FakeLocalAppData",
            fileExists: path => false,
            out string resolved,
            out string? error);

        Assert.False(ok);
        Assert.Empty(resolved);
        Assert.NotNull(error);
        Assert.Contains("Blocker.NativeHost.exe was not found", error);
        Assert.Contains(@"C:\FakeAppDir", error);
        Assert.Contains(@"C:\FakeLocalAppData", error);
    }

    [Fact]
    public void TryResolveNativeHostExePath_WhenCustomPathExists_ResolvesSuccessfully()
    {
        string custom = @"C:\CustomBin\Blocker.NativeHost.exe";
        bool ok = ExtensionHelper.TryResolveNativeHostExePath(
            customPath: custom,
            baseDirectory: @"C:\FakeAppDir",
            localAppDataDirectory: @"C:\FakeLocalAppData",
            fileExists: path => path.Equals(custom, StringComparison.OrdinalIgnoreCase),
            out string resolved,
            out string? error);

        Assert.True(ok);
        Assert.Equal(custom, resolved);
        Assert.Null(error);
    }

    [Fact]
    public void RegisterHost_WhenHostExeNotFound_WritesNothingAndFails()
    {
        bool manifestWritten = false;
        bool registryWritten = false;

        var result = ExtensionHelper.RegisterHost(
            hostExePath: null,
            extensionId: null,
            fileExists: path => false,
            registryWriter: (key, name, val) => registryWritten = true,
            manifestWriter: (path, json) => manifestWritten = true);

        Assert.False(result.Success);
        Assert.False(manifestWritten, "Must not write manifest when host exe is missing");
        Assert.False(registryWritten, "Must not write registry when host exe is missing");
        Assert.Contains("Blocker.NativeHost.exe was not found", result.ErrorMessage);
        Assert.Contains("Blocker.NativeHost.exe was not found", result.FormatSummary());
    }

    [Fact]
    public void RegisterHost_PerBrowserSuccessAndFailure_ReportsClearSummary()
    {
        string dummyExe = @"C:\Bin\Blocker.NativeHost.exe";
        var mockBrowsers = new List<BrowserInfo>
        {
            new("chrome", "Google Chrome", "chrome.exe", @"Software\Google\Chrome", @"Software\Google\Chrome\NativeMessagingHosts", Array.Empty<string>()),
            new("msedge", "Microsoft Edge", "msedge.exe", @"Software\Microsoft\Edge", @"Software\Microsoft\Edge\NativeMessagingHosts", Array.Empty<string>())
        };

        var result = ExtensionHelper.RegisterHost(
            hostExePath: dummyExe,
            extensionId: "testextensionid",
            fileExists: path => path == dummyExe,
            registryWriter: (key, name, val) =>
            {
                if (key.Contains("Edge")) throw new UnauthorizedAccessException("Registry access denied");
            },
            manifestWriter: (path, json) => { },
            targetBrowsers: mockBrowsers);

        Assert.True(result.Success);
        Assert.Equal(2, result.BrowserStatuses.Count);

        var chromeStatus = result.BrowserStatuses.First(b => b.BrowserName == "Google Chrome");
        Assert.True(chromeStatus.Success);

        var edgeStatus = result.BrowserStatuses.First(b => b.BrowserName == "Microsoft Edge");
        Assert.False(edgeStatus.Success);
        Assert.Contains("Registry access denied", edgeStatus.ErrorMessage);

        string summary = result.FormatSummary();
        Assert.Contains("Registered for: Google Chrome", summary);
        Assert.Contains("Failed: Microsoft Edge: Registry access denied", summary);
    }

    [Fact]
    public void RegisterHost_GeneratesSeparateManifestsForChromiumAndFirefox()
    {
        string dummyExe = @"C:\Bin\Blocker.NativeHost.exe";
        var mockBrowsers = new List<BrowserInfo>
        {
            new("chrome", "Google Chrome", "chrome.exe", @"Software\Google\Chrome", @"Software\Google\Chrome\NativeMessagingHosts", Array.Empty<string>(), IsGecko: false),
            new("firefox", "Mozilla Firefox", "firefox.exe", @"Software\Mozilla\Mozilla Firefox", @"Software\Mozilla\NativeMessagingHosts", Array.Empty<string>(), IsGecko: true)
        };

        var writtenManifests = new Dictionary<string, string>();
        var writtenRegistry = new Dictionary<string, string>();

        var result = ExtensionHelper.RegisterHost(
            hostExePath: dummyExe,
            extensionId: "chrome_id_123",
            firefoxExtensionId: "firefox_id_456",
            fileExists: path => path == dummyExe,
            registryWriter: (key, name, val) => writtenRegistry[key] = val,
            manifestWriter: (path, json) => writtenManifests[path] = json,
            targetBrowsers: mockBrowsers);

        Assert.True(result.Success);
        Assert.Equal(2, result.BrowserStatuses.Count);

        string chromeManifest = ExtensionHelper.GetDefaultManifestPath(isGecko: false);
        string firefoxManifest = ExtensionHelper.GetDefaultManifestPath(isGecko: true);

        Assert.True(writtenManifests.ContainsKey(chromeManifest));
        Assert.True(writtenManifests.ContainsKey(firefoxManifest));

        Assert.Contains("chrome-extension://chrome_id_123/", writtenManifests[chromeManifest]);
        Assert.Contains("allowed_extensions", writtenManifests[firefoxManifest]);
        Assert.Contains("firefox_id_456", writtenManifests[firefoxManifest]);

        Assert.Equal(chromeManifest, writtenRegistry[@"Software\Google\Chrome\NativeMessagingHosts\com.blocker.host"]);
        Assert.Equal(firefoxManifest, writtenRegistry[@"Software\Mozilla\NativeMessagingHosts\com.blocker.host"]);
    }

    [Fact]
    public void ResolveAncestorBrowser_WhenLaunchedViaCmd_ResolvesBrowserAncestor()
    {
        var parentMap = new Dictionary<int, int>
        {
            [5000] = 4000, // NativeHost -> cmd.exe
            [4000] = 3000, // cmd.exe -> msedge.exe
            [3000] = 1000  // msedge.exe -> explorer.exe
        };
        var nameMap = new Dictionary<int, string>
        {
            [5000] = "Blocker.NativeHost.exe",
            [4000] = "cmd.exe",
            [3000] = "msedge.exe",
            [1000] = "explorer.exe"
        };
        var knownBrowsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome.exe", "msedge.exe" };

        var resolved = ProcessTreeHelper.ResolveAncestorBrowser(5000, parentMap, nameMap, knownBrowsers);

        Assert.NotNull(resolved);
        Assert.Equal(3000, resolved.Value.BrowserPid);
        Assert.Equal("msedge.exe", resolved.Value.BrowserExeName);
    }

    [Fact]
    public void ResolveAncestorBrowser_WhenNoBrowserAncestor_FallsBackToImmediateParent()
    {
        var parentMap = new Dictionary<int, int>
        {
            [5000] = 4000,
            [4000] = 1000
        };
        var nameMap = new Dictionary<int, string>
        {
            [5000] = "Blocker.NativeHost.exe",
            [4000] = "cmd.exe",
            [1000] = "explorer.exe"
        };
        var knownBrowsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome.exe", "msedge.exe" };

        var resolved = ProcessTreeHelper.ResolveAncestorBrowser(5000, parentMap, nameMap, knownBrowsers);

        Assert.NotNull(resolved);
        Assert.Equal(4000, resolved.Value.BrowserPid);
        Assert.Equal("cmd.exe", resolved.Value.BrowserExeName);
    }

    [Fact]
    public void TryResolveNativeHostExePath_WhenInDevSiblingFolder_ResolvesSuccessfully()
    {
        string baseDir = @"C:\Repo\src\Blocker.App\bin\Debug\net8.0-windows";
        string siblingDevExe = @"C:\Repo\src\Blocker.NativeHost\bin\Debug\net8.0-windows\Blocker.NativeHost.exe";

        bool ok = ExtensionHelper.TryResolveNativeHostExePath(
            customPath: null,
            baseDirectory: baseDir,
            localAppDataDirectory: @"C:\FakeAppData",
            fileExists: path => Path.GetFullPath(path).Equals(Path.GetFullPath(siblingDevExe), StringComparison.OrdinalIgnoreCase),
            out string resolved,
            out string? error);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(siblingDevExe), resolved);
        Assert.Null(error);
    }

    [Fact]
    public void EnsureAumidRegistered_SetsExpectedRegistryValues()
    {
        if (OperatingSystem.IsWindows())
        {
            ExtensionHelper.EnsureAumidRegistered();
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\Sankalpa");
            Assert.NotNull(key);
            Assert.Equal("Sankalpa", key.GetValue("DisplayName")?.ToString());
            Assert.Equal(1, Convert.ToInt32(key.GetValue("ShowInSettings")));
        }
    }
}
