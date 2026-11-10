using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace Blocker.Core;

public enum ProcessAllowDecision
{
    Allowed = 1,
    Exempt = 2,
    MustSuspend = 3
}

/// <summary>
/// Precomputes and caches normalized allowed apps, pinned install roots, and per-PID allow decisions
/// for the hot path during meditation mode. Keyed by (PID, creation time) to completely prevent
/// PID-reuse vulnerabilities while eliminating repeated O(N*M) process evaluations and syscalls.
/// </summary>
public sealed class MeditationAllowedCache
{
    private int _sessionId = -1;
    private string? _sessionStatus;
    private DateTime _sessionStartUtc = DateTime.MinValue;
    private int _allowedAppsHash;
    private int _rootsHash;

    private readonly HashSet<string> _normalizedAllowedApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _normalizedRoots = new(StringComparer.OrdinalIgnoreCase);
    private bool _isExplorerAllowed;
    private bool _hasAllowedApps;

    private readonly ConcurrentDictionary<(int Pid, long CreationTime), ProcessAllowDecision> _decisionCache = new();
    private readonly ConcurrentDictionary<int, string> _allowedAppDirs = new();
    private readonly ConcurrentDictionary<int, (long CreationTime, string? ImagePath)> _identityCache = new();

    public bool IsExplorerAllowed => _isExplorerAllowed;
    public bool HasAllowedApps => _hasAllowedApps;
    public int ActiveSessionId => _sessionId;
    public int CachedDecisionsCount => _decisionCache.Count;

    /// <summary>
    /// Synchronizes active session parameters. If the session or allowed apps have changed,
    /// precomputed collections are rebuilt and all per-PID decision caches are invalidated.
    /// Returns true if the cache was invalidated and rebuilt.
    /// </summary>
    public bool SyncSession(
        int sessionId,
        string? status,
        DateTime startUtc,
        IReadOnlyList<string>? allowedApps,
        IReadOnlyDictionary<string, string>? allowedAppRoots)
    {
        int appsHash = ComputeListHash(allowedApps);
        int rootsHash = ComputeDictHash(allowedAppRoots);

        bool changed = sessionId != _sessionId ||
                       !string.Equals(status, _sessionStatus, StringComparison.OrdinalIgnoreCase) ||
                       startUtc != _sessionStartUtc ||
                       appsHash != _allowedAppsHash ||
                       rootsHash != _rootsHash;

        if (!changed)
        {
            return false;
        }

        _sessionId = sessionId;
        _sessionStatus = status;
        _sessionStartUtc = startUtc;
        _allowedAppsHash = appsHash;
        _rootsHash = rootsHash;

        _normalizedAllowedApps.Clear();
        _normalizedRoots.Clear();
        _decisionCache.Clear();
        _allowedAppDirs.Clear();
        _identityCache.Clear();

        if (allowedApps != null && allowedApps.Count > 0)
        {
            foreach (var app in allowedApps)
            {
                if (!string.IsNullOrWhiteSpace(app))
                {
                    _normalizedAllowedApps.Add(ProcessMatcher.NormalizeProcessName(app));
                }
            }
        }

        if (allowedAppRoots != null && allowedAppRoots.Count > 0)
        {
            foreach (var kvp in allowedAppRoots)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    _normalizedRoots[ProcessMatcher.NormalizeProcessName(kvp.Key)] = kvp.Value;
                }
            }
        }

        _isExplorerAllowed = _normalizedAllowedApps.Contains("explorer");
        _hasAllowedApps = _normalizedAllowedApps.Count > 0;

        return true;
    }

    /// <summary>
    /// Resets and clears all cached session data and decision tables.
    /// </summary>
    public void Reset()
    {
        _sessionId = -1;
        _sessionStatus = null;
        _sessionStartUtc = DateTime.MinValue;
        _allowedAppsHash = 0;
        _rootsHash = 0;
        _normalizedAllowedApps.Clear();
        _normalizedRoots.Clear();
        _decisionCache.Clear();
        _allowedAppDirs.Clear();
        _identityCache.Clear();
        _isExplorerAllowed = false;
        _hasAllowedApps = false;
    }

    /// <summary>
    /// High-performance O(1) check against precomputed normalized allowed apps and pinned roots.
    /// </summary>
    public bool IsAppAllowedFast(string processName, string exeName, string? imagePath)
    {
        string normProc = ProcessMatcher.NormalizeProcessName(processName);
        string normExe = ProcessMatcher.NormalizeProcessName(exeName);

        // Always allow Snipping Tool and Screen Clipping Host during meditation, verified against system folders if imagePath provided
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

        if (!_hasAllowedApps)
        {
            return false;
        }

        bool matchedProc = _normalizedAllowedApps.Contains(normProc);
        bool matchedExe = _normalizedAllowedApps.Contains(normExe);

        if (!matchedProc && !matchedExe)
        {
            return false;
        }

        string matchedNorm = matchedProc ? normProc : normExe;

        if (_normalizedRoots.TryGetValue(matchedNorm, out var rootDir) && !string.IsNullOrWhiteSpace(rootDir))
        {
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                return ProtectedProcesses.IsPathInsideDirectory(imagePath, rootDir);
            }
        }

        return true;
    }

    /// <summary>
    /// Evaluates or retrieves the cached allow decision for a process.
    /// Decision is strictly keyed by (PID, creationTime) to prevent PID reuse vulnerabilities.
    /// </summary>
    public bool TryGetCachedDecision(int pid, long creationTime, out ProcessAllowDecision decision)
    {
        return _decisionCache.TryGetValue((pid, creationTime), out decision);
    }

    public void CacheDecision(int pid, long creationTime, ProcessAllowDecision decision)
    {
        _decisionCache[(pid, creationTime)] = decision;
    }

    public void RecordAllowedAppDir(int pid, string directory)
    {
        _allowedAppDirs[pid] = directory;
    }

    public bool TryGetAllowedAppDir(int pid, out string? directory)
    {
        return _allowedAppDirs.TryGetValue(pid, out directory);
    }

    public bool TryGetCachedIdentity(int pid, out long creationTime, out string? imagePath)
    {
        if (_identityCache.TryGetValue(pid, out var tuple))
        {
            creationTime = tuple.CreationTime;
            imagePath = tuple.ImagePath;
            return true;
        }
        creationTime = 0;
        imagePath = null;
        return false;
    }

    public void CacheIdentity(int pid, long creationTime, string? imagePath)
    {
        _identityCache[pid] = (creationTime, imagePath);
    }

    public void PruneExited(ICollection<int> currentPids)
    {
        foreach (var key in _decisionCache.Keys)
        {
            if (!currentPids.Contains(key.Pid))
            {
                _decisionCache.TryRemove(key, out _);
            }
        }

        foreach (var pid in _allowedAppDirs.Keys)
        {
            if (!currentPids.Contains(pid))
            {
                _allowedAppDirs.TryRemove(pid, out _);
            }
        }

        foreach (var pid in _identityCache.Keys)
        {
            if (!currentPids.Contains(pid))
            {
                _identityCache.TryRemove(pid, out _);
            }
        }
    }

    private static int ComputeListHash(IReadOnlyList<string>? list)
    {
        if (list == null || list.Count == 0) return 0;
        int hash = 17;
        for (int i = 0; i < list.Count; i++)
        {
            hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(list[i]);
        }
        return hash;
    }

    private static int ComputeDictHash(IReadOnlyDictionary<string, string>? dict)
    {
        if (dict == null || dict.Count == 0) return 0;
        int hash = 19;
        foreach (var kvp in dict)
        {
            hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(kvp.Key);
            hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(kvp.Value);
        }
        return hash;
    }
}
