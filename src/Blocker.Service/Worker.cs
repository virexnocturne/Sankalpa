using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Blocker.Core;

namespace Blocker.Service;

public class Worker : BackgroundService
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_MINIMIZE = 6;
    private const int SW_FORCEMINIMIZE = 11;

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


    private readonly ConcurrentDictionary<string, string?> _originalFileNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, string?> _pidOriginalFileNameCache = new();
    private readonly HashSet<int> _protectedPids = new();
    private readonly HashSet<int> _loggedKillPids = new();
    private readonly HashSet<int> _loggedFailurePids = new();
    private readonly HashSet<string> _loggedGraceBrowsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FastProcessEntry> _processBuffer = new(512);
    private readonly HashSet<string> _runningProcessNames = new(512, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string ProcessName, string ExeName)> _processNameCache = new(512, StringComparer.OrdinalIgnoreCase);
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

        // Restore active session if one was running prior to service shutdown/restart
        RestoreActiveSessionOnStartup();

        return base.StartAsync(cancellationToken);
    }

    private void RefreshProtectedPids()
    {
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
            _protectedPids.Add(Environment.ProcessId);
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
                }
            }

            // Enforce active union (scheduled profiles + restored manual session + meditation) on startup
            DateTime localNow = DateTime.Now;
            var allProfiles = _db.ListProfiles();
            bool anySessionLocked = _trackedSessions.Values.Any(t => t.Session.Locked);
            bool anyScheduleLocked = allProfiles.Any(p => p.Schedule != null && p.Schedule.Count > 0 && ScheduleLogic.IsActive(p.Schedule, localNow));
            bool anyHardLocked = anySessionLocked || anyScheduleLocked;
            if (_activeMeditationSession != null &&
                (_activeMeditationSession.LockMode == LockMode.HardLocked || _activeMeditationSession.LockMode == LockMode.Locked))
            {
                anyHardLocked = true;
            }

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
                // Check dev-only emergency unlock
                CheckEmergencyUnlock();

                // Synchronize session state with database
                SyncSessionState();

                // Check scheduled meditation routines
                CheckMeditationSchedules(DateTime.Now);

                // Compute active profile set and enforce union of apps and hosts
                DateTime localNow = DateTime.Now;
                var allProfiles = _db.ListProfiles();
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

                // Prune browsers that are no longer in grace period
                _loggedGraceBrowsers.RemoveWhere(b => !fallbackPlan.GracePeriodBrowsers.Any(gb =>
                    b.StartsWith(gb + ":", StringComparison.OrdinalIgnoreCase) ||
                    b.StartsWith((gb.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? gb : gb + ".exe") + ":", StringComparison.OrdinalIgnoreCase) ||
                    b.Equals(gb, StringComparison.OrdinalIgnoreCase)));

                // Notify about browsers in grace period:
                // - Alert immediately upon entering grace (initial warning, 60s)
                // - Intermediate warning at 30s remaining
                // - Final warning at <= 10s remaining
                foreach (var gb in fallbackPlan.GracePeriodBrowsers)
                {
                    string gExe = gb.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? gb : gb + ".exe";
                    int remSec = fallbackPlan.GracePeriodRemainingSeconds.TryGetValue(gExe, out int s) ? s : 60;

                    string logKey;
                    if (remSec <= 10)
                    {
                        logKey = $"{gExe}:final";
                    }
                    else if (remSec <= 30)
                    {
                        logKey = $"{gExe}:intermediate";
                    }
                    else
                    {
                        logKey = $"{gExe}:initial";
                    }

                    if (_loggedGraceBrowsers.Add(logKey))
                    {
                        string dispName = BrowserCatalog.GetDisplayName(gExe);
                        string graceMsg = $"Extension required. {dispName} will close in {remSec}s unless Sankalpa extension is active and 'Allow in InPrivate' is enabled.";

                        bool dispatched = ServiceNotificationDispatcher.DispatchNotification("BrowserGrace", gExe, graceMsg);

                        _db.RecordBlockEvent(new BlockEvent
                        {
                            TimeUtc = DateTime.UtcNow,
                            Kind = "BrowserGrace",
                            Name = gExe,
                            ProfileName = graceMsg,
                            ServiceDispatched = dispatched
                        });
                    }
                }

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
                            if (!DnsManager.IsDnsActive(dnsConfig.PrimaryDns, dnsConfig.PrimaryIpv6))
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
        string[] emergencyFiles = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "EMERGENCY_UNLOCK"),
            Path.Combine(appData, "Blocker", "EMERGENCY_UNLOCK"),
            Path.Combine(appData, "Sankalpa", "EMERGENCY_UNLOCK")
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

    private void SyncSessionState()
    {
        var dbSessions = _db.GetActiveSessions();
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
                    var prof = _db.GetProfileById(dbSession.ProfileId);
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
            var currentDbSession = _db.GetSessionById(sId);
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
            }
            else
            {
                _activeMeditationSession = currentMeditation;
            }
        }
        else
        {
            _activeMeditationSession = null;
        }

        // 3. Process & service DACL lockdown maintenance
        DateTime localNow = DateTime.Now;
        var allProfiles = _db.ListProfiles();
        bool anySessionLocked = _trackedSessions.Values.Any(t => t.Session.Locked);
        bool anyScheduleLocked = allProfiles.Any(p => p.Schedule != null && p.Schedule.Count > 0 && ScheduleLogic.IsActive(p.Schedule, localNow));
        bool anyHardLocked = anySessionLocked || anyScheduleLocked;
        if (_activeMeditationSession != null &&
            (_activeMeditationSession.LockMode == LockMode.HardLocked || _activeMeditationSession.LockMode == LockMode.Locked))
        {
            anyHardLocked = true;
        }

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
        bool isLockedMeditation = _activeMeditationSession != null &&
            (_activeMeditationSession.LockMode == LockMode.HardLocked || _activeMeditationSession.LockMode == LockMode.Locked);
        bool isLockedActive = _trackedSessions.Values.Any(t => t.Session.Locked) || isLockedMeditation;
        var activeSessions = _trackedSessions.Values.Select(t => t.Session).ToList();
        var activeProfiles = allProfiles.Where(p => ProfileStateLogic.IsProfileActiveWithSessions(p, activeSessions, localNow, out _)).ToList();
        var currentPids = new HashSet<int>();
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        bool hasBlockedApps = currentBlockedList != null && currentBlockedList.Count > 0;
        bool hasUnauthorizedBrowsers = unauthorizedBrowsers != null && unauthorizedBrowsers.Count > 0;
        var killedBrowsersThisPass = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < runningProcesses.Count; i++)
        {
            var process = runningProcesses[i];
            try
            {
                int processId = process.Id;
                currentPids.Add(processId);

                // Self-safety: never kill current process or its parent/ancestor processes
                if (_protectedPids.Contains(processId))
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

                        string msg = $"[{timestamp}] [KILLED] [EXTENSION REQUIRED] Process '{exeName}' terminated: {specificReason}";
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
                    bool nameMatchesBlocked = hasBlockedApps && ProcessMatcher.IsBlocked(processName, currentBlockedList);
                    bool nameMatchesAntiKill = isLockedActive && AntiKillTools.Any(t => t.Equals(processName, StringComparison.OrdinalIgnoreCase));

                    if (!nameMatchesBlocked && !nameMatchesAntiKill && isLockedActive)
                    {
                        originalFileName = GetCachedOriginalFileName(processId);
                    }

                    bool isAntiKillTool = isLockedActive && (nameMatchesAntiKill || (originalFileName != null && AntiKillTools.Any(t => t.Equals(originalFileName, StringComparison.OrdinalIgnoreCase))));
                    bool isBlockedApp = nameMatchesBlocked || (hasBlockedApps && originalFileName != null && ProcessMatcher.IsBlocked(processName, currentBlockedList, originalFileName));

                    if (isAntiKillTool || isBlockedApp)
                    {
                        try
                        {
                            using var targetProc = Process.GetProcessById(processId);
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
                                string killMsg = $"[{timestamp}] [KILLED]{killReason} {details} terminated.";
                                Console.WriteLine(killMsg);
                                _logger.LogInformation("{Message}", killMsg);
                            }
                        }
                        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                        {
                            if (_loggedFailurePids.Add(processId))
                            {
                                string failMsg = $"[{timestamp}] [FAILED] Failed to terminate process '{processName}' (PID: {processId}): {ex.Message}";
                                Console.WriteLine(failMsg);
                                _logger.LogWarning("{Message}", failMsg);
                            }
                        }
                    }
                }

                // 3. Meditation Mode Whitelist enforcement
                if (_activeMeditationSession != null)
                {
                    var allowedApps = _activeMeditationSession.GetAllowedApps() ?? new List<string>();
                    bool isAppPermitted = IsAppAllowed(processName, exeName, allowedApps);
                    if (!isAppPermitted &&
                        !ProtectedProcesses.IsProtected(processName) &&
                        !ProtectedProcesses.IsProtected(exeName))
                    {
                        try
                        {
                            using var targetProc = Process.GetProcessById(processId);
                            IntPtr hwnd = IntPtr.Zero;
                            try { hwnd = targetProc.MainWindowHandle; } catch { }

                            // Only suppress processes that have a top-level GUI window
                            if (hwnd != IntPtr.Zero)
                            {
                                ShowWindow(hwnd, SW_FORCEMINIMIZE);
                                ShowWindow(hwnd, SW_MINIMIZE);

                                try
                                {
                                    if (!targetProc.CloseMainWindow())
                                    {
                                        targetProc.Kill(entireProcessTree: true);
                                    }
                                }
                                catch
                                {
                                    try { targetProc.Kill(); } catch { }
                                }

                                if (_loggedMeditationPids.Add(processId))
                                {
                                    string msg = $"[{timestamp}] [MEDITATION WHITELIST] Process '{exeName}' closed/minimized (not in allowed meditation apps).";
                                    Console.WriteLine(msg);
                                    _logger.LogInformation("{Message}", msg);

                                    bool dispatched = ServiceNotificationDispatcher.DispatchNotification("MeditationWhitelist", exeName, "Allowed Apps Whitelist");
                                    _db.RecordBlockEvent(new BlockEvent
                                    {
                                        TimeUtc = DateTime.UtcNow,
                                        Kind = "MeditationWhitelist",
                                        Name = exeName,
                                        ProfileName = "Meditation Whitelist",
                                        ServiceDispatched = dispatched
                                    });
                                }
                            }
                        }
                        catch { }
                        continue;
                    }
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
        foreach (var pid in _pidOriginalFileNameCache.Keys)
        {
            if (!currentPids.Contains(pid))
            {
                _pidOriginalFileNameCache.TryRemove(pid, out _);
            }
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
            using var process = Process.GetProcessById(processId);
            string? filePath = process.MainModule?.FileName;
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

    private static bool IsAppAllowed(string processName, string exeName, List<string> allowedApps)
    {
        string normProc = ProcessMatcher.NormalizeProcessName(processName);
        string normExe = ProcessMatcher.NormalizeProcessName(exeName);

        foreach (var allowed in allowedApps)
        {
            string normAllowed = ProcessMatcher.NormalizeProcessName(allowed);
            if (!string.IsNullOrEmpty(normProc) && string.Equals(normProc, normAllowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(normExe) && string.Equals(normExe, normAllowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
                                schedule.Soundscape);

                            _activeMeditationSession = started;

                            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                            string msg = $"[{ts}] [MEDITATION START] Scheduled meditation session {started.Id} started ({schedule.DurationSeconds / 60}m).";
                            Console.WriteLine(msg);
                            _logger.LogInformation("{Message}", msg);

                            ServiceNotificationDispatcher.DispatchNotification("MeditationStart", "Meditation Started", $"Meditation session started ({schedule.DurationSeconds / 60}m).");
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
