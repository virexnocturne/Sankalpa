using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Blocker.Core;

namespace Blocker.Service;

public class Worker : BackgroundService
{
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, [Out] StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    private static readonly int CurrentProcessId = Environment.ProcessId;
    private static readonly int CurrentProcessSessionId = GetCurrentProcessSessionId();
    private static readonly string CurrentProcessName = GetCurrentProcessName();

    private static int GetCurrentProcessSessionId()
    {
        try
        {
            return Process.GetCurrentProcess().SessionId;
        }
        catch
        {
            return 0;
        }
    }

    private static string GetCurrentProcessName()
    {
        try
        {
            return Process.GetCurrentProcess().ProcessName;
        }
        catch
        {
            return "blocker.service";
        }
    }

    private const int SW_MINIMIZE = 6;
    private const int SW_FORCEMINIMIZE = 11;
    private const uint WM_CLOSE = 0x0010;

    private readonly ILogger<Worker> _logger;
    private readonly BlockerDatabase _db;
    private readonly HostsFileManager _hostsManager;

    private class TrackedSession
    {
        public BlockSession Session { get; set; } = null!;
        public Profile? Profile { get; set; }
        public TimeSpan InitialRemaining { get; set; }
        public Stopwatch Stopwatch { get; set; } = null!;
        public DateTime BaseSystemTime { get; set; }
    }

    private readonly ConcurrentDictionary<int, TrackedSession> _trackedSessions = new();

    private MeditationSession? _activeMeditationSession;
    private readonly Dictionary<int, DateTime> _scheduleWarningNotifiedUtc = new();
    private readonly Dictionary<int, DateTime> _scheduleTriggeredUtc = new();
    private readonly HashSet<int> _loggedMeditationPids = new();
    private readonly HashSet<IntPtr> _loggedCabinetHwnds = new();
    private DateTime _pendingMeditationDeadline = DateTime.MinValue;


    private readonly ConcurrentDictionary<string, string?> _originalFileNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, string?> _pidOriginalFileNameCache = new();
    private readonly HashSet<int> _protectedPids = new();
    private readonly HashSet<int> _loggedKillPids = new();
    private readonly HashSet<int> _loggedFailurePids = new();
    private readonly HashSet<string> _loggedGraceBrowsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FastProcessEntry> _processBuffer = new(512);
    private readonly HashSet<string> _runningProcessNames = new(512, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string ProcessName, string ExeName)> _processNameCache = new(512, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, string?> _processPathCache = new();
    private readonly Dictionary<int, FastProcessEntry> _processByPid = new(512);
    private readonly MeditationAllowedCache _meditationAllowedCache = new();
    private DateTime _lastDnsWatchdogApplyUtc = DateTime.MinValue;

    public Worker(ILogger<Worker> logger, IBlockStore? db = null)
    {
        _logger = logger;
        _db = (db as BlockerDatabase) ?? new BlockerDatabase();
        _hostsManager = new HostsFileManager(logger: _logger);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string startupMsg = $"[{timestamp}] Blocker Service starting up...";
        Console.WriteLine(startupMsg);
        _logger.LogInformation("{Message}", startupMsg);

        // Discover and protect self & parent/ancestor processes (terminal/shell)
        RefreshProtectedPids();

        // Check process elevation and initialize hosts state
        CheckElevationAndInitializeHostsState();

        // Recover any background apps throttled prior to crash/restart
        BackgroundAppThrottler.RecoverFromJournal();

        // Restore active session if one was running prior to service shutdown/restart
        RestoreActiveSessionOnStartup();

        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _logger.LogInformation("[{Timestamp}] Blocker Service stopping. Checking active lock state...", timestamp);

        try
        {
            DateTime localNow = DateTime.Now;
            var allProfiles = _db.ListProfiles();
            bool anySessionLocked = _trackedSessions.Values.Any(t => t.Session.Locked && t.Session.IsActive(DateTime.UtcNow));
            bool anyScheduleLocked = allProfiles.Any(p => p.Schedule != null && p.Schedule.Count > 0 && ScheduleLogic.IsActive(p.Schedule, localNow));
            bool emergencyPresent = _db.IsEmergencyUnlockPresent();

            // Meditation mode is a focus mode, not a kernel lockdown. It never blocks service teardown or DACL restoration.
            bool isLockedActive = (anySessionLocked || anyScheduleLocked) && !emergencyPresent;

            // Unconditionally resume any suspended processes during service shutdown
            ProcessSuspender.Default.ResumeAll();
            BackgroundAppThrottler.Default.RestoreAll();

            if (isLockedActive)
            {
                _logger.LogWarning("[SECURITY] Blocker Service stopping while locked session/schedule is active. Preserving hosts file and security descriptors.");
            }
            else
            {
                _hostsManager.RemoveBlock();
                SecurityHardener.UnlockService(out _);
                SecurityHardener.UnlockCurrentProcess(out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing StopAsync teardown in Worker.");
        }

        await base.StopAsync(cancellationToken);
    }

    private void RefreshProtectedPids()
    {
        _protectedPids.Add(CurrentProcessId);
        try
        {
            var pids = ProcessTreeHelper.GetCurrentAndAncestorProcessIds();
            foreach (int pid in pids)
            {
                _protectedPids.Add(pid);
            }
        }
        catch
        {
            _protectedPids.Add(CurrentProcessId);
        }
    }

    private void RestoreActiveSessionOnStartup()
    {
        try
        {
            var sessions = _db.GetActiveSessions(includeUnendedExpired: true);
            if (sessions.Count > 0)
            {
                DateTime nowUtc = DateTime.UtcNow;
                foreach (var session in sessions)
                {
                    double remainingSeconds;

                    if (BlockSessionLogic.HasClockMovedBackwards(session, nowUtc))
                    {
                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        string warnMsg = $"[{timestamp}] [WARNING] System clock moved backwards into the past (StartUtc: {session.StartUtc:O}, NowUtc: {nowUtc:O}). Clock manipulation detected.";
                        Console.WriteLine(warnMsg);
                        _logger.LogWarning("{Message}", warnMsg);

                        session.StartUtc = nowUtc;
                        _db.UpdateSessionStartUtc(session.Id, nowUtc);
                        remainingSeconds = session.DurationSeconds;
                    }
                    else
                    {
                        TimeSpan elapsedSinceStart = nowUtc - session.StartUtc;
                        remainingSeconds = session.DurationSeconds - elapsedSinceStart.TotalSeconds;
                    }

                    if (remainingSeconds <= 0)
                    {
                        if (nowUtc >= session.StartUtc.AddSeconds(session.DurationSeconds) && nowUtc >= session.StartUtc)
                        {
                            _db.EndSession(session.Id);

                            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                            string msg = session.Locked
                                ? $"[{timestamp}] Previous locked session for Profile {session.ProfileId} completed while the machine was powered down."
                                : $"[{timestamp}] Previous session for Profile {session.ProfileId} expired during downtime.";
                            Console.WriteLine(msg);
                            _logger.LogInformation("{Message}", msg);
                        }
                    }
                    else
                    {
                        var prof = _db.GetProfileById(session.ProfileId);
                        _trackedSessions[session.Id] = new TrackedSession
                        {
                            Session = session,
                            Profile = prof,
                            InitialRemaining = TimeSpan.FromSeconds(remainingSeconds),
                            Stopwatch = Stopwatch.StartNew(),
                            BaseSystemTime = DateTime.UtcNow
                        };

                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        string msg = $"[{timestamp}] Restored active session on startup: Profile='{prof?.Name}', Remaining={remainingSeconds:F0}s (Locked: {session.Locked}).";
                        Console.WriteLine(msg);
                        _logger.LogInformation("{Message}", msg);
                    }
                }

            }
            else
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string msg = $"[{timestamp}] No active manual block session found on startup.";
                Console.WriteLine(msg);
                _logger.LogInformation("{Message}", msg);
            }

            // Check active meditation session on startup / recovery
            var activeMeditation = _db.GetActiveMeditationSession();
            if (activeMeditation != null)
            {
                var remainingMeditationSeconds = (activeMeditation.StartUtc.AddSeconds(activeMeditation.TargetDurationSeconds) - DateTime.UtcNow).TotalSeconds;
                if (remainingMeditationSeconds > 0)
                {
                    _activeMeditationSession = activeMeditation;
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    string msg = $"[{timestamp}] Restored active meditation session on startup: Id={activeMeditation.Id}, Remaining={remainingMeditationSeconds:F0}s, LockMode={activeMeditation.LockMode}.";
                    Console.WriteLine(msg);
                    _logger.LogInformation("{Message}", msg);
                }
                else
                {
                    _db.EndMeditationSession(activeMeditation.Id, completed: true);
                    _activeMeditationSession = null;
                    _loggedMeditationPids.Clear();
                    ProcessSuspender.Default.ResumeAll();
                BackgroundAppThrottler.Default.RestoreAll();
                    // Scheduled Blocks Integrity: Ensure meditation session cleanup does NOT touch, disable, or alter standard profile schedules.
                }
            }

            // Enforce active union (scheduled profiles + restored manual session) on startup
            DateTime localNow = DateTime.Now;
            var allProfiles = _db.ListProfiles();
            bool anySessionLocked = _trackedSessions.Values.Any(t => t.Session.Locked);
            bool anyScheduleLocked = allProfiles.Any(p => p.Schedule != null && p.Schedule.Count > 0 && ScheduleLogic.IsActive(p.Schedule, localNow));
            bool anyHardLocked = anySessionLocked || anyScheduleLocked;
            // Meditation Mode does not trigger kernel process/service DACL lockdown.

            if (anyHardLocked)
            {
                if (!SecurityHardener.IsLockedDown)
                {
                    if (SecurityHardener.LockdownCurrentProcess(out var procErr))
                    {
                        string procMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SECURITY] Process DACL locked down. Terminate access denied.";
                        Console.WriteLine(procMsg);
                        _logger.LogInformation("{Message}", procMsg);
                    }
                    else
                    {
                        _logger.LogWarning("[SECURITY] Failed to lock down process DACL: {Error}", procErr);
                    }
                }

                if (!SecurityHardener.IsServiceLockedDown)
                {
                    if (SecurityHardener.LockdownService(out var svcErr))
                    {
                        string svcMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SECURITY] Service DACL locked down. Stop access denied.";
                        Console.WriteLine(svcMsg);
                        _logger.LogInformation("{Message}", svcMsg);
                    }
                    else
                    {
                        _logger.LogWarning("[SECURITY] Failed to lock down service DACL: {Error}", svcErr);
                    }
                }
            }
            else
            {
                if (SecurityHardener.IsLockedDown)
                {
                    SecurityHardener.UnlockCurrentProcess(out _);
                    string procMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SECURITY] Process DACL restored.";
                    Console.WriteLine(procMsg);
                    _logger.LogInformation("{Message}", procMsg);
                }
                if (SecurityHardener.IsServiceLockedDown)
                {
                    SecurityHardener.UnlockService(out _);
                    string svcMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SECURITY] Service DACL restored.";
                    Console.WriteLine(svcMsg);
                    _logger.LogInformation("{Message}", svcMsg);
                }
            }
            var startupSessions = _trackedSessions.Values.Select(t => t.Session).ToList();
            var activeProfiles = allProfiles.Where(p => ProfileStateLogic.IsProfileActiveWithSessions(p, startupSessions, localNow, out _)).ToList();

            var desiredDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in activeProfiles)
            {
                if (p.SystemWide && p.BlockedDomains != null)
                {
                    foreach (var d in p.BlockedDomains)
                    {
                        desiredDomains.Add(d);
                    }
                }
            }

            var startupProcesses = FastProcessEnumerator.GetProcesses();
            var runningBrowserExes = BrowserCatalog.FilterAllBrowserProcesses(startupProcesses.Select(p => p.ProcessName));

            var fallbackPlan = _db.FallbackPlanner.Plan(activeProfiles, runningBrowserExes, DateTime.UtcNow);
            _db.LastFallbackPlan = fallbackPlan;
            foreach (var d in fallbackPlan.FallbackDomains)
            {
                desiredDomains.Add(d);
            }

            _hostsManager.SyncHosts(desiredDomains);
            _db.HostsState = _hostsManager.State;
            _db.HostsDomainCount = _hostsManager.CurrentDomainCount;
            _db.HostsBlockingOk = (_hostsManager.State != HostsState.Error);
            _db.HostsLastError = _hostsManager.LastError;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restoring active session on startup.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string runMsg = $"[{timestamp}] Blocker Service is running. Monitoring every 1s.";
        Console.WriteLine(runMsg);
        _logger.LogInformation("{Message}", runMsg);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Prune any exited suspended processes
                ProcessSuspender.Default.PruneExited();

                // Check dev-only emergency unlock
                CheckEmergencyUnlock();

                // Compute active profile set and enforce union of apps and hosts
                DateTime localNow = DateTime.Now;
                var allProfiles = _db.ListProfiles();

                // Synchronize session state with database
                SyncSessionState(allProfiles);

                // Check scheduled meditation routines
                CheckMeditationSchedules(DateTime.Now);

                var activeSessions = _trackedSessions.Values.Select(t => t.Session).ToList();
                var activeProfiles = allProfiles.Where(p => ProfileStateLogic.IsProfileActiveWithSessions(p, activeSessions, localNow, out _)).ToList();
                var (unionApps, _) = ProfileStateLogic.ComputeActiveUnionWithSessions(allProfiles, activeSessions, localNow);

                // Compute desired domain set: (SystemWide active profiles) UNION (fallback domains)
                var desiredDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in activeProfiles)
                {
                    if (p.SystemWide && p.BlockedDomains != null)
                    {
                        foreach (var d in p.BlockedDomains)
                        {
                            desiredDomains.Add(d);
                        }
                    }
                }

                // Single unified zero-allocation process pass for maximum performance and instant enforcement
                FastProcessEnumerator.EnumerateProcesses(_processBuffer, _runningProcessNames, _processNameCache);

                var runningBrowserExes = BrowserCatalog.FilterAllBrowserProcesses(_runningProcessNames);
                var fallbackPlan = _db.FallbackPlanner.Plan(activeProfiles, runningBrowserExes, DateTime.UtcNow);
                _db.LastFallbackPlan = fallbackPlan;

                var unauthorizedBrowsers = fallbackPlan.UnauthorizedBrowserProcesses.Count > 0
                    ? new HashSet<string>(fallbackPlan.UnauthorizedBrowserProcesses.Select(b => b.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? b : b + ".exe"), StringComparer.OrdinalIgnoreCase)
                    : null;

                // Truthful grace notifications off the monitor loop with settle delay and USABLE window check
                BrowserGraceNotifier.ProcessGraceNotifications(fallbackPlan, _db);

                // Unified scanning & termination: blocked apps, anti-kill tools, and unauthorized browsers in one pass
                ScanAndKillProcessesUnified(_processBuffer, unionApps, unauthorizedBrowsers, fallbackPlan.UnauthorizedReasons, fallbackPlan.FallbackReason, allProfiles, localNow);

                foreach (var d in fallbackPlan.FallbackDomains)
                {
                    desiredDomains.Add(d);
                }

                // Enforce hosts file blocking (level-triggered)
                _hostsManager.SyncHosts(desiredDomains);
                _db.HostsState = _hostsManager.State;
                _db.HostsDomainCount = _hostsManager.CurrentDomainCount;
                _db.HostsBlockingOk = (_hostsManager.State != HostsState.Error);
                _db.HostsLastError = _hostsManager.LastError;

                // Enforce DNS protection and lock (level-triggered watchdog)
                try
                {
                    var dnsConfig = _db.GetDnsConfig();
                    if (dnsConfig.Enabled)
                    {
                        if (dnsConfig.Locked)
                        {
                            if (string.Equals(dnsConfig.LockType, "Timer", StringComparison.OrdinalIgnoreCase) &&
                                dnsConfig.LockedUntilUtc.HasValue &&
                                DateTime.UtcNow >= dnsConfig.LockedUntilUtc.Value)
                            {
                                dnsConfig.Locked = false;
                                dnsConfig.LockedUntilUtc = null;
                                _db.SaveDnsConfig(dnsConfig);
                                string unlockMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [DNS] Timer expired. DNS Lock released.";
                                Console.WriteLine(unlockMsg);
                                _logger.LogInformation("{Message}", unlockMsg);
                            }
                        }

                        // Re-apply if tampered or adapter disconnected/reconnected (throttled to at most once per 20s)
                        if (DateTime.UtcNow - _lastDnsWatchdogApplyUtc >= TimeSpan.FromSeconds(20))
                        {
                            if (!DnsManager.IsDnsActive(dnsConfig.PrimaryDns, dnsConfig.PrimaryIpv6, dnsConfig.SecondaryDns, dnsConfig.SecondaryIpv6))
                            {
                                _lastDnsWatchdogApplyUtc = DateTime.UtcNow;
                                _ = Task.Run(() =>
                                {
                                    DnsManager.ApplyDns(dnsConfig.PrimaryDns, dnsConfig.SecondaryDns, dnsConfig.PrimaryIpv6, dnsConfig.SecondaryIpv6, out _);
                                });

                                string presetName = dnsConfig.PresetName ?? "Secure DNS";
                                bool dispatched = ServiceNotificationDispatcher.DispatchNotification("DnsTamper", "DNS", presetName);
                                _db.RecordBlockEvent(new BlockEvent
                                {
                                    TimeUtc = DateTime.UtcNow,
                                    Kind = "DnsTamper",
                                    Name = "DNS",
                                    ProfileName = presetName,
                                    ServiceDispatched = dispatched
                                });
                            }
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string errMsg = $"[{ts}] [ERROR] Unexpected error in monitoring cycle: {ex.Message}";
                Console.WriteLine(errMsg);
                _logger.LogError(ex, "{Message}", errMsg);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void CheckEmergencyUnlock()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string[] emergencyFiles = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "EMERGENCY_UNLOCK"),
            Path.Combine(appData, "Blocker", "EMERGENCY_UNLOCK"),
            Path.Combine(appData, "Sankalpa", "EMERGENCY_UNLOCK"),
            Path.Combine(programData, "Sankalpa", "EMERGENCY_UNLOCK"),
            Path.Combine(programData, "Blocker", "EMERGENCY_UNLOCK")
        };

        bool fileFound = emergencyFiles.Any(File.Exists);
        if (fileFound || _db.IsEmergencyUnlockPresent())
        {
            try
            {
                SecurityHardener.UnlockCurrentProcess(out _);
                SecurityHardener.UnlockService(out _);

                _db.EndActiveSession();
                var activeMed = _db.GetActiveMeditationSession();
                if (activeMed != null)
                {
                    _db.EndMeditationSession(activeMed.Id, completed: false);
                    _activeMeditationSession = null;
                }
                ProcessSuspender.Default.ResumeAll();
                BackgroundAppThrottler.Default.RestoreAll();
                _db.DisableAllProfiles();
                _db.CleanHosts();
                _db.ConsumeEmergencyUnlock();
                foreach (var f in emergencyFiles)
                {
                    if (File.Exists(f)) { try { File.Delete(f); } catch { } }
                }

                _hostsManager.SyncHosts(Array.Empty<string>());
                _db.HostsState = _hostsManager.State;
                _db.HostsDomainCount = _hostsManager.CurrentDomainCount;
                _db.HostsBlockingOk = (_hostsManager.State != HostsState.Error);
                _db.HostsLastError = _hostsManager.LastError;

                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string msg = $"[{ts}] [EMERGENCY UNLOCK] Emergency bypass triggered! DACL unlocked, active locked session ended and all profiles disabled.";
                Console.WriteLine(msg);
                _logger.LogWarning("{Message}", msg);

                _trackedSessions.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to process EMERGENCY_UNLOCK: {Message}", ex.Message);
            }
        }
    }

    private void SyncSessionState(List<Profile>? cachedProfiles = null)
    {
        var dbSessions = _db.GetActiveSessions();
        var dbSessionsById = dbSessions.ToDictionary(s => s.Id);
        var dbSessionIds = dbSessions.Select(s => s.Id).ToHashSet();

        // 1. Detect new sessions started externally or via IPC
        foreach (var dbSession in dbSessions)
        {
            if (!_trackedSessions.ContainsKey(dbSession.Id))
            {
                DateTime nowUtc = DateTime.UtcNow;
                if (nowUtc < dbSession.StartUtc)
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    string warnMsg = $"[{timestamp}] [WARNING] System clock moved backwards into the past (StartUtc: {dbSession.StartUtc:O}, NowUtc: {nowUtc:O}). Adjusting StartUtc.";
                    Console.WriteLine(warnMsg);
                    _logger.LogWarning("{Message}", warnMsg);

                    _db.UpdateSessionStartUtc(dbSession.Id, nowUtc);
                    dbSession.StartUtc = nowUtc;
                }

                TimeSpan elapsed = nowUtc - dbSession.StartUtc;
                double remainingSeconds = Math.Max(0, dbSession.DurationSeconds - elapsed.TotalSeconds);

                if (remainingSeconds > 0)
                {
                    var prof = (cachedProfiles?.FirstOrDefault(p => p.Id == dbSession.ProfileId)) ?? _db.GetProfileById(dbSession.ProfileId);
                    _trackedSessions[dbSession.Id] = new TrackedSession
                    {
                        Session = dbSession,
                        Profile = prof,
                        InitialRemaining = TimeSpan.FromSeconds(remainingSeconds),
                        Stopwatch = Stopwatch.StartNew(),
                        BaseSystemTime = DateTime.UtcNow
                    };

                    string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    string msg = $"[{ts}] Session started: Profile='{prof?.Name}', Remaining={remainingSeconds:F0}s (Locked: {dbSession.Locked}).";
                    Console.WriteLine(msg);
                    _logger.LogInformation("{Message}", msg);
                }
            }
        }

        // 2. Check existing tracked sessions for completion or external termination
        foreach (var kvp in _trackedSessions.ToList())
        {
            int sId = kvp.Key;
            var tracked = kvp.Value;

            // Check if the session was explicitly ended or removed from the database
            if (!dbSessionsById.TryGetValue(sId, out var currentDbSession) || currentDbSession.EndedUtc != null)
            {
                currentDbSession = _db.GetSessionById(sId);
            }

            if (currentDbSession == null || currentDbSession.EndedUtc != null)
            {
                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string msg = $"[{ts}] Active session for '{tracked.Profile?.Name}' has been stopped.";
                Console.WriteLine(msg);
                _logger.LogInformation("{Message}", msg);
                _trackedSessions.TryRemove(sId, out _);
                continue;
            }

            tracked.Session = currentDbSession;

            DateTime nowUtc = DateTime.UtcNow;

            // Check backwards clock manipulation on tracked session
            if (nowUtc < tracked.Session.StartUtc)
            {
                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string warnMsg = $"[{ts}] [WARNING] System clock moved backwards into the past (StartUtc: {tracked.Session.StartUtc:O}, NowUtc: {nowUtc:O}). Adjusting StartUtc.";
                Console.WriteLine(warnMsg);
                _logger.LogWarning("{Message}", warnMsg);

                _db.UpdateSessionStartUtc(sId, nowUtc);
                tracked.Session.StartUtc = nowUtc;
                tracked.BaseSystemTime = nowUtc;
            }

            TimeSpan monotonicElapsed = tracked.Stopwatch.Elapsed;
            TimeSpan remaining = tracked.InitialRemaining - monotonicElapsed;

            TimeSpan systemElapsed = nowUtc - tracked.BaseSystemTime;
            double driftSeconds = Math.Abs((systemElapsed - monotonicElapsed).TotalSeconds);
            if (driftSeconds > 5.0)
            {
                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string warnMsg = $"[{ts}] [WARNING] System clock jump detected for profile '{tracked.Profile?.Name}'! Drift is {driftSeconds:F1}s compared to monotonic clock.";
                Console.WriteLine(warnMsg);
                _logger.LogWarning("{Message}", warnMsg);

                // Re-anchor base system time to avoid log spam every second
                tracked.BaseSystemTime = nowUtc;
            }

            // Expiry check: wall-clock Utc >= StartUtc + DurationSeconds AND monotonic elapsed remaining <= TimeSpan.Zero
            bool wallClockExpired = nowUtc >= tracked.Session.StartUtc.AddSeconds(tracked.Session.DurationSeconds);
            bool monotonicExpired = remaining <= TimeSpan.Zero;

            if (wallClockExpired && monotonicExpired)
            {
                _db.EndSession(sId);
                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string msg = $"[{ts}] Active session for '{tracked.Profile?.Name}' has completed and ended.";
                Console.WriteLine(msg);
                _logger.LogInformation("{Message}", msg);
                _trackedSessions.TryRemove(sId, out _);
            }
        }

        // 2b. Synchronize meditation session state
        var currentMeditation = _db.GetActiveMeditationSession();
        if (currentMeditation != null)
        {
            DateTime nowUtc = DateTime.UtcNow;
            long remMedSec = (long)(currentMeditation.StartUtc.AddSeconds(currentMeditation.TargetDurationSeconds) - nowUtc).TotalSeconds;
            if (remMedSec <= 0)
            {
                _db.EndMeditationSession(currentMeditation.Id, completed: true);
                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string msg = $"[{ts}] Meditation session {currentMeditation.Id} completed.";
                Console.WriteLine(msg);
                _logger.LogInformation("{Message}", msg);
                ServiceNotificationDispatcher.DispatchNotification("MeditationComplete", "Meditation", "Meditation session complete! Well done.");
                _activeMeditationSession = null;
                _loggedMeditationPids.Clear();

                DateTime sessionStart = currentMeditation.StartUtc;
                var (resumed, terminated) = ProcessSuspender.Default.FinishSession(sessionStart);
                if (terminated > 0)
                {
                    string termMsg = $"[{ts}] [MEDITATION CLEANUP] Cleaned up {terminated} child process(es) spawned by allowed apps during meditation session.";
                    Console.WriteLine(termMsg);
                    _logger.LogInformation("{Message}", termMsg);
                    _db.RecordBlockEvent(new BlockEvent
                    {
                        TimeUtc = DateTime.UtcNow,
                        Kind = "MeditationChildCleanup",
                        Name = $"{terminated} processes",
                        ProfileName = "Meditation Whitelist",
                        ServiceDispatched = false
                    });
                }

                // Scheduled Blocks Integrity: Ensure meditation session cleanup does NOT touch, disable, or alter standard profile schedules.
                // Standard profiles and their scheduled active windows operate strictly independently at all times.
            }
            else
            {
                _activeMeditationSession = currentMeditation;
            }
        }
        else
        {
            if (_activeMeditationSession != null)
            {
                _loggedMeditationPids.Clear();
                DateTime sessionStart = _activeMeditationSession.StartUtc;
                var (resumed, terminated) = ProcessSuspender.Default.FinishSession(sessionStart);
                if (terminated > 0)
                {
                    string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    string termMsg = $"[{ts}] [MEDITATION CLEANUP] Cleaned up {terminated} child process(es) spawned by allowed apps during meditation session.";
                    Console.WriteLine(termMsg);
                    _logger.LogInformation("{Message}", termMsg);
                    _db.RecordBlockEvent(new BlockEvent
                    {
                        TimeUtc = DateTime.UtcNow,
                        Kind = "MeditationChildCleanup",
                        Name = $"{terminated} processes",
                        ProfileName = "Meditation Whitelist",
                        ServiceDispatched = false
                    });
                }
            }
            _activeMeditationSession = null;
        }

        // Safety timeout for pending meditation sessions (if UI was closed or failed to confirm timer readiness)
        if (_activeMeditationSession != null && string.Equals(_activeMeditationSession.Status, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            if (DateTime.UtcNow > _pendingMeditationDeadline)
            {
                _logger.LogWarning("Pending meditation session {SessionId} timed out waiting for UI timer confirmation. Cancelling cleanly.", _activeMeditationSession.Id);
                _db.EndMeditationSession(_activeMeditationSession.Id, completed: false);
                DateTime sessionStart = _activeMeditationSession.StartUtc;
                _activeMeditationSession = null;
                var (resumed, terminated) = ProcessSuspender.Default.FinishSession(sessionStart);
                if (terminated > 0)
                {
                    string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    string termMsg = $"[{ts}] [MEDITATION CLEANUP] Cleaned up {terminated} child process(es) spawned by allowed apps during meditation session.";
                    Console.WriteLine(termMsg);
                    _logger.LogInformation("{Message}", termMsg);
                    _db.RecordBlockEvent(new BlockEvent
                    {
                        TimeUtc = DateTime.UtcNow,
                        Kind = "MeditationChildCleanup",
                        Name = $"{terminated} processes",
                        ProfileName = "Meditation Whitelist",
                        ServiceDispatched = false
                    });
                }
            }
        }

        // 3. Process & service DACL lockdown maintenance
        DateTime localNow = DateTime.Now;
        var allProfiles = cachedProfiles ?? _db.ListProfiles();
        bool anySessionLocked = _trackedSessions.Values.Any(t => t.Session.Locked);
        bool anyScheduleLocked = allProfiles.Any(p => p.Schedule != null && p.Schedule.Count > 0 && ScheduleLogic.IsActive(p.Schedule, localNow));
        bool anyHardLocked = anySessionLocked || anyScheduleLocked;
        // Meditation Mode is cooperative focus; it never locks process or service DACLs.

        if (anyHardLocked)
        {
            if (!SecurityHardener.IsLockedDown)
            {
                if (SecurityHardener.LockdownCurrentProcess(out var procErr))
                {
                    string procMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SECURITY] Process DACL locked down. Terminate access denied.";
                    Console.WriteLine(procMsg);
                    _logger.LogInformation("{Message}", procMsg);
                }
                else
                {
                    _logger.LogWarning("[SECURITY] Failed to lock down process DACL: {Error}", procErr);
                }
            }

            if (!SecurityHardener.IsServiceLockedDown)
            {
                if (SecurityHardener.LockdownService(out var svcErr))
                {
                    string svcMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SECURITY] Service DACL locked down. Stop access denied.";
                    Console.WriteLine(svcMsg);
                    _logger.LogInformation("{Message}", svcMsg);
                }
                else
                {
                    _logger.LogWarning("[SECURITY] Failed to lock down service DACL: {Error}", svcErr);
                }
            }
        }
        else
        {
            if (SecurityHardener.IsLockedDown)
            {
                SecurityHardener.UnlockCurrentProcess(out _);
                string procMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SECURITY] Process DACL restored.";
                Console.WriteLine(procMsg);
                _logger.LogInformation("{Message}", procMsg);
            }
            if (SecurityHardener.IsServiceLockedDown)
            {
                SecurityHardener.UnlockService(out _);
                string svcMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SECURITY] Service DACL restored.";
                Console.WriteLine(svcMsg);
                _logger.LogInformation("{Message}", svcMsg);
            }
        }
    }

    #region Hosts File Management

    private void CheckElevationAndInitializeHostsState()
    {
        bool isElevated = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                if (identity.IsSystem || identity.User?.Value == "S-1-5-18")
                {
                    isElevated = true;
                }
                else
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    isElevated = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
        }
        catch { }

        _db.ServiceElevated = isElevated;
    }

    #endregion

    private static readonly string[] AntiKillTools = new[]
    {
        "procexp", "procexp.exe",
        "procexp64", "procexp64.exe",
        "resmon", "resmon.exe",
        "processhacker", "processhacker.exe"
    };

    private void ScanAndKillProcessesUnified(
        IReadOnlyList<FastProcessEntry> runningProcesses,
        List<string> currentBlockedList,
        HashSet<string>? unauthorizedBrowsers,
        Dictionary<string, string>? unauthorizedReasons,
        string? browserKillReason,
        List<Profile> allProfiles,
        DateTime localNow)
    {
        // Anti-kill tools are only terminated for hard locked standard sessions, never during meditation mode.
        bool isLockedActive = _trackedSessions.Values.Any(t => t.Session.Locked);
        var activeSessions = _trackedSessions.Values.Select(t => t.Session).ToList();
        var activeProfiles = allProfiles.Where(p => ProfileStateLogic.IsProfileActiveWithSessions(p, activeSessions, localNow, out _)).ToList();
        var currentPids = new HashSet<int>();
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        bool hasBlockedApps = currentBlockedList != null && currentBlockedList.Count > 0;
        bool hasUnauthorizedBrowsers = unauthorizedBrowsers != null && unauthorizedBrowsers.Count > 0;
        var killedBrowsersThisPass = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Compute active console session ID ONCE per tick (never inside the per-process loop)
        uint activeSessionId = WTSGetActiveConsoleSessionId();
        if (activeSessionId == 0xFFFFFFFF || activeSessionId == 0)
        {
            try
            {
                var explorer = Process.GetProcessesByName("explorer").FirstOrDefault(e => e.SessionId > 0);
                if (explorer != null)
                {
                    activeSessionId = (uint)explorer.SessionId;
                }
            }
            catch { }
        }

        // Index running processes by PID for O(1) parent lookup
        _processByPid.Clear();
        for (int i = 0; i < runningProcesses.Count; i++)
        {
            _processByPid[runningProcesses[i].Id] = runningProcesses[i];
        }

        // Check standalone File Explorer windows during active meditation (only if promoted from Pending)
        bool isMeditationActive = _activeMeditationSession != null &&
            !string.Equals(_activeMeditationSession.Status, "Pending", StringComparison.OrdinalIgnoreCase);

        List<string>? allowedApps = null;
        var allowedAppDirs = new Dictionary<int, string>();
        if (isMeditationActive)
        {
            allowedApps = _activeMeditationSession!.GetAllowedApps() ?? new List<string>();
            _meditationAllowedCache.SyncSession(
                _activeMeditationSession.Id,
                _activeMeditationSession.Status,
                _activeMeditationSession.StartUtc,
                allowedApps,
                _db.ActiveAllowedAppRoots);

            if (!_meditationAllowedCache.IsExplorerAllowed)
            {
                SuppressStandaloneExplorerWindows(timestamp);
            }
        }
        else
        {
            _meditationAllowedCache.Reset();
            if (_loggedCabinetHwnds.Count > 0)
            {
                _loggedCabinetHwnds.Clear();
            }
        }

        for (int i = 0; i < runningProcesses.Count; i++)
        {
            var process = runningProcesses[i];
            try
            {
                int processId = process.Id;
                currentPids.Add(processId);

                // Self-safety: never kill current process or its parent/ancestor processes
                if (processId == CurrentProcessId || _protectedPids.Contains(processId))
                {
                    continue;
                }

                string processName = process.ProcessName;
                string exeName = process.ExeName;

                // 1. Check unauthorized browsers first (e.g. unextended msedge, chrome, tor, opera)
                if (hasUnauthorizedBrowsers && (unauthorizedBrowsers!.Contains(exeName) || unauthorizedBrowsers!.Contains(processName + ".exe")))
                {
                    try
                    {
                        using var pObj = Process.GetProcessById(processId);
                        if (!string.Equals(pObj.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // Process identity verification: ensure process image matches before kill
                        string? currentPath = GetProcessImagePath(processId);
                        if (!string.IsNullOrEmpty(currentPath) &&
                            !currentPath.EndsWith(exeName, StringComparison.OrdinalIgnoreCase) &&
                            !currentPath.EndsWith(processName + ".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // PID was recycled by another process
                        }

                        try
                        {
                            pObj.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                            try
                            {
                                pObj.Kill();
                            }
                            catch { }
                        }
                    }
                    catch { }

                    if (killedBrowsersThisPass.Add(exeName))
                    {
                        ExecuteTaskkillByImageName(exeName);

                        _db.FallbackPlanner.ResetBrowserTracking(exeName);

                        _loggedGraceBrowsers.RemoveWhere(b =>
                            b.StartsWith(exeName + ":", StringComparison.OrdinalIgnoreCase) ||
                            b.StartsWith(exeName + ".exe:", StringComparison.OrdinalIgnoreCase) ||
                            b.Equals(exeName, StringComparison.OrdinalIgnoreCase));

                        string specificReason = (unauthorizedReasons != null && unauthorizedReasons.TryGetValue(exeName, out var r))
                            ? r
                            : (browserKillReason ?? "Sankalpa extension missing or 'Allow in InPrivate' is disabled");

                        string msg = $"[ZenEnforce] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [KILLED] [EXTENSION REQUIRED] Process '{exeName}' terminated: {specificReason}";
                        Console.WriteLine(msg);
                        _logger.LogInformation("{Message}", msg);

                        bool dispatched = ServiceNotificationDispatcher.DispatchNotification("BrowserKill", exeName, specificReason);

                        _db.RecordBlockEvent(new BlockEvent
                        {
                            TimeUtc = DateTime.UtcNow,
                            Kind = "BrowserKill",
                            Name = exeName,
                            ProfileName = specificReason,
                            ServiceDispatched = dispatched
                        });
                    }
                    continue;
                }

                // 2. Check blocked apps and anti-kill tools
                if (hasBlockedApps || isLockedActive)
                {
                    // Fast-path name check before inspecting disk or PE headers
                    string? originalFileName = null;
                    string? imagePath = GetProcessImagePath(processId);
                    bool nameMatchesBlocked = hasBlockedApps && ProcessMatcher.IsBlocked(processName, currentBlockedList, null, imagePath);
                    bool nameMatchesAntiKill = isLockedActive && AntiKillTools.Any(t => t.Equals(processName, StringComparison.OrdinalIgnoreCase));

                    if (!nameMatchesBlocked && !nameMatchesAntiKill && isLockedActive)
                    {
                        originalFileName = GetCachedOriginalFileName(processId);
                    }

                    bool isAntiKillTool = isLockedActive && (nameMatchesAntiKill || (originalFileName != null && AntiKillTools.Any(t => t.Equals(originalFileName, StringComparison.OrdinalIgnoreCase))));
                    bool isBlockedApp = nameMatchesBlocked || (hasBlockedApps && originalFileName != null && ProcessMatcher.IsBlocked(processName, currentBlockedList, originalFileName, imagePath));

                    if (isAntiKillTool || isBlockedApp)
                    {
                        try
                        {
                            using var targetProc = Process.GetProcessById(processId);
                            if (!string.Equals(targetProc.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            // Process identity verification: ensure executable path matches before kill
                            string? currentPath = GetProcessImagePath(processId);
                            if (!string.IsNullOrEmpty(imagePath) && !string.IsNullOrEmpty(currentPath))
                            {
                                try
                                {
                                    if (!string.Equals(Path.GetFullPath(currentPath), Path.GetFullPath(imagePath), StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue; // Identity mismatch: PID reuse!
                                    }
                                }
                                catch
                                {
                                    continue;
                                }
                            }

                            try
                            {
                                targetProc.Kill(entireProcessTree: true);
                            }
                            catch
                            {
                                try
                                {
                                    targetProc.Kill();
                                }
                                catch { }
                            }

                            // If blocked app is a browser or multi-process app, sweep all detached/child processes via taskkill
                            bool isBrowserOrMultiProcess =
                                BrowserCatalog.IsSupportedBrowser(exeName) ||
                                BrowserCatalog.IsUnsupportedBrowser(exeName) ||
                                BrowserCatalog.IsSupportedBrowser(processName) ||
                                BrowserCatalog.IsUnsupportedBrowser(processName);

                            if (isBrowserOrMultiProcess && killedBrowsersThisPass.Add(exeName))
                            {
                                ExecuteTaskkillByImageName(exeName);
                            }

                            string matchingProfileName = isAntiKillTool
                                ? "Sankalpa Tamper Protection"
                                : (activeProfiles
                                    .FirstOrDefault(p => ProcessMatcher.IsBlocked(processName, p.BlockedProcesses, originalFileName))
                                    ?.Name ?? "Default");

                            bool dispatched = ServiceNotificationDispatcher.DispatchNotification("App", processName, matchingProfileName);

                            _db.RecordBlockEvent(new BlockEvent
                            {
                                TimeUtc = DateTime.UtcNow,
                                Kind = "App",
                                Name = processName,
                                ProfileName = matchingProfileName,
                                ServiceDispatched = dispatched
                            });

                            if (_loggedKillPids.Add(processId))
                            {
                                string details = !string.IsNullOrEmpty(originalFileName)
                                    ? $"Process '{processName}' (OriginalFilename: '{originalFileName}', PID: {processId})"
                                    : $"Process '{processName}' (PID: {processId})";
                                string killReason = isAntiKillTool ? " [TAMPER ATTEMPT BLOCKED]" : "";
                                string killMsg = $"[ZenEnforce] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [KILLED]{killReason} {details} terminated.";
                                Console.WriteLine(killMsg);
                                _logger.LogInformation("{Message}", killMsg);
                            }
                        }
                        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                        {
                            if (_loggedFailurePids.Add(processId))
                            {
                                string failMsg = $"[ZenEnforce] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [FAILED] Failed to terminate process '{processName}' (PID: {processId}): {ex.Message}";
                                Console.WriteLine(failMsg);
                                _logger.LogWarning("{Message}", failMsg);
                            }
                        }
                    }
                }

                // 3. Meditation Mode Whitelist enforcement via thread suspension (non-destructive)
                if (isMeditationActive && allowedApps != null)
                {
                    // Skip syscalls for already-suspended PIDs immediately!
                    if (ProcessSuspender.Default.IsSuspended(processId))
                    {
                        continue;
                    }

                    // Query process creation time & image path in a single syscall
                    long creationTime = 0;
                    string? path = null;
                    if (!_meditationAllowedCache.TryGetCachedIdentity(processId, out creationTime, out path))
                    {
                        ProcessIdentityHelper.GetProcessIdentity(processId, out creationTime, out path);
                        if (string.IsNullOrEmpty(path))
                        {
                            path = GetProcessImagePath(processId);
                        }
                        _meditationAllowedCache.CacheIdentity(processId, creationTime, path);
                    }

                    // Fast-path decision lookup: keyed by (PID, creationTime) to guarantee PID reuse safety
                    if (_meditationAllowedCache.TryGetCachedDecision(processId, creationTime, out var decision))
                    {
                        if (decision == ProcessAllowDecision.Allowed || decision == ProcessAllowDecision.Exempt)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        bool isAppPermitted = _meditationAllowedCache.IsAppAllowedFast(processName, exeName, path);

                        // Child exemption:
                        // If a process is NOT directly in allowedApps, check its ParentId:
                        // If process.ParentId > 0 and the parent process is an allowed app (or parent PID is tracked as an allowed app):
                        // Check if the child process executable/image path is inside that allowed app's installation directory!
                        if (!isAppPermitted && process.ParentId > 0)
                        {
                            if (!allowedAppDirs.TryGetValue(process.ParentId, out string? parentInstallDir))
                            {
                                if (!_meditationAllowedCache.TryGetAllowedAppDir(process.ParentId, out parentInstallDir))
                                {
                                    if (_processByPid.TryGetValue(process.ParentId, out var parentEntry))
                                    {
                                        string? parentImagePath = GetProcessImagePath(parentEntry.Id);
                                        if (_meditationAllowedCache.IsAppAllowedFast(parentEntry.ProcessName, parentEntry.ExeName, parentImagePath))
                                        {
                                            if (!string.IsNullOrEmpty(parentImagePath))
                                            {
                                                string? dir = Path.GetDirectoryName(parentImagePath);
                                                if (!string.IsNullOrEmpty(dir) && !IsSystemOrRootDirectory(dir))
                                                {
                                                    parentInstallDir = dir;
                                                    allowedAppDirs[parentEntry.Id] = dir;
                                                    _meditationAllowedCache.RecordAllowedAppDir(parentEntry.Id, dir);
                                                }
                                            }
                                        }
                                    }
                                }
                                else if (!string.IsNullOrEmpty(parentInstallDir))
                                {
                                    allowedAppDirs[process.ParentId] = parentInstallDir;
                                }
                            }

                            if (!string.IsNullOrEmpty(parentInstallDir))
                            {
                                if (!string.IsNullOrEmpty(path) && ProtectedProcesses.IsPathInsideDirectory(path, parentInstallDir))
                                {
                                    allowedAppDirs[processId] = parentInstallDir;
                                    _meditationAllowedCache.RecordAllowedAppDir(processId, parentInstallDir);
                                    _meditationAllowedCache.CacheDecision(processId, creationTime, ProcessAllowDecision.Allowed);
                                    continue; // Exempt from suspension!
                                }
                            }
                        }

                        if (isAppPermitted)
                        {
                            if (!string.IsNullOrEmpty(path))
                            {
                                string? dir = Path.GetDirectoryName(path);
                                if (!string.IsNullOrEmpty(dir) && !IsSystemOrRootDirectory(dir))
                                {
                                    allowedAppDirs[processId] = dir;
                                    _meditationAllowedCache.RecordAllowedAppDir(processId, dir);
                                }
                            }
                            _meditationAllowedCache.CacheDecision(processId, creationTime, ProcessAllowDecision.Allowed);
                            continue;
                        }

                        if (ProtectedProcesses.IsProtected(processName, path) ||
                            ProtectedProcesses.IsProtected(exeName, path))
                        {
                            _meditationAllowedCache.CacheDecision(processId, creationTime, ProcessAllowDecision.Exempt);
                            continue;
                        }

                        int targetSession = -1;
                        if (OperatingSystem.IsWindows() && ProcessIdToSessionId((uint)processId, out uint sessId))
                        {
                            targetSession = (int)sessId;
                        }

                        bool isTargetInActiveSession = (activeSessionId != 0xFFFFFFFF && targetSession == (int)activeSessionId) ||
                                                      (targetSession > 0 && targetSession == CurrentProcessSessionId) ||
                                                      (activeSessionId == 0xFFFFFFFF && targetSession > 0);

                        if (!isTargetInActiveSession || string.IsNullOrEmpty(path))
                        {
                            _meditationAllowedCache.CacheDecision(processId, creationTime, ProcessAllowDecision.Exempt);
                            continue;
                        }

                        if (SystemHelperGuard.IsTrusted(path))
                        {
                            _meditationAllowedCache.CacheDecision(processId, creationTime, ProcessAllowDecision.Exempt);
                            continue;
                        }

                        _meditationAllowedCache.CacheDecision(processId, creationTime, ProcessAllowDecision.MustSuspend);
                    }

                    try
                    {
                        DateTime? creationTimeUtc = creationTime > 0 ? DateTime.FromFileTimeUtc(creationTime) : GetProcessCreationTimeUtc(processId);
                        bool fromAllowedApp = IsDescendedFromAllowedApp(process.ParentId, allowedApps);

                        if (ProcessSuspender.Default.SuspendProcess(processId, exeName, fromAllowedApp, creationTimeUtc, path))
                        {
                            if (_loggedMeditationPids.Add(processId))
                            {
                                string msg = $"[{timestamp}] [MEDITATION SUSPEND] Process '{exeName}' (PID: {processId}) suspended (not in allowed meditation apps).";
                                Console.WriteLine(msg);
                                _logger.LogInformation("{Message}", msg);

                                _db.RecordBlockEvent(new BlockEvent
                                {
                                    TimeUtc = DateTime.UtcNow,
                                    Kind = "MeditationSuspend",
                                    Name = exeName,
                                    ProfileName = "Meditation Whitelist",
                                    ServiceDispatched = false
                                });
                            }
                        }
                    }
                    catch { }
                    continue;
                }
            }
            catch
            {
                // Process might have terminated between snapshot and check
            }
        }

        // Clean up PIDs for processes that have exited to prevent memory leaks and handle PID recycling
        _loggedKillPids.IntersectWith(currentPids);
        _loggedFailurePids.IntersectWith(currentPids);
        _loggedMeditationPids.IntersectWith(currentPids);
        _meditationAllowedCache.PruneExited(currentPids);
        foreach (var pid in _pidOriginalFileNameCache.Keys)
        {
            if (!currentPids.Contains(pid))
            {
                _pidOriginalFileNameCache.TryRemove(pid, out _);
            }
        }
        foreach (var pid in _processPathCache.Keys)
        {
            if (!currentPids.Contains(pid))
            {
                _processPathCache.TryRemove(pid, out _);
            }
        }
    }

    private string? GetProcessImagePath(int processId)
    {
        if (processId <= 4)
        {
            return null;
        }

        if (_processPathCache.TryGetValue(processId, out string? cached))
        {
            return cached;
        }

        string? path = null;
        if (OperatingSystem.IsWindows())
        {
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                    {
                        path = sb.ToString();
                    }
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
        }

        if (string.IsNullOrEmpty(path))
        {
            try
            {
                using var p = Process.GetProcessById(processId);
                path = p.MainModule?.FileName;
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(path))
        {
            _processPathCache[processId] = path;
        }
        return path;
    }

    private static bool IsPathInsideDirectory(string filePath, string directoryPath)
    {
        return ProtectedProcesses.IsPathInsideDirectory(filePath, directoryPath);
    }

    private static bool IsSystemOrRootDirectory(string directoryPath)
    {
        try
        {
            string fullDirPath = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = Path.GetPathRoot(fullDirPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "";
            if (string.Equals(fullDirPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullDirPath, winDir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string sysDir = Environment.SystemDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullDirPath, sysDir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private string? GetCachedOriginalFileName(int processId)
    {
        if (_pidOriginalFileNameCache.TryGetValue(processId, out string? cached))
        {
            return cached;
        }

        string? originalFileName = null;
        try
        {
            string? filePath = GetProcessImagePath(processId);
            if (!string.IsNullOrEmpty(filePath))
            {
                if (!_originalFileNameCache.TryGetValue(filePath, out originalFileName))
                {
                    try
                    {
                        var versionInfo = FileVersionInfo.GetVersionInfo(filePath);
                        originalFileName = versionInfo.OriginalFilename;
                    }
                    catch
                    {
                        originalFileName = null;
                    }

                    _originalFileNameCache[filePath] = originalFileName;
                }
            }
        }
        catch
        {
            // Access denied or module inspection not allowed - fall back safely to null
            originalFileName = null;
        }

        _pidOriginalFileNameCache[processId] = originalFileName;
        return originalFileName;
    }

    private static void ExecuteTaskkillByImageName(string exeName)
    {
        try
        {
            string systemDir = Environment.SystemDirectory;
            string taskkillPath = Path.Combine(systemDir, "taskkill.exe");
            if (!File.Exists(taskkillPath))
            {
                taskkillPath = @"C:\Windows\System32\taskkill.exe";
            }
            if (!File.Exists(taskkillPath))
            {
                taskkillPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
            }
            if (!File.Exists(taskkillPath))
            {
                taskkillPath = "taskkill.exe";
            }

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = taskkillPath,
                Arguments = $"/F /T /IM \"{exeName}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(1500);
        }
        catch { }
    }

    #region Meditation Mode Helpers

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr hProcess,
        out long lpCreationTime,
        out long lpExitTime,
        out long lpKernelTime,
        out long lpUserTime);

    private static DateTime? GetProcessCreationTimeUtc(int processId)
    {
        if (processId <= 4 || !OperatingSystem.IsWindows()) return null;
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero) return null;
        try
        {
            if (GetProcessTimes(hProcess, out long creationTime, out _, out _, out _) && creationTime > 0)
            {
                return DateTime.FromFileTimeUtc(creationTime);
            }
        }
        catch { }
        finally
        {
            CloseHandle(hProcess);
        }
        return null;
    }

    public static bool IsAppAllowed(string processName, string exeName, List<string> allowedApps)
    {
        return IsAppAllowed(processName, exeName, allowedApps, null, null);
    }

    public static bool IsAppAllowed(
        string processName,
        string exeName,
        List<string> allowedApps,
        string? imagePath,
        IReadOnlyDictionary<string, string>? allowedAppRoots)
    {
        return IsProcessAllowedPredicate(processName, exeName, imagePath, allowedApps, allowedAppRoots);
    }

    /// <summary>
    /// Published Contract C-3 allowed-process predicate.
    /// Determines whether a process is permitted during active meditation mode:
    /// - Checks system clipping tools against canonical system folders.
    /// - Matches process against allowedApps with install root directory pinning.
    /// - For Edge: verifies msedge.exe against canonical Edge install roots (rejecting user-writable folder imposters).
    /// - For Edge helpers: verifies helper name, canonical install root, and parent/launcher lineage when msedge is allowed.
    /// - For child processes: verifies multi-hop ancestry and containment inside the allowed ancestor's install tree.
    /// </summary>
    public static bool IsProcessAllowedPredicate(
        string processName,
        string exeName,
        string? imagePath,
        List<string>? allowedApps,
        IReadOnlyDictionary<string, string>? allowedAppRoots,
        int parentId = 0,
        IReadOnlyDictionary<int, FastProcessEntry>? processByPid = null,
        Func<int, string?>? getImagePath = null)
    {
        string normProc = ProcessMatcher.NormalizeProcessName(processName);
        string normExe = ProcessMatcher.NormalizeProcessName(exeName);

        // 1. Built-in system clipping tools
        if (string.Equals(normProc, "screenclippinghost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normExe, "screenclippinghost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normProc, "snippingtool", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normExe, "snippingtool", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return true;
            }
            return ProtectedProcesses.IsProtected(normProc, imagePath) || ProtectedProcesses.IsProtected(normExe, imagePath);
        }

        if (allowedApps == null || allowedApps.Count == 0)
        {
            return false;
        }

        bool isEdgeAllowed = false;

        // 2. Direct allowed app match
        foreach (var allowed in allowedApps)
        {
            string normAllowed = ProcessMatcher.NormalizeProcessName(allowed);
            if (string.Equals(normAllowed, "msedge", StringComparison.OrdinalIgnoreCase))
            {
                isEdgeAllowed = true;
            }

            bool nameMatch = (!string.IsNullOrEmpty(normProc) && string.Equals(normProc, normAllowed, StringComparison.OrdinalIgnoreCase)) ||
                             (!string.IsNullOrEmpty(normExe) && string.Equals(normExe, normAllowed, StringComparison.OrdinalIgnoreCase));

            if (nameMatch)
            {
                if (allowedAppRoots != null &&
                    allowedAppRoots.TryGetValue(normAllowed, out var rootDir) &&
                    !string.IsNullOrWhiteSpace(rootDir))
                {
                    if (!string.IsNullOrWhiteSpace(imagePath))
                    {
                        if (ProtectedProcesses.IsPathInsideDirectory(imagePath, rootDir))
                        {
                            return true;
                        }
                        // Executable path does not match the pinned root (e.g. claude.exe CLI vs desktop app)
                        continue;
                    }
                }
                else if (string.Equals(normAllowed, "msedge", StringComparison.OrdinalIgnoreCase))
                {
                    // Enforce that msedge.exe itself must be located in a canonical Edge installation directory
                    if (!string.IsNullOrWhiteSpace(imagePath))
                    {
                        if (ProtectedProcesses.IsEdgeInstallRoot(imagePath))
                        {
                            return true;
                        }
                        // Renamed msedge in user-writable folder rejected
                        continue;
                    }
                }
                return true;
            }
        }

        // 3. Legitimate Edge helper exemption (only active when msedge is allowed)
        if (isEdgeAllowed && (ProtectedProcesses.IsLegitimateEdgeHelper(normExe, imagePath) || ProtectedProcesses.IsLegitimateEdgeHelper(normProc, imagePath)))
        {
            // If parent process is still active, verify ancestor chain
            if (parentId > 0 && processByPid != null)
            {
                var visited = new HashSet<int>();
                int cur = parentId;
                int hops = 0;
                bool parentChainValid = false;

                while (cur > 0 && hops < 16)
                {
                    if (!visited.Add(cur)) break;
                    if (!processByPid.TryGetValue(cur, out var anc))
                    {
                        // Parent exited; launcher or worker lifecycle
                        parentChainValid = true;
                        break;
                    }

                    string normAnc = ProcessMatcher.NormalizeProcessName(anc.ProcessName);
                    string normAncExe = ProcessMatcher.NormalizeProcessName(anc.ExeName);

                    if (string.Equals(normAnc, "msedge", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(normAncExe, "msedge", StringComparison.OrdinalIgnoreCase))
                    {
                        parentChainValid = true;
                        break;
                    }

                    if (ProtectedProcesses.IsLegitimateEdgeHelper(normAncExe, getImagePath?.Invoke(anc.Id)) ||
                        ProtectedProcesses.IsLegitimateEdgeHelper(normAnc, getImagePath?.Invoke(anc.Id)))
                    {
                        cur = anc.ParentId;
                        hops++;
                        continue;
                    }

                    // Launcher pipeline parent (explorer, services, svchost)
                    if (string.Equals(normAnc, "explorer", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(normAnc, "services", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(normAnc, "svchost", StringComparison.OrdinalIgnoreCase))
                    {
                        parentChainValid = true;
                    }
                    break;
                }

                if (parentChainValid)
                {
                    return true;
                }
            }
            else
            {
                // Parent already exited or no parent PID: authentic Edge helper verified inside canonical root
                return true;
            }
        }

        // 4. General child process exemption (multi-hop ancestor containment)
        if (parentId > 0 && processByPid != null && !string.IsNullOrWhiteSpace(imagePath))
        {
            var visited = new HashSet<int>();
            int cur = parentId;
            int hops = 0;

            while (cur > 0 && hops < 16)
            {
                if (!visited.Add(cur)) break;
                if (!processByPid.TryGetValue(cur, out var anc)) break;

                string? ancPath = getImagePath?.Invoke(anc.Id);
                if (IsAppAllowed(anc.ProcessName, anc.ExeName, allowedApps, ancPath, allowedAppRoots))
                {
                    if (!string.IsNullOrEmpty(ancPath))
                    {
                        string? ancDir = Path.GetDirectoryName(ancPath);
                        if (!string.IsNullOrEmpty(ancDir) && !IsSystemOrRootDirectory(ancDir))
                        {
                            if (ProtectedProcesses.IsPathInsideDirectory(imagePath, ancDir))
                            {
                                return true;
                            }
                        }
                    }
                }

                cur = anc.ParentId;
                hops++;
            }
        }

        return false;
    }

    public bool IsDescendedFromAllowedApp(int parentId, List<string> allowedApps)
    {
        return IsDescendedFromAllowedApp(parentId, allowedApps, _processByPid, GetProcessImagePath, _db.ActiveAllowedAppRoots);
    }

    public static bool IsDescendedFromAllowedApp(int parentId, List<string> allowedApps, IReadOnlyDictionary<int, FastProcessEntry> processByPid)
    {
        return IsDescendedFromAllowedApp(parentId, allowedApps, processByPid, null, null);
    }

    public static bool IsDescendedFromAllowedApp(
        int parentId,
        List<string> allowedApps,
        IReadOnlyDictionary<int, FastProcessEntry> processByPid,
        Func<int, string?>? getImagePath,
        IReadOnlyDictionary<string, string>? allowedAppRoots)
    {
        if (parentId <= 0 || allowedApps == null || allowedApps.Count == 0 || processByPid == null)
        {
            return false;
        }

        var visited = new HashSet<int>();
        int currentPid = parentId;
        int hops = 0;

        while (currentPid > 0 && hops < 16)
        {
            if (!visited.Add(currentPid))
            {
                return false;
            }

            if (!processByPid.TryGetValue(currentPid, out var ancestor))
            {
                return false;
            }

            string? ancestorPath = getImagePath?.Invoke(ancestor.Id);
            if (IsAppAllowed(ancestor.ProcessName, ancestor.ExeName, allowedApps, ancestorPath, allowedAppRoots))
            {
                return true;
            }

            currentPid = ancestor.ParentId;
            hops++;
        }

        return false;
    }

    private void SuppressStandaloneExplorerWindows(string timestamp)
    {
        try
        {
            var foundHwnds = new HashSet<IntPtr>();
            foreach (var className in new[] { "CabinetWClass", "ExploreWClass" })
            {
                IntPtr hwnd = IntPtr.Zero;
                while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, className, null)) != IntPtr.Zero)
                {
                    foundHwnds.Add(hwnd);
                    ShowWindow(hwnd, SW_FORCEMINIMIZE);
                    ShowWindow(hwnd, SW_MINIMIZE);
                    PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

                    if (_loggedCabinetHwnds.Add(hwnd))
                    {
                        string msg = $"[{timestamp}] [MEDITATION WHITELIST] Standalone File Explorer window suppressed ({className}).";
                        Console.WriteLine(msg);
                        _logger.LogInformation("{Message}", msg);

                        bool dispatched = ServiceNotificationDispatcher.DispatchNotification("MeditationWhitelist", "explorer.exe", "Standalone File Explorer closed");
                        _db.RecordBlockEvent(new BlockEvent
                        {
                            TimeUtc = DateTime.UtcNow,
                            Kind = "MeditationWhitelist",
                            Name = "explorer.exe",
                            ProfileName = "Standalone File Explorer",
                            ServiceDispatched = dispatched
                        });
                    }
                }
            }
            _loggedCabinetHwnds.IntersectWith(foundHwnds);
        }
        catch { }
    }

    private void CheckMeditationSchedules(DateTime localNow)
    {
        try
        {
            var schedules = _db.GetMeditationSchedules();
            if (schedules == null || schedules.Count == 0) return;

            foreach (var schedule in schedules)
            {
                if (!schedule.Enabled) continue;

                var days = schedule.GetDays();
                if (days.Count > 0 && !days.Contains(localNow.DayOfWeek))
                {
                    continue;
                }

                if (!TimeSpan.TryParse(schedule.TimeOfDay, out TimeSpan schedTime))
                {
                    continue;
                }

                DateTime schedTargetDateTime = localNow.Date.Add(schedTime);

                // Check if currently snoozed
                bool isSnoozed = schedule.LastSnoozedUtc.HasValue &&
                                 (DateTime.UtcNow - schedule.LastSnoozedUtc.Value) < TimeSpan.FromMinutes(5);

                DateTime effectiveTriggerTime = isSnoozed
                    ? schedule.LastSnoozedUtc!.Value.AddMinutes(5).ToLocalTime()
                    : schedTargetDateTime;

                // 1. 60-second heads-up warning
                if (localNow >= effectiveTriggerTime.AddSeconds(-60) && localNow < effectiveTriggerTime)
                {
                    if (!_scheduleWarningNotifiedUtc.TryGetValue(schedule.Id, out var lastWarn) ||
                        (DateTime.UtcNow - lastWarn) > TimeSpan.FromMinutes(2))
                    {
                        _scheduleWarningNotifiedUtc[schedule.Id] = DateTime.UtcNow;

                        string warnMsg = isSnoozed
                            ? "Snoozed meditation session will begin in 60 seconds."
                            : $"Meditation session scheduled for {schedule.TimeOfDay} will begin in 60 seconds.";

                        string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        string consoleMsg = $"[{ts}] [MEDITATION WARNING] {warnMsg}";
                        Console.WriteLine(consoleMsg);
                        _logger.LogInformation("{Message}", consoleMsg);

                        ServiceNotificationDispatcher.DispatchNotification("MeditationWarning", "Meditation Reminder", warnMsg);
                    }
                }

                // 2. Scheduled trigger
                if (localNow >= effectiveTriggerTime && localNow < effectiveTriggerTime.AddMinutes(5))
                {
                    if (!_scheduleTriggeredUtc.TryGetValue(schedule.Id, out var lastTrig) ||
                        (DateTime.UtcNow - lastTrig) > TimeSpan.FromMinutes(10))
                    {
                        _scheduleTriggeredUtc[schedule.Id] = DateTime.UtcNow;

                        // Clear snooze state in DB once triggered
                        if (schedule.LastSnoozedUtc.HasValue)
                        {
                            schedule.LastSnoozedUtc = null;
                            _db.SaveMeditationSchedule(schedule);
                        }

                        if (_activeMeditationSession == null)
                        {
                            var started = _db.StartMeditationSession(
                                schedule.DurationSeconds,
                                schedule.GetAllowedApps(),
                                schedule.LockMode,
                                schedule.Soundscape,
                                schedule.PasswordHash,
                                "Pending");

                            _activeMeditationSession = started;
                            _pendingMeditationDeadline = DateTime.UtcNow.AddSeconds(10);

                            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                            string msg = $"[{ts}] [MEDITATION SCHEDULED] Scheduled meditation session {started.Id} pending UI confirmation ({schedule.DurationSeconds / 60}m).";
                            Console.WriteLine(msg);
                            _logger.LogInformation("{Message}", msg);

                            ServiceNotificationDispatcher.DispatchNotification("MeditationStart", "Meditation Scheduled", $"Meditation session ready ({schedule.DurationSeconds / 60}m).");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking meditation schedules: {Message}", ex.Message);
        }
    }

    #endregion
}
