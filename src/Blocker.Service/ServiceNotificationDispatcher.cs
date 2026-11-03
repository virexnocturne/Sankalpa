using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Blocker.Core;

namespace Blocker.Service;

public static class ServiceNotificationDispatcher
{
    private static readonly NotificationThrottler _throttler = new(TimeSpan.FromSeconds(4));

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint SessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int ImpersonationLevel,
        int TokenType,
        out IntPtr phNewToken);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    private static readonly object _logLock = new();
    private static void Log(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            // 1. Service base directory log
            try
            {
                string srvLogDir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(srvLogDir);
                File.AppendAllText(Path.Combine(srvLogDir, "service-notifications.log"), line + Environment.NewLine);
            }
            catch { }

            // 2. User AppData log
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string userLogDir = Path.Combine(localAppData, "Blocker", "logs");
                Directory.CreateDirectory(userLogDir);
                File.AppendAllText(Path.Combine(userLogDir, "service-notifications.log"), line + Environment.NewLine);
            }
            catch { }
        }
        catch { }
    }

    public static bool DispatchNotification(string kind, string name, string profileOrReason)
    {
        try
        {
            if (!_throttler.ShouldNotify(name, DateTime.UtcNow, kind))
            {
                return false;
            }

            Log($"[ServiceNotification] Dispatching notification: kind='{kind}', name='{name}', reason='{profileOrReason}'");

            // Check notification settings
            try
            {
                var settings = UiSettingsStore.Load();
                if (!settings.AreNotificationsEnabled)
                {
                    Log("[ServiceNotification] Notifications disabled in UiSettings.");
                    return false;
                }
                if (string.Equals(kind, "BrowserKill", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kind, "BrowserGrace", StringComparison.OrdinalIgnoreCase))
                {
                    if (!settings.ShowBrowserAlerts)
                    {
                        Log("[ServiceNotification] Browser alerts disabled in UiSettings.");
                        return false;
                    }
                }
                else if (string.Equals(kind, "DnsTamper", StringComparison.OrdinalIgnoreCase))
                {
                    if (!settings.ShowDnsAlerts)
                    {
                        Log("[ServiceNotification] DNS alerts disabled in UiSettings.");
                        return false;
                    }
                }
                else
                {
                    if (!settings.ShowAppAlerts)
                    {
                        Log("[ServiceNotification] App alerts disabled in UiSettings.");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[ServiceNotification] UiSettings check warning: {ex.Message}");
            }

            string hostExe = ResolveNativeHostExe();
            string safeKind = EscapeCliArg(kind);
            string safeName = EscapeCliArg(name);
            string safeReason = EscapeCliArg(profileOrReason);
            string arguments = $"--notify \"{safeKind}\" \"{safeName}\" \"{safeReason}\"";

            bool launched = false;
            if (!string.IsNullOrEmpty(hostExe) && File.Exists(hostExe))
            {
                try
                {
                    ExtensionHelper.EnsureAumidRegistered(hostExe);
                }
                catch { }

                launched = LaunchProcessInUserSession(hostExe, arguments);
            }

            if (!launched)
            {
                Log("[ServiceNotification] NativeHost launch was unsuccessful or not found. Falling back to PowerShell toast.");
                try
                {
                    ExtensionHelper.EnsureAumidRegistered();
                }
                catch { }

                launched = LaunchPowerShellToast(kind, name, profileOrReason);
            }

            return launched;
        }
        catch (Exception ex)
        {
            Log($"[ServiceNotification] DispatchNotification error: {ex.Message}");
            return false;
        }
    }

    private static string ResolveNativeHostExe()
    {
        // 1. Next to current service binary
        string candidateBase = Path.Combine(AppContext.BaseDirectory, "Blocker.NativeHost.exe");
        if (File.Exists(candidateBase)) return Path.GetFullPath(candidateBase);

        // 2. In LocalAppData
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string candidateAppData = Path.Combine(localAppData, "Blocker", "Blocker.NativeHost.exe");
        if (File.Exists(candidateAppData)) return Path.GetFullPath(candidateAppData);

        // 3. Inspect Registry NativeMessagingHosts entries registered by ExtensionHelper
        try
        {
            string[] regKeys = new[]
            {
                @"Software\Google\Chrome\NativeMessagingHosts\com.sankalpa.native_host",
                @"Software\Microsoft\Edge\NativeMessagingHosts\com.sankalpa.native_host",
                @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.sankalpa.native_host"
            };
            foreach (var rk in regKeys)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(rk) ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(rk);
                if (key != null)
                {
                    string? manifestPath = key.GetValue(null)?.ToString();
                    if (!string.IsNullOrEmpty(manifestPath) && File.Exists(manifestPath))
                    {
                        string json = File.ReadAllText(manifestPath);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("path", out var pProp))
                        {
                            string? exe = pProp.GetString();
                            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                            {
                                return Path.GetFullPath(exe);
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // 4. Walk up directory tree to search development and sibling project build outputs
        try
        {
            string? dir = AppContext.BaseDirectory;
            for (int depth = 0; depth < 8 && dir != null; depth++)
            {
                string[] candidates = new[]
                {
                    Path.Combine(dir, "Blocker.NativeHost", "bin", "Debug", "net8.0-windows10.0.19041.0", "Blocker.NativeHost.exe"),
                    Path.Combine(dir, "Blocker.NativeHost", "bin", "Release", "net8.0-windows10.0.19041.0", "Blocker.NativeHost.exe"),
                    Path.Combine(dir, "Blocker.NativeHost", "bin", "Debug", "net8.0-windows", "Blocker.NativeHost.exe"),
                    Path.Combine(dir, "Blocker.NativeHost", "bin", "Release", "net8.0-windows", "Blocker.NativeHost.exe"),
                    Path.Combine(dir, "src", "Blocker.NativeHost", "bin", "Debug", "net8.0-windows10.0.19041.0", "Blocker.NativeHost.exe"),
                    Path.Combine(dir, "src", "Blocker.NativeHost", "bin", "Release", "net8.0-windows10.0.19041.0", "Blocker.NativeHost.exe"),
                    Path.Combine(dir, "src", "Blocker.NativeHost", "bin", "Debug", "net8.0-windows", "Blocker.NativeHost.exe"),
                    Path.Combine(dir, "src", "Blocker.NativeHost", "bin", "Release", "net8.0-windows", "Blocker.NativeHost.exe"),
                    Path.Combine(dir, "Blocker.App", "bin", "Debug", "net8.0-windows", "Blocker.NativeHost.exe"),
                    Path.Combine(dir, "Blocker.App", "bin", "Release", "net8.0-windows", "Blocker.NativeHost.exe"),
                    Path.Combine(dir, "src", "Blocker.App", "bin", "Debug", "net8.0-windows", "Blocker.NativeHost.exe"),
                    Path.Combine(dir, "src", "Blocker.App", "bin", "Release", "net8.0-windows", "Blocker.NativeHost.exe"),
                    Path.Combine(dir, "dist", "Sankalpa-Release", "Blocker.NativeHost.exe"),
                    Path.Combine(dir, "dist", "publish", "native-host", "Blocker.NativeHost.exe"),
                    Path.Combine(dir, "Sankalpa-Release", "Blocker.NativeHost.exe")
                };

                foreach (var c in candidates)
                {
                    try
                    {
                        if (File.Exists(c)) return Path.GetFullPath(c);
                    }
                    catch { }
                }

                dir = Directory.GetParent(dir)?.FullName;
            }
        }
        catch { }

        return string.Empty;
    }

    private static bool LaunchProcessInUserSession(string exePath, string arguments)
    {
        try
        {
            int currentSessionId = Process.GetCurrentProcess().SessionId;
            if (currentSessionId != 0)
            {
                // Running in user interactive session (dev/debug console)
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                Log($"[ServiceNotification] Started in current interactive session: {exePath}");
                return true;
            }

            // Running in Session 0 as Windows Service: locate active interactive session
            uint activeSessionId = WTSGetActiveConsoleSessionId();
            if (activeSessionId == 0xFFFFFFFF || activeSessionId == 0)
            {
                var explorer = Process.GetProcessesByName("explorer").FirstOrDefault();
                if (explorer != null)
                {
                    activeSessionId = (uint)explorer.SessionId;
                }
            }

            if (activeSessionId == 0xFFFFFFFF || activeSessionId == 0)
            {
                Log("[ServiceNotification] No active interactive user session found.");
                return false;
            }

            IntPtr hUserToken = IntPtr.Zero;
            bool querySuccess = WTSQueryUserToken(activeSessionId, out hUserToken);
            if (!querySuccess || hUserToken == IntPtr.Zero)
            {
                int wtsErr = Marshal.GetLastWin32Error();
                Log($"[ServiceNotification] WTSQueryUserToken failed for session {activeSessionId} (error: {wtsErr}). Attempting explorer process token fallback.");

                // Fallback: Query token from explorer.exe running in the target session
                try
                {
                    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
                    const uint TOKEN_DUPLICATE = 0x0002;
                    const uint TOKEN_QUERY = 0x0008;
                    const uint TOKEN_ASSIGN_PRIMARY = 0x0001;

                    var explorerProcs = Process.GetProcessesByName("explorer");
                    foreach (var exp in explorerProcs)
                    {
                        if (exp.SessionId == (int)activeSessionId || (activeSessionId == 0 && exp.SessionId != 0))
                        {
                            IntPtr hExp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, exp.Id);
                            if (hExp != IntPtr.Zero)
                            {
                                if (OpenProcessToken(hExp, TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY, out hUserToken))
                                {
                                    activeSessionId = (uint)exp.SessionId;
                                    Log($"[ServiceNotification] Acquired user token via explorer.exe PID {exp.Id} in session {activeSessionId}");
                                    CloseHandle(hExp);
                                    break;
                                }
                                CloseHandle(hExp);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[ServiceNotification] Explorer token fallback exception: {ex.Message}");
                }
            }

            if (hUserToken == IntPtr.Zero)
            {
                Log("[ServiceNotification] Failed to acquire user token for notifications.");
                return false;
            }

            IntPtr hDuplicatedToken = IntPtr.Zero;
            IntPtr pEnv = IntPtr.Zero;
            try
            {
                const uint MAXIMUM_ALLOWED = 0x02000000;
                const int SecurityImpersonation = 2;
                const int TokenPrimary = 1;

                if (!DuplicateTokenEx(hUserToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out hDuplicatedToken))
                {
                    Log($"[ServiceNotification] DuplicateTokenEx failed. Win32 error: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                CreateEnvironmentBlock(out pEnv, hDuplicatedToken, false);

                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(si);
                si.lpDesktop = @"winsta0\default";

                const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
                const uint CREATE_NO_WINDOW = 0x08000000;
                uint creationFlags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;

                string commandLine = $"\"{exePath}\" {arguments}";

                if (CreateProcessAsUser(
                    hDuplicatedToken,
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    creationFlags,
                    pEnv != IntPtr.Zero ? pEnv : IntPtr.Zero,
                    Path.GetDirectoryName(exePath),
                    ref si,
                    out PROCESS_INFORMATION pi))
                {
                    Log($"[ServiceNotification] Launched '{exePath}' (PID: {pi.dwProcessId}) in user session {activeSessionId}");
                    CloseHandle(pi.hProcess);
                    CloseHandle(pi.hThread);
                    return true;
                }
                else
                {
                    Log($"[ServiceNotification] CreateProcessAsUser failed for '{exePath}'. Win32 error: {Marshal.GetLastWin32Error()}");
                    return false;
                }
            }
            finally
            {
                if (pEnv != IntPtr.Zero) DestroyEnvironmentBlock(pEnv);
                if (hDuplicatedToken != IntPtr.Zero) CloseHandle(hDuplicatedToken);
                if (hUserToken != IntPtr.Zero) CloseHandle(hUserToken);
            }
        }
        catch (Exception ex)
        {
            Log($"[ServiceNotification] LaunchProcessInUserSession error: {ex.Message}");
            return false;
        }
    }

    private static bool LaunchPowerShellToast(string kind, string name, string profileOrReason)
    {
        try
        {
            string title;
            string line1;
            string? line2 = null;
            if (string.Equals(kind, "BrowserKill", StringComparison.OrdinalIgnoreCase))
            {
                string disp = BrowserCatalog.GetDisplayName(name);
                title = $"⚠️ {disp} Terminated";
                line1 = $"Reason: {profileOrReason}";
                line2 = "Action: Install Sankalpa extension & enable 'Allow in InPrivate'.";
            }
            else if (string.Equals(kind, "BrowserGrace", StringComparison.OrdinalIgnoreCase))
            {
                string disp = BrowserCatalog.GetDisplayName(name);
                title = $"⏳ {disp} Closing Soon";
                line1 = profileOrReason;
            }
            else if (string.Equals(kind, "DnsTamper", StringComparison.OrdinalIgnoreCase))
            {
                title = "🛡️ DNS Shield Restored";
                line1 = $"DNS tampering was detected and reverted to {profileOrReason}.";
            }
            else
            {
                title = $"🚫 {name} Blocked";
                line1 = $"Blocked by profile '{profileOrReason}'.";
            }

            string escTitle = System.Security.SecurityElement.Escape(title) ?? title;
            string escLine1 = System.Security.SecurityElement.Escape(line1) ?? line1;
            string escLine2 = !string.IsNullOrEmpty(line2) ? (System.Security.SecurityElement.Escape(line2) ?? line2) : "";

            var sb = new System.Text.StringBuilder();
            sb.Append("<toast duration=\"short\"><visual><binding template=\"ToastGeneric\">");
            sb.Append("<text>").Append(escTitle).Append("</text>");
            sb.Append("<text>").Append(escLine1).Append("</text>");
            if (!string.IsNullOrEmpty(escLine2))
            {
                sb.Append("<text>").Append(escLine2).Append("</text>");
            }
            sb.Append("</binding></visual><audio src=\"ms-winsoundevent:Notification.Default\" /></toast>");

            string xmlText = sb.ToString();

            string psScript = 
                "try { " +
                "New-Item -Path 'HKCU:\\Software\\Classes\\AppUserModelId\\Sankalpa' -Force -ErrorAction SilentlyContinue | Out-Null; " +
                "Set-ItemProperty -Path 'HKCU:\\Software\\Classes\\AppUserModelId\\Sankalpa' -Name 'DisplayName' -Value 'Sankalpa' -Force -ErrorAction SilentlyContinue; " +
                "Set-ItemProperty -Path 'HKCU:\\Software\\Classes\\AppUserModelId\\Sankalpa' -Name 'ShowInSettings' -Value 1 -Force -ErrorAction SilentlyContinue; " +
                "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; " +
                "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null; " +
                $"$rawXml = @'\n{xmlText}\n'@; " +
                "$doc = [Windows.Data.Xml.Dom.XmlDocument]::new(); " +
                "$doc.LoadXml($rawXml); " +
                "$t = [Windows.UI.Notifications.ToastNotification]::new($doc); " +
                "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Sankalpa').Show($t); " +
                "Start-Sleep -Milliseconds 800; " +
                "} catch { }";

            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(psScript);
            string b64 = Convert.ToBase64String(bytes);

            string pwshExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            string args = $"-WindowStyle Hidden -NoProfile -NonInteractive -EncodedCommand {b64}";
            return LaunchProcessInUserSession(pwshExe, args);
        }
        catch (Exception ex)
        {
            Log($"[ServiceNotification] LaunchPowerShellToast error: {ex.Message}");
            return false;
        }
    }

    private static string EscapeCliArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return string.Empty;
        return arg.Replace("\"", "\\\"");
    }
}
