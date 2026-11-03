using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Blocker.Core;

namespace Blocker.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly BlockerDatabase _db;
    private readonly HostsFileManager _hostsManager;

    private BlockSession? _activeSession;
    private Profile? _activeProfile;
    private Stopwatch? _sessionStopwatch;
    private TimeSpan _sessionInitialRemaining = TimeSpan.Zero;
    private DateTime _sessionBaseSystemTime = DateTime.MinValue;

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
            var session = _db.GetActiveOrUnendedSession();
            if (session != null)
            {
                DateTime nowUtc = DateTime.UtcNow;
                double remainingSeconds;

                if (BlockSessionLogic.HasClockMovedBackwards(session, nowUtc))
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    string warnMsg = $"[{timestamp}] [WARNING] System clock moved backwards into the past (StartUtc: {session.StartUtc:O}, NowUtc: {nowUtc:O}). Clock manipulation detected.";
                    Console.WriteLine(warnMsg);
                    _logger.LogWarning("{Message}", warnMsg);

                    // Clamp remaining time to session.DurationSeconds and adjust session.StartUtc to DateTime.UtcNow
                    session.StartUtc = nowUtc;
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

                        if (session.Locked)
                        {
                            SecurityHardener.UnlockCurrentProcess(out _);
                            SecurityHardener.UnlockService(out _);
                        }

                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        string msg = session.Locked
                            ? $"[{timestamp}] Previous locked session completed while the machine was powered down."
                            : $"[{timestamp}] Previous session expired during downtime.";
                        Console.WriteLine(msg);
                        _logger.LogInformation("{Message}", msg);
                    }
                }
                else
                {
                    _activeSession = session;
                    _activeProfile = _db.GetProfileById(session.ProfileId);
                    _sessionInitialRemaining = TimeSpan.FromSeconds(remainingSeconds);
                    _sessionStopwatch = Stopwatch.StartNew();
                    _sessionBaseSystemTime = DateTime.UtcNow;

                    if (session.Locked && !SecurityHardener.IsLockedDown)
                    {
                        SecurityHardener.LockdownCurrentProcess(out var secErr);
                        SecurityHardener.LockdownService(out _);
                        if (secErr == null)
                        {
                            string secMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SECURITY] Sankalpa process & service DACLs locked down on boot. Terminate and stop access denied.";
                            Console.WriteLine(secMsg);
                            _logger.LogInformation("{Message}", secMsg);
                        }
                    }

                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    string msg = $"[{timestamp}] Restored active session on startup: Profile='{_activeProfile?.Name}', Remaining={remainingSeconds:F0}s (Locked: {session.Locked}).";
                    Console.WriteLine(msg);
                    _logger.LogInformation("{Message}", msg);
                }
            }
            else
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string msg = $"[{timestamp}] No active manual block session found on startup.";
                Console.WriteLine(msg);
                _logger.LogInformation("{Message}", msg);
            }

            // Enforce active union (scheduled profiles + restored manual session) on startup
            DateTime localNow = DateTime.Now;
            var allProfiles = _db.ListProfiles();
            var activeProfiles = allProfiles.Where(p => ProfileStateLogic.IsProfileActive(p, _activeSession, localNow, out _)).ToList();

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

                // Compute active profile set and enforce union of apps and hosts
                DateTime localNow = DateTime.Now;
                var allProfiles = _db.ListProfiles();
                var activeProfiles = allProfiles.Where(p => ProfileStateLogic.IsProfileActive(p, _activeSession, localNow, out _)).ToList();
                var (unionApps, _) = ProfileStateLogic.ComputeActiveUnion(allProfiles, _activeSession, localNow);

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
                    b.StartsWith(gb, StringComparison.OrdinalIgnoreCase) || b.StartsWith(gb + ".exe", StringComparison.OrdinalIgnoreCase)));

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

                _activeSession = null;
                _activeProfile = null;
                _sessionStopwatch = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to process EMERGENCY_UNLOCK: {Message}", ex.Message);
            }
        }
    }

    private void SyncSessionState()
    {
        var dbSession = _db.GetActiveSession();

        if (_activeSession == null && dbSession != null)
        {
            // A new session was started via CLI / UI
            DateTime nowUtc = DateTime.UtcNow;
            double remainingSeconds;
            if (nowUtc < dbSession.StartUtc)
            {
                remainingSeconds = dbSession.DurationSeconds;
            }
            else
            {
                TimeSpan elapsed = nowUtc - dbSession.StartUtc;
                remainingSeconds = dbSession.DurationSeconds - elapsed.TotalSeconds;
            }

            if (remainingSeconds > 0)
            {
                _activeSession = dbSession;
                _activeProfile = _db.GetProfileById(dbSession.ProfileId);
                _sessionInitialRemaining = TimeSpan.FromSeconds(remainingSeconds);
                _sessionStopwatch = Stopwatch.StartNew();
                _sessionBaseSystemTime = DateTime.UtcNow;

                if (dbSession.Locked && !SecurityHardener.IsLockedDown)
                {
                    SecurityHardener.LockdownCurrentProcess(out var secErr);
                    SecurityHardener.LockdownService(out _);
                    if (secErr == null)
                    {
                        string secMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SECURITY] Sankalpa process & service DACLs locked down. Terminate and stop access denied.";
                        Console.WriteLine(secMsg);
                        _logger.LogInformation("{Message}", secMsg);
                    }
                }

                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string msg = $"[{ts}] Session started: Profile='{_activeProfile?.Name}', Remaining={remainingSeconds:F0}s (Locked: {dbSession.Locked}).";
                Console.WriteLine(msg);
                _logger.LogInformation("{Message}", msg);
            }
        }
        else if (_activeSession != null)
        {
            var currentInDb = _db.GetSessionById(_activeSession.Id);
            if (currentInDb == null || currentInDb.EndedUtc != null)
            {
                if (SecurityHardener.IsLockedDown)
                {
                    SecurityHardener.UnlockCurrentProcess(out _);
                    SecurityHardener.UnlockService(out _);
                    string secMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SECURITY] Sankalpa process & service DACLs restored.";
                    Console.WriteLine(secMsg);
                    _logger.LogInformation("{Message}", secMsg);
                }

                // Session was ended via CLI or externally
                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string msg = $"[{ts}] Active session for '{_activeProfile?.Name}' has been stopped.";
                Console.WriteLine(msg);
                _logger.LogInformation("{Message}", msg);

                _activeSession = null;
                _activeProfile = null;
                _sessionStopwatch = null;
                return;
            }

            // Monotonic clock tracking
            TimeSpan monotonicElapsed = _sessionStopwatch!.Elapsed;
            TimeSpan remaining = _sessionInitialRemaining - monotonicElapsed;

            // System clock jump / drift detection (> 5 seconds compared to monotonic clock)
            TimeSpan systemElapsed = DateTime.UtcNow - _sessionBaseSystemTime;
            double driftSeconds = Math.Abs((systemElapsed - monotonicElapsed).TotalSeconds);
            if (driftSeconds > 5.0)
            {
                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string warnMsg = $"[{ts}] [WARNING] System clock jump detected! Drift is {driftSeconds:F1}s compared to monotonic clock.";
                Console.WriteLine(warnMsg);
                _logger.LogWarning("{Message}", warnMsg);
            }

            if (remaining <= TimeSpan.Zero)
            {
                if (SecurityHardener.IsLockedDown)
                {
                    SecurityHardener.UnlockCurrentProcess(out _);
                    SecurityHardener.UnlockService(out _);
                    string secMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SECURITY] Sankalpa process & service DACLs restored.";
                    Console.WriteLine(secMsg);
                    _logger.LogInformation("{Message}", secMsg);
                }

                // Monotonic session timer completed
                _db.EndSession(_activeSession.Id);

                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string msg = $"[{ts}] Active session for '{_activeProfile?.Name}' has completed and ended.";
                Console.WriteLine(msg);
                _logger.LogInformation("{Message}", msg);

                _activeSession = null;
                _activeProfile = null;
                _sessionStopwatch = null;
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
        bool isLockedActive = _activeSession != null && _activeSession.Locked;
        var activeProfiles = allProfiles.Where(p => ProfileStateLogic.IsProfileActive(p, _activeSession, localNow, out _)).ToList();
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
                if (hasUnauthorizedBrowsers && unauthorizedBrowsers!.Contains(exeName))
                {
                    try
                    {
                        using var pObj = Process.GetProcessById(processId);
                        pObj.Kill(entireProcessTree: true);
                    }
                    catch { }

                    if (killedBrowsersThisPass.Add(exeName))
                    {
                        try
                        {
                            // Secondary sweep with taskkill to ensure any detached child workers or helper processes are terminated
                            using var proc = Process.Start(new ProcessStartInfo
                            {
                                FileName = "taskkill.exe",
                                Arguments = $"/F /T /IM \"{exeName}\"",
                                CreateNoWindow = true,
                                UseShellExecute = false
                            });
                            proc?.WaitForExit(1000);
                        }
                        catch { }

                        _loggedGraceBrowsers.RemoveWhere(b => b.StartsWith(exeName, StringComparison.OrdinalIgnoreCase) || b.StartsWith(exeName + ".exe", StringComparison.OrdinalIgnoreCase));

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
                            targetProc.Kill(entireProcessTree: true);

                            string matchingProfileName = isAntiKillTool
                                ? "Sankalpa Tamper Protection"
                                : (activeProfiles
                                    .FirstOrDefault(p => ProcessMatcher.IsBlocked(processName, p.BlockedProcesses, originalFileName))
                                    ?.Name ?? _activeProfile?.Name ?? "Default");

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
            }
            catch
            {
                // Process might have terminated between snapshot and check
            }
        }

        // Clean up PIDs for processes that have exited to prevent memory leaks and handle PID recycling
        _loggedKillPids.IntersectWith(currentPids);
        _loggedFailurePids.IntersectWith(currentPids);
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
}
