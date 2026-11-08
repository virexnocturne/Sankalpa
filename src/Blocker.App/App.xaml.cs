using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Blocker.Core;
using Wpf = System.Windows;
using Forms = System.Windows.Forms;

namespace Blocker.App;

public partial class App : Wpf.Application
{
    private const string MutexName = @"Global\BlockerUiSingletonMutex";
    private const string ShowWindowEventName = @"Global\SankalpaUiShowWindowEvent";
    private Mutex? _mutex;
    private EventWaitHandle? _showWindowEvent;
    private bool _hasMutex;
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _masterNotifItem;
    private Forms.ToolStripMenuItem? _browserNotifItem;
    private Forms.ToolStripMenuItem? _appNotifItem;
    private Forms.ToolStripMenuItem? _dnsNotifItem;
    private bool _balloonShown;
    private bool _isExiting;

    protected override void OnStartup(Wpf.StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            try { System.IO.File.WriteAllText("crash.log", ev.ExceptionObject?.ToString() ?? "Unknown exception"); } catch { }
        };
        DispatcherUnhandledException += (s, ev) =>
        {
            try { System.IO.File.WriteAllText("crash.log", ev.Exception.ToString()); } catch { }
            if (MainWindow == null || !MainWindow.IsLoaded)
            {
                ev.Handled = false;
            }
            else
            {
                ev.Handled = true;
            }
        };

        // Hidden command-line switch for dev screenshot test
        if (e.Args.Any(a => string.Equals(a, "--render-test", StringComparison.OrdinalIgnoreCase)))
        {
            int exitCode = DevRenderTester.RunRenderTest(e.Args);
            Shutdown(exitCode);
            return;
        }

        EnsureRunAsInvokerPreference();
        ExtensionHelper.EnsureAumidRegistered();

        try
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (createdNew)
            {
                _hasMutex = true;
            }
            else
            {
                _hasMutex = _mutex.WaitOne(0, false);
            }
        }
        catch (AbandonedMutexException)
        {
            _hasMutex = true;
        }
        catch
        {
            _hasMutex = true;
        }

        if (!_hasMutex)
        {
            // Another instance of the UI is already running: signal it to restore its window
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var existingHandle))
                {
                    existingHandle.Set();
                    existingHandle.Dispose();
                }
            }
            catch { }

            Shutdown();
            return;
        }

        // Start listener thread for subsequent double-clicks/launches to bring window to front
        try
        {
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            var listenerThread = new Thread(() =>
            {
                while (_hasMutex)
                {
                    try
                    {
                        if (_showWindowEvent.WaitOne())
                        {
                            Dispatcher.Invoke(() => ShowMainWindow());
                        }
                    }
                    catch { break; }
                }
            })
            {
                IsBackground = true
            };
            listenerThread.Start();
        }
        catch { }

        base.OnStartup(e);

        ThemeManager.Initialize(Blocker.Core.UiSettingsStore.Load());

        InitializeTrayIcon();

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    public static void EnsureRunAsInvokerPreference()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var candidatePaths = new List<string>();
                if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
                {
                    candidatePaths.Add(Environment.ProcessPath);
                }
                string baseExe = System.IO.Path.Combine(AppContext.BaseDirectory, "Sankalpa.exe");
                if (!candidatePaths.Contains(baseExe, StringComparer.OrdinalIgnoreCase))
                {
                    candidatePaths.Add(baseExe);
                }

                // 1. Current user AppCompatFlags
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"))
                {
                    if (key != null)
                    {
                        foreach (var path in candidatePaths)
                        {
                            object? currentVal = key.GetValue(path);
                            string currentStr = currentVal?.ToString() ?? string.Empty;
                            if (currentStr.Contains("RUNASADMIN", StringComparison.OrdinalIgnoreCase))
                            {
                                currentStr = currentStr.Replace("RUNASADMIN", "").Trim();
                            }
                            if (!currentStr.Contains("RUNASINVOKER", StringComparison.OrdinalIgnoreCase))
                            {
                                key.SetValue(path, "~ RUNASINVOKER");
                            }
                        }
                    }
                }

                // 2. Machine-wide AppCompatFlags (if accessible)
                try
                {
                    using var hklmKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", writable: true);
                    if (hklmKey != null)
                    {
                        foreach (var path in candidatePaths)
                        {
                            hklmKey.SetValue(path, "~ RUNASINVOKER");
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private Forms.ToolStripMenuItem? _trayStatusItem;

    private void InitializeTrayIcon()
    {
        Icon? appIcon = null;
        try
        {
            string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(iconPath))
            {
                appIcon = new Icon(iconPath);
            }
            else
            {
                var iconUri = new Uri("pack://application:,,,/Assets/app.ico");
                var streamInfo = Wpf.Application.GetResourceStream(iconUri);
                if (streamInfo != null)
                {
                    appIcon = new Icon(streamInfo.Stream);
                }
            }
        }
        catch { }

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = appIcon ?? SystemIcons.Application,
            Text = "Sankalpa - Tamper-Proof Focus",
            Visible = true
        };

        var contextMenu = new Forms.ContextMenuStrip();

        // 1. Real-time Status Header
        _trayStatusItem = new Forms.ToolStripMenuItem("Status: Guarding (Active)")
        {
            Enabled = false,
            Font = new Font(contextMenu.Font, System.Drawing.FontStyle.Bold)
        };
        contextMenu.Items.Add(_trayStatusItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        // 2. Navigation items
        contextMenu.Items.Add("Open Dashboard", null, (s, e) => ShowMainWindow());
        contextMenu.Items.Add("Website & App Blocks...", null, (s, e) =>
        {
            ShowMainWindow();
            if (MainWindow is MainWindow mw)
            {
                if (mw.DataContext is MainViewModel vm) vm.IsSettingsViewActive = false;
            }
        });
        contextMenu.Items.Add("DNS Protection...", null, (s, e) =>
        {
            ShowMainWindow();
            if (MainWindow is MainWindow mw)
            {
                mw.OpenSettings();
            }
        });
        contextMenu.Items.Add("Browser Extension...", null, (s, e) =>
        {
            ShowMainWindow();
            if (MainWindow is MainWindow mw)
            {
                mw.OpenExtensionDialog();
            }
        });

        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        // 3. Notifications Submenu
        var currentSettings = UiSettingsStore.Load();
        AreNotificationsEnabled = currentSettings.AreNotificationsEnabled;
        ShowBrowserAlerts = currentSettings.ShowBrowserAlerts;
        ShowAppAlerts = currentSettings.ShowAppAlerts;
        ShowDnsAlerts = currentSettings.ShowDnsAlerts;

        var notifMenu = new Forms.ToolStripMenuItem("Notifications");
        _masterNotifItem = new Forms.ToolStripMenuItem("Enable All Notifications")
        {
            Checked = AreNotificationsEnabled,
            CheckOnClick = true
        };
        _masterNotifItem.Click += (s, e) =>
        {
            AreNotificationsEnabled = _masterNotifItem.Checked;
            SaveNotificationSettings();
            NotifyMainWindowOfSettingsChange();
        };
        notifMenu.DropDownItems.Add(_masterNotifItem);

        _browserNotifItem = new Forms.ToolStripMenuItem("Browser Security Warnings")
        {
            Checked = ShowBrowserAlerts,
            CheckOnClick = true
        };
        _browserNotifItem.Click += (s, e) =>
        {
            ShowBrowserAlerts = _browserNotifItem.Checked;
            SaveNotificationSettings();
            NotifyMainWindowOfSettingsChange();
        };
        notifMenu.DropDownItems.Add(_browserNotifItem);

        _appNotifItem = new Forms.ToolStripMenuItem("App Block Notifications")
        {
            Checked = ShowAppAlerts,
            CheckOnClick = true
        };
        _appNotifItem.Click += (s, e) =>
        {
            ShowAppAlerts = _appNotifItem.Checked;
            SaveNotificationSettings();
            NotifyMainWindowOfSettingsChange();
        };
        notifMenu.DropDownItems.Add(_appNotifItem);

        _dnsNotifItem = new Forms.ToolStripMenuItem("DNS Security Alerts")
        {
            Checked = ShowDnsAlerts,
            CheckOnClick = true
        };
        _dnsNotifItem.Click += (s, e) =>
        {
            ShowDnsAlerts = _dnsNotifItem.Checked;
            SaveNotificationSettings();
            NotifyMainWindowOfSettingsChange();
        };
        notifMenu.DropDownItems.Add(_dnsNotifItem);

        contextMenu.Items.Add(notifMenu);

        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add("Exit Sankalpa", null, (s, e) => ExitApplication());

        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();
    }

    public void UpdateTrayStatus(string statusText)
    {
        if (_trayStatusItem != null)
        {
            _trayStatusItem.Text = statusText;
        }
        if (_notifyIcon != null && !string.IsNullOrWhiteSpace(statusText))
        {
            string shortText = statusText.Length > 63 ? statusText.Substring(0, 60) + "..." : statusText;
            _notifyIcon.Text = shortText;
        }
    }

    public bool AreNotificationsEnabled { get; set; } = true;
    public bool ShowBrowserAlerts { get; set; } = true;
    public bool ShowAppAlerts { get; set; } = true;
    public bool ShowDnsAlerts { get; set; } = true;

    private void NotifyMainWindowOfSettingsChange()
    {
        if (MainWindow is MainWindow mw && mw.DataContext is MainViewModel vm)
        {
            vm.SyncNotificationSettingsFromStore();
        }
    }

    public void SyncNotificationTray(bool master, bool browser, bool app, bool dns)
    {
        AreNotificationsEnabled = master;
        ShowBrowserAlerts = browser;
        ShowAppAlerts = app;
        ShowDnsAlerts = dns;
        if (_masterNotifItem != null) _masterNotifItem.Checked = master;
        if (_browserNotifItem != null) _browserNotifItem.Checked = browser;
        if (_appNotifItem != null) _appNotifItem.Checked = app;
        if (_dnsNotifItem != null) _dnsNotifItem.Checked = dns;
    }

    private void SaveNotificationSettings()
    {
        try
        {
            var settings = UiSettingsStore.Load();
            settings.AreNotificationsEnabled = AreNotificationsEnabled;
            settings.ShowBrowserAlerts = ShowBrowserAlerts;
            settings.ShowAppAlerts = ShowAppAlerts;
            settings.ShowDnsAlerts = ShowDnsAlerts;
            UiSettingsStore.Save(settings);
        }
        catch { }
    }

    public void ShowBlockNotification(string appName, string profileName, string kind = "App")
    {
        if (!AreNotificationsEnabled) return;

        if (string.Equals(kind, "BrowserKill", StringComparison.OrdinalIgnoreCase))
        {
            if (!ShowBrowserAlerts) return;
            string disp = BrowserCatalog.GetDisplayName(appName);
            _notifyIcon?.ShowBalloonTip(
                6000,
                $"{disp} Terminated",
                $"Reason: {profileName}\nAction: Install Sankalpa extension & enable 'Allow in InPrivate'.",
                Forms.ToolTipIcon.Warning);
        }
        else if (string.Equals(kind, "BrowserGrace", StringComparison.OrdinalIgnoreCase))
        {
            if (!ShowBrowserAlerts) return;
            string disp = BrowserCatalog.GetDisplayName(appName);
            _notifyIcon?.ShowBalloonTip(
                4000,
                $"{disp} Closing Soon",
                profileName,
                Forms.ToolTipIcon.Warning);
        }
        else if (string.Equals(kind, "DnsTamper", StringComparison.OrdinalIgnoreCase))
        {
            if (!ShowDnsAlerts) return;
            _notifyIcon?.ShowBalloonTip(
                5000,
                "DNS Shield Restored",
                $"DNS tampering was detected and reverted to {profileName}.",
                Forms.ToolTipIcon.Info);
        }
        else
        {
            if (!ShowAppAlerts) return;
            _notifyIcon?.ShowBalloonTip(
                3000,
                $"{appName} Blocked",
                $"Blocked by profile '{profileName}'.",
                Forms.ToolTipIcon.Info);
        }
    }

    public void ShowMainWindow()
    {
        if (MainWindow != null)
        {
            MainWindow.Show();
            if (MainWindow.WindowState == Wpf.WindowState.Minimized)
            {
                MainWindow.WindowState = Wpf.WindowState.Normal;
            }
            MainWindow.Activate();
            MainWindow.Topmost = true;
            MainWindow.Topmost = false;
            MainWindow.Focus();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize", SetLastError = true)]
    private static extern int SetProcessWorkingSetSize(IntPtr process, int min, int max);

    public static void TrimWorkingSet()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
            }
        }
        catch { }
    }

    public void OnWindowClosing(CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            MainWindow?.Hide();

            // Reclaim unneeded WPF DirectX surfaces & cache memory while hidden in tray
            TrimWorkingSet();

            if (!_balloonShown && _notifyIcon != null)
            {
                _notifyIcon.ShowBalloonTip(
                    3000,
                    "Sankalpa",
                    "Sankalpa is minimized to system tray. Double-click tray icon to open.",
                    Forms.ToolTipIcon.Info);
                _balloonShown = true;
            }
        }
    }

    public void ExitApplication()
    {
        _isExiting = true;
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        if (_showWindowEvent != null)
        {
            try { _showWindowEvent.Dispose(); } catch { }
            _showWindowEvent = null;
        }
        Shutdown();
    }

    protected override void OnExit(Wpf.ExitEventArgs e)
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        if (_showWindowEvent != null)
        {
            try { _showWindowEvent.Dispose(); } catch { }
            _showWindowEvent = null;
        }

        if (_mutex != null)
        {
            if (_hasMutex)
            {
                try { _mutex.ReleaseMutex(); } catch { }
            }
            _mutex.Dispose();
            _mutex = null;
        }

        base.OnExit(e);
    }
}
