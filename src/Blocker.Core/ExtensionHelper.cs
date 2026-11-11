using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Blocker.Core;

public class BrowserRegistrationStatus
{
    public string BrowserName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public class HostRegistrationResult
{
    public bool Success { get; set; }
    public string ResolvedHostExePath { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public List<BrowserRegistrationStatus> BrowserStatuses { get; set; } = new();

    public string FormatSummary()
    {
        if (!Success)
        {
            return ErrorMessage ?? "Registration failed: native host executable not found.";
        }

        var successful = BrowserStatuses.Where(b => b.Success).Select(b => b.BrowserName).ToList();
        var failed = BrowserStatuses.Where(b => !b.Success).ToList();

        if (failed.Count == 0 && successful.Count > 0)
        {
            return $"Registered for: {string.Join(", ", successful)}";
        }

        if (successful.Count > 0 && failed.Count > 0)
        {
            string failures = string.Join("; ", failed.Select(f => $"{f.BrowserName}: {f.ErrorMessage}"));
            return $"Registered for: {string.Join(", ", successful)} (Failed: {failures})";
        }

        if (failed.Count > 0)
        {
            return $"Failed for: {string.Join("; ", failed.Select(f => $"{f.BrowserName}: {f.ErrorMessage}"))}";
        }

        return "No supported browsers found to register.";
    }
}

public static class ExtensionHelper
{
    public const string HostName = "com.blocker.host";
    public const string DefaultExtensionId = "gbkcfglfhihlgeghnkkackpklfjgccfh";
    public const string DefaultFirefoxExtensionId = "sankalpa-blocker@local";

    public static string GetDefaultManifestPath(bool isGecko = false)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string fileName = isGecko ? $"{HostName}.firefox.json" : $"{HostName}.json";
        return Path.Combine(localAppData, "Blocker", "native-host", fileName);
    }

    /// <summary>
    /// Computes the Chrome 32-character extension ID from a Base64-encoded SubjectPublicKeyInfo (the "key" field in manifest.json).
    /// Algorithm: SHA-256 of the DER bytes, first 16 bytes (32 nibbles), each mapped 0->'a' .. 15->'p'.
    /// </summary>
    public static string ComputeExtensionIdFromManifestKey(string base64ManifestKey)
    {
        if (string.IsNullOrWhiteSpace(base64ManifestKey))
        {
            throw new ArgumentException("Manifest key cannot be empty.", nameof(base64ManifestKey));
        }

        byte[] derBytes = Convert.FromBase64String(base64ManifestKey.Trim());
        return ComputeExtensionIdFromPublicKey(derBytes);
    }

    /// <summary>
    /// Computes the Chrome 32-character extension ID from SubjectPublicKeyInfo DER bytes.
    /// </summary>
    public static string ComputeExtensionIdFromPublicKey(byte[] derPublicKey)
    {
        if (derPublicKey == null || derPublicKey.Length == 0)
        {
            throw new ArgumentException("Public key bytes cannot be empty.", nameof(derPublicKey));
        }

        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(derPublicKey);

        var chars = new char[32];
        for (int i = 0; i < 16; i++)
        {
            chars[i * 2] = (char)('a' + ((hash[i] >> 4) & 0x0F));
            chars[i * 2 + 1] = (char)('a' + (hash[i] & 0x0F));
        }

        return new string(chars);
    }

    /// <summary>
    /// Generates the Native Messaging Host manifest JSON for Chromium browsers (Chrome, Edge, Brave, Chromium, Vivaldi).
    /// </summary>
    public static string GenerateHostManifestJson(string hostExePath, string extensionId)
    {
        var manifest = new
        {
            name = HostName,
            description = "Sankalpa Blocker Native Messaging Host",
            path = hostExePath,
            type = "stdio",
            allowed_origins = new[]
            {
                $"chrome-extension://{extensionId}/"
            }
        };

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Generates the Native Messaging Host manifest JSON for Mozilla Firefox.
    /// Firefox strictly requires allowed_extensions instead of allowed_origins.
    /// </summary>
    public static string GenerateFirefoxHostManifestJson(string hostExePath, string geckoExtensionId)
    {
        var manifest = new
        {
            name = HostName,
            description = "Sankalpa Blocker Native Messaging Host",
            path = hostExePath,
            type = "stdio",
            allowed_extensions = new[]
            {
                geckoExtensionId
            }
        };

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool TryResolveNativeHostExePath(
        string? customPath,
        string baseDirectory,
        string localAppDataDirectory,
        Func<string, bool> fileExists,
        out string resolvedPath,
        out string? error)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            string trimmed = customPath.Trim();
            if (fileExists(trimmed))
            {
                resolvedPath = Path.GetFullPath(trimmed);
                error = null;
                return true;
            }
            resolvedPath = string.Empty;
            error = $"Native host executable not found at specified path: '{trimmed}'.";
            return false;
        }

        string candidateBase = Path.Combine(baseDirectory, "Blocker.NativeHost.exe");
        if (fileExists(candidateBase))
        {
            resolvedPath = Path.GetFullPath(candidateBase);
            error = null;
            return true;
        }

        string candidateAppData = Path.Combine(localAppDataDirectory, "Blocker", "Blocker.NativeHost.exe");
        if (fileExists(candidateAppData))
        {
            resolvedPath = Path.GetFullPath(candidateAppData);
            error = null;
            return true;
        }

        // Check development / sibling build output directories
        string[] devCandidates = new[]
        {
            Path.Combine(baseDirectory, "..", "..", "..", "..", "Blocker.NativeHost", "bin", "Debug", "net8.0-windows", "Blocker.NativeHost.exe"),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "Blocker.NativeHost", "bin", "Release", "net8.0-windows", "Blocker.NativeHost.exe"),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "src", "Blocker.NativeHost", "bin", "Debug", "net8.0-windows", "Blocker.NativeHost.exe"),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "src", "Blocker.NativeHost", "bin", "Release", "net8.0-windows", "Blocker.NativeHost.exe"),
            Path.Combine(baseDirectory, "..", "..", "Blocker.NativeHost", "bin", "Debug", "net8.0-windows", "Blocker.NativeHost.exe"),
            Path.Combine(baseDirectory, "..", "..", "Blocker.NativeHost", "bin", "Release", "net8.0-windows", "Blocker.NativeHost.exe")
        };

        foreach (var devCandidate in devCandidates)
        {
            try
            {
                if (fileExists(devCandidate))
                {
                    resolvedPath = Path.GetFullPath(devCandidate);
                    error = null;
                    return true;
                }
            }
            catch
            {
                // Ignore invalid paths
            }
        }

        resolvedPath = string.Empty;
        error = $"Blocker.NativeHost.exe was not found at '{candidateBase}' or '{candidateAppData}'. Please build the solution or install the native host before registering.";
        return false;
    }

    /// <summary>
    /// Registers the Native Messaging Host for detected browsers (Chromium and Firefox).
    /// Fails with a clear message, writing nothing, when Blocker.NativeHost.exe is not found at the resolved path.
    /// Reports per-browser success/failure instead of swallowing exceptions.
    /// </summary>
    public static HostRegistrationResult RegisterHost(
        string? hostExePath = null,
        string? extensionId = null,
        string? firefoxExtensionId = null,
        Func<string, bool>? fileExists = null,
        Action<string, string?, string>? registryWriter = null,
        Action<string, string>? manifestWriter = null,
        IEnumerable<BrowserInfo>? targetBrowsers = null)
    {
        fileExists ??= File.Exists;
        string baseDir = AppContext.BaseDirectory;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!TryResolveNativeHostExePath(hostExePath, baseDir, localAppData, fileExists, out string resolvedExe, out string? resolveError))
        {
            return new HostRegistrationResult
            {
                Success = false,
                ErrorMessage = resolveError
            };
        }

        if (string.IsNullOrWhiteSpace(extensionId))
        {
            extensionId = DefaultExtensionId;
        }

        if (string.IsNullOrWhiteSpace(firefoxExtensionId))
        {
            firefoxExtensionId = DefaultFirefoxExtensionId;
        }

        // 1. Generate & write Chrome/Chromium manifest
        string chromeManifestPath = GetDefaultManifestPath(isGecko: false);
        string chromeManifestJson = GenerateHostManifestJson(resolvedExe, extensionId);

        // 2. Generate & write Firefox manifest
        string firefoxManifestPath = GetDefaultManifestPath(isGecko: true);
        string firefoxManifestJson = GenerateFirefoxHostManifestJson(resolvedExe, firefoxExtensionId);

        if (manifestWriter != null)
        {
            manifestWriter(chromeManifestPath, chromeManifestJson);
            manifestWriter(firefoxManifestPath, firefoxManifestJson);
        }
        else
        {
            string? chromeDir = Path.GetDirectoryName(chromeManifestPath);
            if (!string.IsNullOrEmpty(chromeDir))
            {
                Directory.CreateDirectory(chromeDir);
            }
            File.WriteAllText(chromeManifestPath, chromeManifestJson);
            File.WriteAllText(firefoxManifestPath, firefoxManifestJson);
        }

        // Ensure AppUserModelID is registered for Windows Toast notifications
        EnsureAumidRegistered(resolvedExe);

        var browsersToRegister = (targetBrowsers ?? BrowserCatalog.SupportedBrowsers).ToList();

        var result = new HostRegistrationResult
        {
            Success = true,
            ResolvedHostExePath = resolvedExe
        };

        foreach (var browser in browsersToRegister)
        {
            string manifestForBrowser = browser.IsGecko ? firefoxManifestPath : chromeManifestPath;
            string subKey = $@"{browser.NativeMessagingHostsRegistryPath}\{HostName}";
            try
            {
                if (registryWriter != null)
                {
                    registryWriter(subKey, null, manifestForBrowser);
                }
                else
                {
                    BrowserCatalog.SetRegistryStringValue(subKey, null, manifestForBrowser);
                }

                result.BrowserStatuses.Add(new BrowserRegistrationStatus
                {
                    BrowserName = browser.DisplayName,
                    Success = true
                });
            }
            catch (Exception ex)
            {
                result.BrowserStatuses.Add(new BrowserRegistrationStatus
                {
                    BrowserName = browser.DisplayName,
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Unregisters the Native Messaging Host from all supported browsers and deletes manifests.
    /// </summary>
    public static void UnregisterHost()
    {
        foreach (var browser in BrowserCatalog.SupportedBrowsers)
        {
            string subKey = $@"{browser.NativeMessagingHostsRegistryPath}\{HostName}";
            BrowserCatalog.DeleteRegistrySubKey(subKey);
        }

        string[] manifestPaths = new[]
        {
            GetDefaultManifestPath(isGecko: false),
            GetDefaultManifestPath(isGecko: true)
        };

        foreach (var manifestPath in manifestPaths)
        {
            try
            {
                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                }
            }
            catch
            {
                // Ignore file deletion errors
            }
        }
    }

    /// <summary>
    /// Checks which browsers currently have the extension host registered and valid.
    /// </summary>
    public static List<string> GetRegisteredBrowsers()
    {
        var registered = new List<string>();
        foreach (var browser in BrowserCatalog.SupportedBrowsers)
        {
            string subKey = $@"{browser.NativeMessagingHostsRegistryPath}\{HostName}";
            string? val = BrowserCatalog.GetRegistryStringValue(subKey, null);
            if (!string.IsNullOrEmpty(val) && File.Exists(val))
            {
                registered.Add(browser.DisplayName);
            }
        }
        return registered;
    }

    #region AUMID and Start Menu Shortcut COM Interop

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;

        public PropertyKey(Guid guid, uint id)
        {
            fmtid = guid;
            pid = id;
        }
    }

    private static readonly PropertyKey PKEY_AppUserModel_ID = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotKey(out short pwHotKey);
        void SetHotKey(short wHotKey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int piShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        uint GetCount(out uint cProps);
        uint GetAt(uint iProp, out PropertyKey pkey);
        uint GetValue(ref PropertyKey key, [Out] PropVariant pv);
        uint SetValue(ref PropertyKey key, [In] PropVariant pv);
        uint Commit();
    }

    [StructLayout(LayoutKind.Explicit)]
    private sealed class PropVariant : IDisposable
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr pwszVal;

        public PropVariant(string str)
        {
            vt = 31; // VT_LPWSTR
            pwszVal = Marshal.StringToCoTaskMemUni(str);
        }

        public void Dispose()
        {
            if (vt == 31 && pwszVal != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pwszVal);
                pwszVal = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static void CreateOrUpdateShortcut(string shortcutPath, string targetExe, string? iconPath, string aumid)
    {
        try
        {
            var shellLink = (IShellLinkW)new ShellLink();
            shellLink.SetPath(targetExe);
            string? workDir = Path.GetDirectoryName(targetExe);
            if (!string.IsNullOrEmpty(workDir))
            {
                shellLink.SetWorkingDirectory(workDir);
            }
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                shellLink.SetIconLocation(iconPath, 0);
            }

            var propertyStore = (IPropertyStore)shellLink;
            using (var propVar = new PropVariant(aumid))
            {
                var key = PKEY_AppUserModel_ID;
                propertyStore.SetValue(ref key, propVar);
                propertyStore.Commit();
            }

            var persistFile = (IPersistFile)shellLink;
            string? dir = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            persistFile.Save(shortcutPath, true);

            // Notify Windows Shell to refresh shortcut cache
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Ignore shortcut creation failures
        }
    }

    #endregion

    /// <summary>
    /// Registers AppUserModelID 'Sankalpa' in the Windows Registry (HKCU and HKLM if permitted)
    /// and ensures an AUMID-linked Start Menu shortcut exists so unpackaged background processes
    /// can display Windows Action Center Toast notifications directly on screen.
    /// </summary>
    public static void EnsureAumidRegistered(string? exePath = null)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string? targetExe = null;
                string baseDir = AppContext.BaseDirectory;

                // Priority 1: Check if exePath points to Sankalpa.exe or its sibling in the same directory
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    if (Path.GetFileName(exePath).Equals("Sankalpa.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        targetExe = Path.GetFullPath(exePath);
                    }
                    else
                    {
                        string? dir = Path.GetDirectoryName(exePath);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            string siblingUi = Path.Combine(dir, "Sankalpa.exe");
                            if (File.Exists(siblingUi))
                            {
                                targetExe = Path.GetFullPath(siblingUi);
                            }
                        }
                    }
                }

                // Priority 2: Check AppContext.BaseDirectory for Sankalpa.exe
                if (string.IsNullOrEmpty(targetExe))
                {
                    string candidateUi = Path.Combine(baseDir, "Sankalpa.exe");
                    if (File.Exists(candidateUi))
                    {
                        targetExe = Path.GetFullPath(candidateUi);
                    }
                }

                // Priority 3: Check standard installation directories
                if (string.IsNullOrEmpty(targetExe))
                {
                    string pfCandidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sankalpa", "Sankalpa.exe");
                    if (File.Exists(pfCandidate))
                    {
                        targetExe = Path.GetFullPath(pfCandidate);
                    }
                    else
                    {
                        string laCandidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Sankalpa", "Sankalpa.exe");
                        if (File.Exists(laCandidate))
                        {
                            targetExe = Path.GetFullPath(laCandidate);
                        }
                    }
                }

                // Fallback: If Sankalpa.exe is still not found and exePath is an existing executable
                if (string.IsNullOrEmpty(targetExe) && !string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    targetExe = Path.GetFullPath(exePath);
                }

                // Priority for Icon: Assets\app.ico in target directory or BaseDirectory, falling back to targetExe
                string? iconPath = null;
                if (!string.IsNullOrEmpty(targetExe))
                {
                    string? targetDir = Path.GetDirectoryName(targetExe);
                    if (!string.IsNullOrEmpty(targetDir))
                    {
                        string targetAssetIcon = Path.Combine(targetDir, "Assets", "app.ico");
                        if (File.Exists(targetAssetIcon))
                        {
                            iconPath = targetAssetIcon;
                        }
                    }
                }
                if (string.IsNullOrEmpty(iconPath))
                {
                    string baseAssetIcon = Path.Combine(baseDir, "Assets", "app.ico");
                    if (File.Exists(baseAssetIcon))
                    {
                        iconPath = baseAssetIcon;
                    }
                    else if (!string.IsNullOrEmpty(targetExe))
                    {
                        iconPath = targetExe;
                    }
                }

                string aumidIcon = iconPath ?? targetExe ?? string.Empty;

                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\Sankalpa");
                if (key != null)
                {
                    key.SetValue("DisplayName", "Sankalpa", Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("ShowInSettings", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    if (!string.IsNullOrEmpty(aumidIcon) && File.Exists(aumidIcon))
                    {
                        key.SetValue("IconUri", aumidIcon, Microsoft.Win32.RegistryValueKind.String);
                    }
                }

                // Ensure notifications for AUMID 'Sankalpa' are allowed in Windows Settings
                WindowsNotificationHelper.EnableNotificationInWindows();

                try
                {
                    using var hklmKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"Software\Classes\AppUserModelId\Sankalpa");
                    if (hklmKey != null)
                    {
                        hklmKey.SetValue("DisplayName", "Sankalpa", Microsoft.Win32.RegistryValueKind.String);
                        hklmKey.SetValue("ShowInSettings", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        if (!string.IsNullOrEmpty(aumidIcon) && File.Exists(aumidIcon))
                        {
                            hklmKey.SetValue("IconUri", aumidIcon, Microsoft.Win32.RegistryValueKind.String);
                        }
                    }
                }
                catch { }

                // Start Menu shortcut with PKEY_AppUserModel_ID
                if (!string.IsNullOrEmpty(targetExe))
                {
                    var targetFolders = new List<string>();
                    string userPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                    if (!string.IsNullOrEmpty(userPrograms)) targetFolders.Add(userPrograms);
                    string commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
                    if (!string.IsNullOrEmpty(commonPrograms)) targetFolders.Add(commonPrograms);

                    foreach (var dir in targetFolders)
                    {
                        if (!Directory.Exists(dir)) continue;
                        string shortcutFile = Path.Combine(dir, "Sankalpa.lnk");
                        CreateOrUpdateShortcut(shortcutFile, targetExe, iconPath ?? targetExe, "Sankalpa");
                    }
                }
            }
        }
        catch { }
    }
}
