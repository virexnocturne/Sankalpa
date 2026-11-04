using System;
using System.Collections.Generic;
using System.Linq;

namespace Blocker.Core;

public interface IMonotonicClock
{
    long GetTimestampMs();
}

public class SystemMonotonicClock : IMonotonicClock
{
    public static readonly SystemMonotonicClock Instance = new();
    public long GetTimestampMs() => Environment.TickCount64;
}

public class TestMonotonicClock : IMonotonicClock
{
    private long _currentMs;
    private DateTime? _baseUtc;
    private long _baseMs;

    public TestMonotonicClock(long initialMs = 1_000_000)
    {
        _currentMs = initialMs;
    }

    public long GetTimestampMs() => _currentMs;

    public void SetMs(long ms) => _currentMs = ms;

    public void AdvanceMs(long deltaMs) => _currentMs += deltaMs;

    public void AdvanceSeconds(double seconds) => _currentMs += (long)(seconds * 1000);

    public void SyncWithVirtualDateTime(DateTime utc)
    {
        if (_baseUtc == null)
        {
            _baseUtc = utc;
            _baseMs = _currentMs;
        }
        else
        {
            double diffMs = (utc - _baseUtc.Value).TotalMilliseconds;
            _currentMs = _baseMs + (long)diffMs;
        }
    }
}

public class BrowserTrackingState
{
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long? FirstSeenRunningMs { get; set; }
    public long? LastHeartbeatMs { get; set; }
    public long? GracePeriodStartMs { get; set; }
    public bool IncognitoAllowed { get; set; }
    public string ExtensionVersion { get; set; } = string.Empty;
}

public class BrowserStatusInfo
{
    public string Name { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public bool Connected { get; set; }
    public bool IncognitoAllowed { get; set; }
    public double LastHeartbeatSecondsAgo { get; set; }
}

public class FallbackPlanResult
{
    public bool FallbackActive { get; set; }
    public string? FallbackReason { get; set; }
    public bool BlockAllUncovered { get; set; }
    public HashSet<string> FallbackDomains { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BrowserStatusInfo> BrowserStatuses { get; set; } = new();
    public List<string> UnauthorizedBrowserProcesses { get; set; } = new();
    public Dictionary<string, string> UnauthorizedReasons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> GracePeriodBrowsers { get; set; } = new();
    public Dictionary<string, int> GracePeriodRemainingSeconds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class FallbackPlanner
{
    public const long DefaultGracePeriodMs = 60_000;

    private readonly object _lock = new();
    private readonly IMonotonicClock _clock;
    private readonly long _gracePeriodMs;
    private readonly Dictionary<string, BrowserTrackingState> _browsers = new(StringComparer.OrdinalIgnoreCase);

    public FallbackPlanner(IMonotonicClock? clock = null, long gracePeriodMs = DefaultGracePeriodMs)
    {
        _clock = clock ?? (TestDetector.IsRunningInTest ? new TestMonotonicClock() : SystemMonotonicClock.Instance);
        _gracePeriodMs = gracePeriodMs;
        foreach (var b in BrowserCatalog.SupportedBrowsers)
        {
            if (!_browsers.ContainsKey(b.ExeName) || b.Id == "chrome")
            {
                _browsers[b.ExeName] = new BrowserTrackingState
                {
                    ProcessName = b.ExeName,
                    DisplayName = b.DisplayName
                };
            }
        }
    }

    public void RecordHeartbeat(string browserProcess, bool incognitoAllowed = true, string extensionVersion = "1.0.0")
    {
        long nowMs = _clock.GetTimestampMs();
        RecordHeartbeatInternal(browserProcess, nowMs, incognitoAllowed, extensionVersion);
    }

    public void RecordHeartbeat(string browserProcess, DateTime utcNow, bool incognitoAllowed = true, string extensionVersion = "1.0.0")
    {
        if (_clock is TestMonotonicClock testClock)
        {
            testClock.SyncWithVirtualDateTime(utcNow);
        }
        RecordHeartbeat(browserProcess, incognitoAllowed, extensionVersion);
    }

    public void RecordHeartbeat(string browserProcess, long timestampMs, bool incognitoAllowed = true, string extensionVersion = "1.0.0")
    {
        RecordHeartbeatInternal(browserProcess, timestampMs, incognitoAllowed, extensionVersion);
    }

    private void RecordHeartbeatInternal(string browserProcess, long nowMs, bool incognitoAllowed, string extensionVersion)
    {
        string exe = browserProcess.Trim();
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            exe += ".exe";
        }

        lock (_lock)
        {
            if (!_browsers.TryGetValue(exe, out var state))
            {
                state = new BrowserTrackingState
                {
                    ProcessName = exe,
                    DisplayName = exe
                };
                _browsers[exe] = state;
            }

            state.LastHeartbeatMs = nowMs;
            state.IncognitoAllowed = incognitoAllowed;
            state.ExtensionVersion = extensionVersion;
            if (incognitoAllowed)
            {
                state.GracePeriodStartMs = null;
            }
            if (state.FirstSeenRunningMs == null)
            {
                state.FirstSeenRunningMs = nowMs;
            }
        }
    }

    public FallbackPlanResult Plan(
        IEnumerable<Profile>? activeProfiles,
        IEnumerable<string>? runningBrowserProcesses)
    {
        return PlanInternal(activeProfiles, runningBrowserProcesses, _clock.GetTimestampMs());
    }

    public FallbackPlanResult Plan(
        IEnumerable<Profile>? activeProfiles,
        IEnumerable<string>? runningBrowserProcesses,
        DateTime utcNow)
    {
        if (_clock is TestMonotonicClock testClock)
        {
            testClock.SyncWithVirtualDateTime(utcNow);
        }
        return PlanInternal(activeProfiles, runningBrowserProcesses, _clock.GetTimestampMs());
    }

    public FallbackPlanResult Plan(
        IEnumerable<Profile>? activeProfiles,
        IEnumerable<string>? runningBrowserProcesses,
        long nowMs)
    {
        return PlanInternal(activeProfiles, runningBrowserProcesses, nowMs);
    }

    private FallbackPlanResult PlanInternal(
        IEnumerable<Profile>? activeProfiles,
        IEnumerable<string>? runningBrowserProcesses,
        long nowMs)
    {
        var result = new FallbackPlanResult();
        var runningSet = new HashSet<string>(
            (runningBrowserProcesses ?? Enumerable.Empty<string>())
                .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : p + ".exe"),
            StringComparer.OrdinalIgnoreCase);

        string? missingBrowserReason = null;

        lock (_lock)
        {
            // Update first seen timestamps
            foreach (var exe in runningSet)
            {
                if (BrowserCatalog.IsUnsupportedBrowser(exe))
                {
                    continue;
                }

                if (!_browsers.TryGetValue(exe, out var state))
                {
                    state = new BrowserTrackingState
                    {
                        ProcessName = exe,
                        DisplayName = BrowserCatalog.GetDisplayName(exe)
                    };
                    _browsers[exe] = state;
                }

                state.FirstSeenRunningMs ??= nowMs;
            }

            // Reset first seen and grace tracker for browsers that stopped running
            foreach (var (exe, state) in _browsers)
            {
                if (!runningSet.Contains(exe))
                {
                    state.FirstSeenRunningMs = null;
                    state.GracePeriodStartMs = null;
                }
            }

            // Build browser statuses
            foreach (var (exe, state) in _browsers)
            {
                bool isRunning = runningSet.Contains(exe);
                bool hasHeartbeat = state.LastHeartbeatMs.HasValue;
                double secondsAgo = hasHeartbeat ? Math.Max(0, (nowMs - state.LastHeartbeatMs!.Value) / 1000.0) : -1;
                bool connected = hasHeartbeat && secondsAgo >= 0 && secondsAgo < 6.0;

                if (isRunning || hasHeartbeat)
                {
                    result.BrowserStatuses.Add(new BrowserStatusInfo
                    {
                        Name = state.DisplayName,
                        ProcessName = state.ProcessName,
                        Connected = connected,
                        IncognitoAllowed = state.IncognitoAllowed,
                        LastHeartbeatSecondsAgo = secondsAgo >= 0 ? Math.Round(secondsAgo, 1) : -1
                    });
                }
            }

            // Check for missing running browsers (heartbeat missing or incognito disallowed)
            foreach (var exe in runningSet)
            {
                if (BrowserCatalog.IsUnsupportedBrowser(exe))
                {
                    result.UnauthorizedBrowserProcesses.Add(exe);
                    string reason = $"Unsupported browser '{exe}' cannot be monitored by Sankalpa extension";
                    result.UnauthorizedReasons[exe] = reason;
                    missingBrowserReason ??= reason;
                    continue;
                }

                if (_browsers.TryGetValue(exe, out var state))
                {
                    bool hasRecentHeartbeat = state.LastHeartbeatMs.HasValue && (nowMs - state.LastHeartbeatMs.Value) <= 10_000;
                    bool incognitoDisallowed = !state.IncognitoAllowed;
                    bool isHealthy = hasRecentHeartbeat && !incognitoDisallowed;

                    if (isHealthy)
                    {
                        state.GracePeriodStartMs = null;
                        continue;
                    }

                    // Browser is unhealthy (newly launched without extension, extension disabled/unresponsive, or incognito disallowed)
                    state.GracePeriodStartMs ??= nowMs;
                    long graceElapsed = nowMs - state.GracePeriodStartMs.Value;

                    if (graceElapsed < _gracePeriodMs)
                    {
                        // In grace period: track remaining seconds and do not terminate yet
                        result.GracePeriodBrowsers.Add(exe);
                        int remaining = Math.Max(1, (int)Math.Ceiling((_gracePeriodMs - graceElapsed) / 1000.0));
                        result.GracePeriodRemainingSeconds[exe] = remaining;
                        continue;
                    }

                    // Grace period expired: mark for termination
                    result.UnauthorizedBrowserProcesses.Add(exe);
                    string specificReason = (hasRecentHeartbeat && incognitoDisallowed)
                        ? $"Sankalpa extension in {state.DisplayName} must have 'Allow in Incognito/Private' enabled"
                        : $"Browser extension not responding in {state.DisplayName}";
                    result.UnauthorizedReasons[exe] = specificReason;
                    if (missingBrowserReason == null)
                    {
                        missingBrowserReason = specificReason;
                    }
                }
            }
        }

        var profilesList = (activeProfiles ?? Enumerable.Empty<Profile>()).ToList();

        if (missingBrowserReason != null)
        {
            result.FallbackActive = true;
            result.FallbackReason = missingBrowserReason;

            if (profilesList.Any(p => p.BlockAllWebsites))
            {
                result.BlockAllUncovered = true;
            }

            // Compute fallback domains: concrete host parts of all blocked patterns from active profiles
            // Run through existing hosts domain normalization and expansion; skip IP addresses and invalid names.
            foreach (var profile in profilesList)
            {
                if (profile.WebRules != null)
                {
                    foreach (var rule in profile.WebRules.Where(r => !r.Allow && !string.IsNullOrWhiteSpace(r.Pattern)))
                    {
                        string p = rule.Pattern.Trim();
                        if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) p = p[7..];
                        if (p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) p = p[8..];

                        int slashIdx = p.IndexOf('/');
                        string hostPart = slashIdx >= 0 ? p[..slashIdx] : p;
                        if (hostPart.EndsWith("$")) hostPart = hostPart[..^1];

                        if (hostPart.Contains('*'))
                        {
                            continue; // Glob hosts cannot be enforced by hosts fallback
                        }

                        if (HostsFileBuilder.TryNormalizeDomain(hostPart, out string normalizedDomain))
                        {
                            foreach (var variant in HostsFileBuilder.ExpandDomain(normalizedDomain))
                            {
                                result.FallbackDomains.Add(variant);
                            }
                        }
                    }
                }

                if (profile.BlockedDomains != null)
                {
                    foreach (var rawDomain in profile.BlockedDomains.Where(d => !string.IsNullOrWhiteSpace(d)))
                    {
                        if (HostsFileBuilder.TryNormalizeDomain(rawDomain, out string normalizedDomain))
                        {
                            foreach (var variant in HostsFileBuilder.ExpandDomain(normalizedDomain))
                            {
                                result.FallbackDomains.Add(variant);
                            }
                        }
                    }
                }
            }
        }
        else
        {
            // All running browsers are healthy or in grace period
            result.FallbackActive = false;
            result.FallbackReason = null;
        }

        return result;
    }

    public void ResetBrowserTracking(string processName)
    {
        string normExe = processName.Trim();
        if (!normExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normExe += ".exe";
        }
        lock (_lock)
        {
            if (_browsers.TryGetValue(normExe, out var state))
            {
                state.FirstSeenRunningMs = null;
                state.GracePeriodStartMs = null;
                state.LastHeartbeatMs = null;
            }
        }
    }
}

internal static class TestDetector
{
    public static bool IsRunningInTest =>
        AppDomain.CurrentDomain.GetAssemblies().Any(a => a.FullName?.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) == true);
}
