using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blocker.Core;

namespace Blocker.NativeHost;

public static class Program
{
    private static readonly object _logLock = new();
    private static string _logFilePath = string.Empty;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static int Main(string[] args)
    {
        InitializeLogging();

        // 1. CLI flag handling
        if (args.Length > 0)
        {
            if (string.Equals(args[0], "--notify", StringComparison.OrdinalIgnoreCase))
            {
                string kind = args.Length > 1 ? args[1] : "App";
                string name = args.Length > 2 ? args[2] : "";
                string profileOrReason = args.Length > 3 ? args[3] : "";
                string tag = args.Length > 4 ? args[4] : name;
                string group = args.Length > 5 ? args[5] : ("Sankalpa." + kind);
                int expirationSec = (args.Length > 6 && int.TryParse(args[6], out int exp)) ? exp : 0;
                Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CLI --notify received: kind='{kind}', name='{name}', reason='{profileOrReason}', tag='{tag}', group='{group}', exp={expirationSec}s");
                ShowToastNotification(kind, name, profileOrReason, tag, group, expirationSec);
                return 0;
            }

            if (string.Equals(args[0], "--retract", StringComparison.OrdinalIgnoreCase))
            {
                string tag = args.Length > 1 ? args[1] : "";
                string group = args.Length > 2 ? args[2] : "";
                Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CLI --retract received: tag='{tag}', group='{group}'");
                RetractToastNotification(tag, group);
                return 0;
            }

            if (string.Equals(args[0], "--register", StringComparison.OrdinalIgnoreCase))
            {
                var result = ExtensionHelper.RegisterHost();
                Console.WriteLine(result.FormatSummary());
                return result.Success ? 0 : 1;
            }

            if (string.Equals(args[0], "--unregister", StringComparison.OrdinalIgnoreCase))
            {
                ExtensionHelper.UnregisterHost();
                Console.WriteLine("Native messaging host unregistered.");
                return 0;
            }
        }

        Log("==========================================");
        Log($"Native host starting up (PID: {Environment.ProcessId})");

        try
        {
            // 3. Parent process identification
            string parentExe = "chrome.exe";
            var parentInfo = ProcessTreeHelper.GetParentProcessInfo(Environment.ProcessId);
            if (parentInfo.HasValue && !string.IsNullOrWhiteSpace(parentInfo.Value.ParentExeName))
            {
                parentExe = parentInfo.Value.ParentExeName;
                Log($"Identified parent process: '{parentExe}' (PID: {parentInfo.Value.ParentPid})");
            }
            else
            {
                Log("Could not determine parent process name; defaulting to 'chrome.exe'");
            }

            // 4. Stdio Loop
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            var pipeClient = new PipeClient(timeoutMs: 800);
            string lastCachedPayload = "{\"ok\":true,\"rulesVersion\":1,\"activeProfiles\":[],\"allProfiles\":[],\"cached\":true,\"serviceConnected\":false}";

            while (true)
            {
                string? requestJson;
                try
                {
                    requestJson = NativeMessageFraming.ReadMessage(stdin);
                }
                catch (EndOfStreamException)
                {
                    Log("Stdin stream reached EOF. Browser closed or host disconnected.");
                    break;
                }
                catch (IOException ioEx)
                {
                    Log($"Stdin pipe disconnected: {ioEx.Message}. Browser closed.");
                    break;
                }
                catch (InvalidDataException idEx)
                {
                    Log($"Stdin framing invalid or truncated: {idEx.Message}. Browser closed.");
                    break;
                }

                if (requestJson == null)
                {
                    Log("Stdin closed cleanly. Exiting.");
                    break;
                }

                string msgType = "heartbeat";
                bool incognitoAllowed = true;
                string extensionVersion = "1.0.0";
                string domainToAdd = "";
                string targetProfileId = "";
                string targetProfileName = "";

                try
                {
                    if (!string.IsNullOrWhiteSpace(requestJson))
                    {
                        using var doc = JsonDocument.Parse(requestJson);
                        if (doc.RootElement.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
                        {
                            msgType = typeProp.GetString() ?? "heartbeat";
                        }

                        if (doc.RootElement.TryGetProperty("incognitoAllowed", out var incProp) &&
                            (incProp.ValueKind == JsonValueKind.True || incProp.ValueKind == JsonValueKind.False))
                        {
                            incognitoAllowed = incProp.GetBoolean();
                        }

                        if (doc.RootElement.TryGetProperty("extensionVersion", out var verProp) &&
                            verProp.ValueKind == JsonValueKind.String)
                        {
                            extensionVersion = verProp.GetString() ?? "1.0.0";
                        }

                        if (doc.RootElement.TryGetProperty("domain", out var dProp) && dProp.ValueKind == JsonValueKind.String)
                        {
                            domainToAdd = dProp.GetString() ?? "";
                        }

                        if (doc.RootElement.TryGetProperty("profileId", out var pidProp))
                        {
                            targetProfileId = pidProp.ToString();
                        }

                        if (doc.RootElement.TryGetProperty("profileName", out var pnameProp) && pnameProp.ValueKind == JsonValueKind.String)
                        {
                            targetProfileName = pnameProp.GetString() ?? "";
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Warning: could not parse request JSON: {ex.Message}");
                }

                // Handle open_app request from extension popup header
                if (string.Equals(msgType, "open_app", StringComparison.OrdinalIgnoreCase))
                {
                    HandleOpenApp();
                    NativeMessageFraming.WriteMessage(stdout, "{\"type\":\"open_app_response\",\"ok\":true}");
                    continue;
                }

                // Handle add_website request from extension popup
                if (string.Equals(msgType, "add_website", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[NativeHost] Processing add_website: domain='{domainToAdd}', id='{targetProfileId}', name='{targetProfileName}'");
                    var addReq = new Request
                    {
                        Command = CommandNames.AddWebsite,
                        Args = new Dictionary<string, string>
                        {
                            ["domain"] = domainToAdd,
                            ["profile_id"] = targetProfileId,
                            ["profile_name"] = targetProfileName
                        }
                    };

                    try
                    {
                        var addRes = pipeClient.SendRequestAsync(addReq).GetAwaiter().GetResult();
                        Log($"[NativeHost] AddWebsite pipe result: Connected={addRes.Connected}, Ok={addRes.Response?.Ok}, Error='{addRes.Response?.Error}'");
                        string addReply = addRes.Connected && addRes.Response != null
                            ? JsonSerializer.Serialize(new
                              {
                                  type = "add_website_response",
                                  ok = addRes.Response.Ok,
                                  error = addRes.Response.Error,
                                  data = addRes.Response.Data
                              })
                            : JsonSerializer.Serialize(new
                              {
                                  type = "add_website_response",
                                  ok = false,
                                  error = "Cannot connect to Sankalpa service."
                              });
                        NativeMessageFraming.WriteMessage(stdout, addReply);
                    }
                    catch (Exception ex)
                    {
                        Log($"[NativeHost] AddWebsite exception: {ex.Message}");
                        NativeMessageFraming.WriteMessage(stdout, JsonSerializer.Serialize(new
                        {
                            type = "add_website_response",
                            ok = false,
                            error = ex.Message
                        }));
                    }
                    continue;
                }

                // Handle create_profile request from extension popup
                if (string.Equals(msgType, "create_profile", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[NativeHost] Processing create_profile: name='{targetProfileName}'");
                    var createReq = new Request
                    {
                        Command = CommandNames.SaveProfile,
                        Args = new Dictionary<string, string>
                        {
                            ["name"] = targetProfileName,
                            ["enabled"] = "true"
                        }
                    };

                    try
                    {
                        var createRes = pipeClient.SendRequestAsync(createReq).GetAwaiter().GetResult();
                        Log($"[NativeHost] SaveProfile pipe result: Connected={createRes.Connected}, Ok={createRes.Response?.Ok}, Error='{createRes.Response?.Error}'");
                        string createReply = createRes.Connected && createRes.Response != null
                            ? JsonSerializer.Serialize(new
                              {
                                  type = "create_profile_response",
                                  ok = createRes.Response.Ok,
                                  error = createRes.Response.Error,
                                  data = createRes.Response.Data
                              })
                            : JsonSerializer.Serialize(new
                              {
                                  type = "create_profile_response",
                                  ok = false,
                                  error = "Cannot connect to Sankalpa service."
                              });
                        NativeMessageFraming.WriteMessage(stdout, createReply);
                    }
                    catch (Exception ex)
                    {
                        Log($"[NativeHost] CreateProfile exception: {ex.Message}");
                        NativeMessageFraming.WriteMessage(stdout, JsonSerializer.Serialize(new
                        {
                            type = "create_profile_response",
                            ok = false,
                            error = ex.Message
                        }));
                    }
                    continue;
                }

                var pipeRequest = new Request
                {
                    Command = CommandNames.ExtHeartbeat,
                    Args = new Dictionary<string, string>
                    {
                        ["browserProcess"] = parentExe,
                        ["incognitoAllowed"] = incognitoAllowed ? "true" : "false",
                        ["extensionVersion"] = extensionVersion
                    }
                };

                PipeResult pipeResult;
                try
                {
                    pipeResult = pipeClient.SendRequestAsync(pipeRequest).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Log($"Error communicating with service: {ex.Message}");
                    pipeResult = PipeResult.NotConnected(ex.Message);
                }

                string replyPayload;
                bool isCached;
                if (pipeResult.Connected && pipeResult.Response != null && pipeResult.Response.Ok && !string.IsNullOrEmpty(pipeResult.Response.Data))
                {
                    replyPayload = pipeResult.Response.Data;
                    lastCachedPayload = replyPayload;
                    isCached = false;
                }
                else
                {
                    replyPayload = lastCachedPayload;
                    isCached = true;
                }

                // Inject extension theme setting from ui-settings.json and cached/serviceConnected status
                string? theme = null;
                try
                {
                    var uiSettings = UiSettingsStore.Load();
                    theme = UiSettingsStore.NormalizeTheme(uiSettings.Theme);
                }
                catch { }

                replyPayload = FormatHeartbeatReply(replyPayload, isCached, theme);

                try
                {
                    NativeMessageFraming.WriteMessage(stdout, replyPayload);
                }
                catch (Exception ex)
                {
                    Log($"Error writing response to stdout: {ex.Message}");
                    break;
                }
            }

            Log("Native host exiting cleanly.");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FATAL: Unhandled exception: {ex}");
            return 1;
        }
    }

    private static void HandleOpenApp()
    {
        Log("[NativeHost] HandleOpenApp requested");
        try
        {
            // 1. Signal the app's registered ShowWindowEvent (brings existing instance to front)
            try
            {
                if (EventWaitHandle.TryOpenExisting(@"Global\SankalpaUiShowWindowEvent", out var showEvent))
                {
                    showEvent.Set();
                    showEvent.Dispose();
                    Log("[NativeHost] Signaled Global\\SankalpaUiShowWindowEvent successfully.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log($"[NativeHost] TryOpenExisting ShowWindowEvent failed: {ex.Message}");
            }

            // 2. If event not found, check if process exists and try window restore
            var existing = Process.GetProcessesByName("Sankalpa")
                .Concat(Process.GetProcessesByName("Blocker.App"))
                .FirstOrDefault();

            if (existing != null && existing.MainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(existing.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(existing.MainWindowHandle);
                Log($"[NativeHost] Restored existing window handle {existing.MainWindowHandle}");
                return;
            }

            // 3. Otherwise launch Sankalpa executable
            string[] candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Sankalpa.exe"),
                Path.Combine(AppContext.BaseDirectory, "Blocker.App.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "Sankalpa-Release", "Sankalpa.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "Blocker.App", "bin", "Debug", "net8.0-windows", "Blocker.App.exe")
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c))
                {
                    Log($"[NativeHost] Starting process: {c}");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = c,
                        WorkingDirectory = Path.GetDirectoryName(c) ?? AppContext.BaseDirectory,
                        UseShellExecute = true
                    });
                    return;
                }
            }

            Log("[NativeHost] No Sankalpa executable found to launch.");
        }
        catch (Exception ex)
        {
            Log($"[NativeHost] Error in HandleOpenApp: {ex.Message}");
        }
    }

    private static void InitializeLogging()
    {
        try
        {
            if (!string.IsNullOrEmpty(_logFilePath)) return;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string logDir = Path.Combine(localAppData, "Blocker", "logs");
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, "native-host.log");

            // Roll log file if > 5 MB
            if (File.Exists(_logFilePath))
            {
                var fi = new FileInfo(_logFilePath);
                if (fi.Length > 5 * 1024 * 1024)
                {
                    string oldLog = Path.Combine(logDir, "native-host.old.log");
                    try
                    {
                        File.Delete(oldLog);
                        File.Move(_logFilePath, oldLog);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static void ShowToastNotification(string kind, string name, string profileOrReason, string tag = "", string group = "", int expirationSec = 0)
    {
        try
        {
            Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ShowToastNotification invoked: kind='{kind}', name='{name}', profileOrReason='{profileOrReason}', tag='{tag}', group='{group}', exp={expirationSec}s");

            // Pre-show USABLE window verification for browser notifications
            if (string.Equals(kind, "BrowserGrace", StringComparison.OrdinalIgnoreCase))
            {
                if (!WindowsNotificationHelper.HasUsableWindow(name))
                {
                    Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Pre-show check suppressed toast for '{name}': no USABLE window.");
                    RetractToastNotification(tag, group);
                    return;
                }
            }

            // 1. Ensure HKCU\Software\Classes\AppUserModelId\Sankalpa is verified and registered before calling ToastNotificationManager
            string appIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (!File.Exists(appIconPath))
            {
                appIconPath = Path.Combine(AppContext.BaseDirectory, "Sankalpa.exe");
            }

            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\Sankalpa");
                if (key != null)
                {
                    key.SetValue("DisplayName", "Sankalpa", Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("ShowInSettings", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    if (File.Exists(appIconPath))
                    {
                        key.SetValue("IconUri", appIconPath, Microsoft.Win32.RegistryValueKind.String);
                    }
                    key.SetValue("IconBackgroundColor", "0", Microsoft.Win32.RegistryValueKind.String);
                    Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Verified and registered HKCU\\Software\\Classes\\AppUserModelId\\Sankalpa in registry.");
                }
            }
            catch (Exception regEx)
            {
                Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Notice: Direct HKCU AUMID registration check: {regEx.Message}");
            }

            try
            {
                ExtensionHelper.EnsureAumidRegistered(appIconPath);
            }
            catch (Exception aumidEx)
            {
                Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Notice: ExtensionHelper.EnsureAumidRegistered: {aumidEx.Message}");
            }

            // 2. Build Toast XML with appropriate title, message, and icon
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
            else if (string.Equals(kind, "App", StringComparison.OrdinalIgnoreCase))
            {
                title = $"🚫 {name} Blocked";
                line1 = $"Blocked by profile '{profileOrReason}'.";
            }
            else
            {
                // General or custom notification (delivered regardless of active block state)
                title = string.IsNullOrWhiteSpace(name) ? "Sankalpa Notification" : name;
                line1 = string.IsNullOrWhiteSpace(profileOrReason) ? kind : profileOrReason;
            }

            // Find visual icon asset if available
            string? logoPngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (!File.Exists(logoPngPath))
            {
                logoPngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo-32.png");
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("<toast duration=\"short\"><visual><binding template=\"ToastGeneric\">");
            if (File.Exists(logoPngPath))
            {
                sb.Append("<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"")
                  .Append(System.Security.SecurityElement.Escape(Path.GetFullPath(logoPngPath)))
                  .Append("\" />");
            }
            sb.Append("<text>").Append(System.Security.SecurityElement.Escape(title)).Append("</text>");
            sb.Append("<text>").Append(System.Security.SecurityElement.Escape(line1)).Append("</text>");
            if (!string.IsNullOrEmpty(line2))
            {
                sb.Append("<text>").Append(System.Security.SecurityElement.Escape(line2)).Append("</text>");
            }
            sb.Append("</binding></visual><audio src=\"ms-winsoundevent:Notification.Default\" /></toast>");

            string toastXml = sb.ToString();
            Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Prepared Toast XML: {toastXml}");

            var doc = new Windows.Data.Xml.Dom.XmlDocument();
            doc.LoadXml(toastXml);
            var toast = new Windows.UI.Notifications.ToastNotification(doc);
            if (!string.IsNullOrEmpty(tag)) toast.Tag = tag;
            if (!string.IsNullOrEmpty(group)) toast.Group = group;
            if (expirationSec > 0)
            {
                toast.ExpirationTime = DateTimeOffset.UtcNow.AddSeconds(expirationSec);
            }

            bool shown = false;
            try
            {
                // 3. Call ToastNotificationManager.CreateToastNotifier("Sankalpa").Show(toast)
                var notifier = Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier("Sankalpa");
                notifier.Show(toast);
                shown = true;
                Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Successfully invoked WinRT ToastNotifier.Show() for '{title}' (Tag='{tag}', Group='{group}').");

                // CRITICAL: WinRT ToastNotifier.Show() is an asynchronous COM RPC call.
                // Wait 1200ms after Show() to ensure Windows Action Center finishes receiving and rendering the toast!
                System.Threading.Thread.Sleep(1200);
                Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] WinRT toast display wait completed (1200ms).");
            }
            catch (Exception ex)
            {
                Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] WinRT ToastNotificationManager failed: {ex.Message}");
            }

            if (!shown)
            {
                Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Falling back to PowerShell notification toast.");
                ShowFallbackNotification(title, line1, line2, tag, group);
            }
        }
        catch (Exception ex)
        {
            Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Failed to show toast notification: {ex}");
        }
    }

    private static void RetractToastNotification(string tag, string group)
    {
        try
        {
            var history = Windows.UI.Notifications.ToastNotificationManager.History;
            if (!string.IsNullOrEmpty(tag) && !string.IsNullOrEmpty(group))
            {
                history.Remove(tag, group, "Sankalpa");
                Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Successfully retracted toast for tag='{tag}', group='{group}'.");
            }
            else if (!string.IsNullOrEmpty(group))
            {
                history.RemoveGroup(group, "Sankalpa");
                Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Successfully retracted toast group='{group}'.");
            }
        }
        catch (Exception ex)
        {
            Log($"[ZenNotify] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] RetractToastNotification error: {ex.Message}");
        }
    }

    private static void ShowFallbackNotification(string title, string line1, string? line2, string tag = "", string group = "")
    {
        try
        {
            string fullBody = string.IsNullOrEmpty(line2) ? line1 : $"{line1} {line2}";
            string escTitle = System.Security.SecurityElement.Escape(title) ?? title;
            string escBody = System.Security.SecurityElement.Escape(fullBody) ?? fullBody;

            string psScript = 
                "try { " +
                "New-Item -Path 'HKCU:\\Software\\Classes\\AppUserModelId\\Sankalpa' -Force -ErrorAction SilentlyContinue | Out-Null; " +
                "Set-ItemProperty -Path 'HKCU:\\Software\\Classes\\AppUserModelId\\Sankalpa' -Name 'DisplayName' -Value 'Sankalpa' -Force -ErrorAction SilentlyContinue; " +
                "Set-ItemProperty -Path 'HKCU:\\Software\\Classes\\AppUserModelId\\Sankalpa' -Name 'ShowInSettings' -Value 1 -Force -ErrorAction SilentlyContinue; " +
                "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; " +
                "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null; " +
                $"$rawXml = @'\n<toast duration=\"short\"><visual><binding template=\"ToastGeneric\"><text>{escTitle}</text><text>{escBody}</text></binding></visual></toast>\n'@; " +
                "$doc = [Windows.Data.Xml.Dom.XmlDocument]::new(); " +
                "$doc.LoadXml($rawXml); " +
                "$t = [Windows.UI.Notifications.ToastNotification]::new($doc); " +
                (string.IsNullOrEmpty(tag) ? "" : $"$t.Tag = '{tag.Replace("'", "''")}'; ") +
                (string.IsNullOrEmpty(group) ? "" : $"$t.Group = '{group.Replace("'", "''")}'; ") +
                "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Sankalpa').Show($t); " +
                "Start-Sleep -Milliseconds 1200; " +
                "} catch { }";

            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(psScript);
            string b64 = Convert.ToBase64String(bytes);

            string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var psi = new ProcessStartInfo
            {
                FileName = pwsh,
                Arguments = $"-WindowStyle Hidden -NoProfile -NonInteractive -EncodedCommand {b64}",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(4000);
            Log("[NativeHost] Dispatched fallback PowerShell notification successfully.");
        }
        catch (Exception ex)
        {
            Log($"[NativeHost] ShowFallbackNotification error: {ex.Message}");
        }
    }

    public static string FormatHeartbeatReply(string basePayload, bool isCached, string? extensionTheme = null)
    {
        try
        {
            if (JsonNode.Parse(basePayload) is JsonObject obj)
            {
                obj["cached"] = isCached;
                obj["serviceConnected"] = !isCached;
                if (!string.IsNullOrEmpty(extensionTheme))
                {
                    obj["extensionTheme"] = extensionTheme;
                }
                return obj.ToJsonString();
            }
        }
        catch (Exception ex)
        {
            Log($"Error formatting heartbeat JSON via JsonNode: {ex.Message}");
        }

        // Fallback simple injection if JSON node parsing fails
        try
        {
            string trimmed = basePayload.Trim();
            if (trimmed.EndsWith("}"))
            {
                string extra = $",\"cached\":{(isCached ? "true" : "false")},\"serviceConnected\":{(isCached ? "false" : "true")}";
                if (!string.IsNullOrEmpty(extensionTheme))
                {
                    extra += $",\"extensionTheme\":\"{extensionTheme}\"";
                }
                return trimmed.Substring(0, trimmed.Length - 1) + extra + "}";
            }
        }
        catch { }

        return basePayload;
    }

    private static void Log(string message)
    {
        if (string.IsNullOrEmpty(_logFilePath)) return;

        try
        {
            lock (_logLock)
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
        }
        catch { }
    }
}
