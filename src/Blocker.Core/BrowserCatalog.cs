using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Blocker.Core;

public record BrowserInfo(
    string Id,
    string DisplayName,
    string ExeName,
    string RegistryParentPath,
    string NativeMessagingHostsRegistryPath,
    string[] RelativeExePaths,
    bool IsGecko = false
);

public readonly record struct ProcessWindowInfo(string ProcessName, bool HasWindow);

public static class BrowserCatalog
{
    private static readonly IntPtr HKEY_CURRENT_USER = unchecked((IntPtr)(long)(int)0x80000001);
    private static readonly IntPtr HKEY_LOCAL_MACHINE = unchecked((IntPtr)(long)(int)0x80000002);
    private const int KEY_READ = 0x20019;
    private const int KEY_WRITE = 0x20006;
    private const int REG_SZ = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, int ulOptions, int samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegCreateKeyExW(IntPtr hKey, string lpSubKey, int reserved, string? lpClass, int dwOptions, int samDesired, IntPtr lpSecurityAttributes, out IntPtr phkResult, out int lpdwDisposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueExW(IntPtr hKey, string? lpValueName, int reserved, int dwType, string lpData, int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(IntPtr hKey, string? lpValueName, int lpReserved, out int lpType, System.Text.StringBuilder lpData, ref int lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteKeyW(IntPtr hKey, string lpSubKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);

    public static readonly IReadOnlyList<BrowserInfo> SupportedBrowsers = new List<BrowserInfo>
    {
        new(
            "chrome",
            "Google Chrome",
            "chrome.exe",
            @"Software\Google\Chrome",
            @"Software\Google\Chrome\NativeMessagingHosts",
            new[]
            {
                @"Google\Chrome\Application\chrome.exe"
            }
        ),
        new(
            "msedge",
            "Microsoft Edge",
            "msedge.exe",
            @"Software\Microsoft\Edge",
            @"Software\Microsoft\Edge\NativeMessagingHosts",
            new[]
            {
                @"Microsoft\Edge\Application\msedge.exe"
            }
        ),
        new(
            "brave",
            "Brave",
            "brave.exe",
            @"Software\BraveSoftware\Brave-Browser",
            @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
            new[]
            {
                @"BraveSoftware\Brave-Browser\Application\brave.exe"
            }
        ),
        new(
            "firefox",
            "Mozilla Firefox",
            "firefox.exe",
            @"Software\Mozilla\Mozilla Firefox",
            @"Software\Mozilla\NativeMessagingHosts",
            new[]
            {
                @"Mozilla Firefox\firefox.exe",
                @"Programs\Mozilla Firefox\firefox.exe"
            },
            IsGecko: true
        ),
        new(
            "chromium",
            "Chromium",
            "chrome.exe",
            @"Software\Chromium",
            @"Software\Chromium\NativeMessagingHosts",
            new[]
            {
                @"Chromium\Application\chrome.exe"
            }
        ),
        new(
            "vivaldi",
            "Vivaldi",
            "vivaldi.exe",
            @"Software\Vivaldi",
            @"Software\Vivaldi\NativeMessagingHosts",
            new[]
            {
                @"Vivaldi\Application\vivaldi.exe"
            }
        ),
        new(
            "opera",
            "Opera",
            "opera.exe",
            @"Software\Opera Software",
            @"Software\Opera Software\NativeMessagingHosts",
            new[]
            {
                @"Opera\opera.exe",
                @"Programs\Opera\opera.exe",
                @"Opera Stable\opera.exe"
            }
        ),
        new(
            "operagx",
            "Opera GX",
            "operagx.exe",
            @"Software\Opera Software",
            @"Software\Opera Software\NativeMessagingHosts",
            new[]
            {
                @"Opera GX\opera.exe",
                @"Programs\Opera GX\operagx.exe",
                @"Programs\Opera GX\opera.exe"
            }
        ),
        new(
            "waterfox",
            "Waterfox",
            "waterfox.exe",
            @"Software\Waterfox",
            @"Software\Mozilla\NativeMessagingHosts",
            new[]
            {
                @"Waterfox\waterfox.exe",
                @"Programs\Waterfox\waterfox.exe"
            },
            IsGecko: true
        ),
        new(
            "librewolf",
            "LibreWolf",
            "librewolf.exe",
            @"Software\LibreWolf",
            @"Software\Mozilla\NativeMessagingHosts",
            new[]
            {
                @"LibreWolf\librewolf.exe",
                @"Programs\LibreWolf\librewolf.exe"
            },
            IsGecko: true
        ),
        new(
            "floorp",
            "Floorp",
            "floorp.exe",
            @"Software\Floorp",
            @"Software\Mozilla\NativeMessagingHosts",
            new[]
            {
                @"Floorp\floorp.exe",
                @"Programs\Floorp\floorp.exe"
            },
            IsGecko: true
        ),
        new(
            "zen",
            "Zen Browser",
            "zen.exe",
            @"Software\Zen Browser",
            @"Software\Mozilla\NativeMessagingHosts",
            new[]
            {
                @"Zen Browser\zen.exe",
                @"Programs\Zen Browser\zen.exe",
                @"zen\zen.exe"
            },
            IsGecko: true
        )
    };

    public static string GetDisplayName(string exeOrName)
    {
        if (string.IsNullOrWhiteSpace(exeOrName)) return "Browser";
        string clean = exeOrName.Trim();
        if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring(0, clean.Length - 4);
        }
        var found = SupportedBrowsers.FirstOrDefault(b =>
            b.Id.Equals(clean, StringComparison.OrdinalIgnoreCase) ||
            b.ExeName.Equals(clean, StringComparison.OrdinalIgnoreCase) ||
            b.ExeName.Equals(clean + ".exe", StringComparison.OrdinalIgnoreCase));
        if (found != null) return found.DisplayName;
        return clean;
    }

    /// <summary>
    /// Checks if a browser is detected (either registry parent key exists or exe exists in standard folders).
    /// </summary>
    public static bool IsBrowserDetected(BrowserInfo browser)
    {
        if (RegistryKeyExists(browser.RegistryParentPath) ||
            (browser.IsGecko && (RegistryKeyExists(@"Software\Mozilla\Firefox") || RegistryKeyExists(@"Software\Mozilla"))))
        {
            return true;
        }

        string[] standardRoots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        foreach (var root in standardRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;

            foreach (var relPath in browser.RelativeExePaths)
            {
                string fullPath = Path.Combine(root, relPath);
                if (File.Exists(fullPath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static List<BrowserInfo> GetDetectedBrowsers()
    {
        var list = new List<BrowserInfo>();
        foreach (var b in SupportedBrowsers)
        {
            if (IsBrowserDetected(b))
            {
                list.Add(b);
            }
        }
        return list;
    }

    /// <summary>
    /// Filters running processes to supported Chromium and Gecko browsers that have an active top-level window.
    /// Windowless background/helper processes are excluded to avoid triggering false fallbacks.
    /// </summary>
    public static List<string> FilterActiveBrowserProcesses(IEnumerable<ProcessWindowInfo>? processes)
    {
        if (processes == null) return new List<string>();

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in processes)
        {
            if (!p.HasWindow) continue;

            string name = p.ProcessName?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name)) continue;

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name += ".exe";
            }

            if (SupportedBrowsers.Any(b => string.Equals(b.ExeName, name, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(name);
            }
        }

        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Known browsers that cannot be monitored by the Sankalpa extension (e.g. Tor, Opera, PaleMoon, etc.).
    /// Used by the background service to terminate unauthorized browsers during active web-blocking sessions.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownUnsupportedBrowsers = new[]
    {
        "tor.exe",
        "palemoon.exe",
        "safari.exe",
        "maxthon.exe",
        "epic.exe",
        "avastbrowser.exe",
        "ccleanerbrowser.exe",
        "duckduckgo.exe",
        "sidekick.exe",
        "wavebrowser.exe",
        "arc.exe",
        "chromium.exe",
        "thorium.exe",
        "mullvadbrowser.exe",
        "iridium.exe"
    };

    public static bool IsSupportedBrowser(string exeName)
    {
        string norm = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName : exeName + ".exe";
        return SupportedBrowsers.Any(b => string.Equals(b.ExeName, norm, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsUnsupportedBrowser(string exeName)
    {
        string norm = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName : exeName + ".exe";
        return KnownUnsupportedBrowsers.Any(b => string.Equals(b, norm, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Filters running process names to both supported and known unsupported browser executable names.
    /// Does not require a window handle so background, multi-process, or child browser processes are accurately tracked.
    /// </summary>
    public static List<string> FilterAllBrowserProcesses(IEnumerable<string>? processNames)
    {
        if (processNames == null) return new List<string>();

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in processNames)
        {
            string name = p?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name)) continue;

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name += ".exe";
            }

            var supported = SupportedBrowsers.FirstOrDefault(b => string.Equals(b.ExeName, name, StringComparison.OrdinalIgnoreCase));
            if (supported != null)
            {
                result.Add(supported.ExeName.ToLowerInvariant());
                continue;
            }

            var unsupported = KnownUnsupportedBrowsers.FirstOrDefault(b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase));
            if (unsupported != null)
            {
                result.Add(unsupported.ToLowerInvariant());
            }
        }

        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Overload taking ProcessWindowInfo collection for backward compatibility.
    /// </summary>
    public static List<string> FilterAllBrowserProcesses(IEnumerable<ProcessWindowInfo>? processes)
    {
        if (processes == null) return new List<string>();
        return FilterAllBrowserProcesses(processes.Select(p => p.ProcessName));
    }

    public static bool RegistryKeyExists(string subKey)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        int ret = RegOpenKeyExW(HKEY_CURRENT_USER, subKey, 0, KEY_READ, out IntPtr hKey);
        if (ret == 0)
        {
            RegCloseKey(hKey);
            return true;
        }

        ret = RegOpenKeyExW(HKEY_LOCAL_MACHINE, subKey, 0, KEY_READ, out hKey);
        if (ret == 0)
        {
            RegCloseKey(hKey);
            return true;
        }

        return false;
    }

    public static void SetRegistryStringValue(string subKey, string? valueName, string valueData)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        int ret = RegCreateKeyExW(HKEY_CURRENT_USER, subKey, 0, null, 0, KEY_WRITE, IntPtr.Zero, out IntPtr hKey, out _);
        if (ret != 0)
        {
            throw new InvalidOperationException($"RegCreateKeyExW failed with error code {ret} for key {subKey}");
        }

        try
        {
            int cbData = (valueData.Length + 1) * 2; // UTF-16 with null terminator
            ret = RegSetValueExW(hKey, valueName, 0, REG_SZ, valueData, cbData);
            if (ret != 0)
            {
                throw new InvalidOperationException($"RegSetValueExW failed with error code {ret} for value '{valueName}' in key {subKey}");
            }
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    public static string? GetRegistryStringValue(string subKey, string? valueName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        int ret = RegOpenKeyExW(HKEY_CURRENT_USER, subKey, 0, KEY_READ, out IntPtr hKey);
        if (ret != 0) return null;

        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int cbData = sb.Capacity * 2;
            ret = RegQueryValueExW(hKey, valueName, 0, out int type, sb, ref cbData);
            if (ret == 0 && type == REG_SZ)
            {
                return sb.ToString();
            }
            return null;
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    public static bool DeleteRegistrySubKey(string subKey)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        int ret = RegDeleteKeyW(HKEY_CURRENT_USER, subKey);
        return ret == 0;
    }
}
