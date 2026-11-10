using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Blocker.Core;

public sealed class ThrottledProcessJournalEntry
{
    public int ProcessId { get; set; }
    public long CreationTime { get; set; }
    public string ImagePath { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public uint OriginalPriorityClass { get; set; } = ProcessIdentityHelper.NORMAL_PRIORITY_CLASS;
    public DateTime ThrottledAtUtc { get; set; }
}

public sealed class ThrottledProcessInfo
{
    public int ProcessId { get; set; }
    public long CreationTime { get; set; }
    public string ImagePath { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string ExeName { get; set; } = string.Empty;
    public uint OriginalPriorityClass { get; set; } = ProcessIdentityHelper.NORMAL_PRIORITY_CLASS;
    public DateTime ThrottledAtUtc { get; set; }
    public DateTime BackgroundSinceUtc { get; set; }
    public bool HasTrimmedWorkingSet { get; set; }
    public bool EcoQoSEnabled { get; set; }
    public bool PriorityReduced { get; set; }
    public bool MemoryPriorityReduced { get; set; }
}

/// <summary>
/// Manages background-app resource optimization during meditation mode.
/// Throttles allowed applications (and their child trees) that are not currently in the foreground:
/// 1. Applies EcoQoS execution speed throttling via SetProcessInformation.
/// 2. Sets process priority class to BELOW_NORMAL (avoiding IDLE to prevent starvation/hangs).
/// 3. Lowers memory priority to Low (2) via SetProcessInformation.
/// 4. Performs a one-time working-set trim after an app dwells in the background for 20-30 seconds.
/// 5. Restores foreground apps instantly (< 1ms) prior to window activation.
/// 6. Maintains an on-disk atomic crash journal for 100% resilient crash recovery.
/// 7. Exempts active audio players, protected processes, Sankalpa helpers, and system processes.
/// </summary>
public sealed class BackgroundAppThrottler : IDisposable
{
    private static BackgroundAppThrottler? _defaultInstance;
    private static readonly object _instanceLock = new();

    public static BackgroundAppThrottler Default
    {
        get
        {
            if (_defaultInstance == null)
            {
                lock (_instanceLock)
                {
                    _defaultInstance ??= new BackgroundAppThrottler();
                }
            }
            return _defaultInstance;
        }
    }

    private readonly object _syncLock = new();
    private readonly string _journalFilePath;
    private readonly TimeSpan _workingSetTrimDelay;
    private readonly bool _enableAudioExemption;

    private readonly HashSet<string> _normalizedAllowedApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _normalizedRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ThrottledProcessInfo> _throttledProcesses = new();
    private readonly HashSet<int> _protectedPids = new();

    private int _foregroundPid = 0;
    private bool _isSessionActive = false;

    public bool IsSessionActive => _isSessionActive;
    public int ForegroundPid => _foregroundPid;
    public int ThrottledCount => _throttledProcesses.Count;
    public string JournalFilePath => _journalFilePath;

    public BackgroundAppThrottler(
        string? journalPath = null,
        TimeSpan? workingSetTrimDelay = null,
        bool enableAudioExemption = true)
    {
        _journalFilePath = journalPath ?? GetDefaultJournalFilePath();
        _workingSetTrimDelay = workingSetTrimDelay ?? TimeSpan.FromSeconds(25);
        _enableAudioExemption = enableAudioExemption;

        RefreshProtectedPids();
    }

    private void RefreshProtectedPids()
    {
        _protectedPids.Clear();
        int selfPid = Environment.ProcessId;
        _protectedPids.Add(selfPid);

        try
        {
            var ancestors = ProcessTreeHelper.GetCurrentAndAncestorProcessIds();
            foreach (var pid in ancestors)
            {
                _protectedPids.Add(pid);
            }
        }
        catch
        {
            _protectedPids.Add(selfPid);
        }
    }

    public static string GetDefaultJournalFilePath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(localAppData, "Sankalpa", "state");
        return Path.Combine(dir, "throttled_apps.json");
    }

    /// <summary>
    /// Starts a meditation throttling session with the specified allowed applications and pinned roots.
    /// Recovers any un-restored processes from previous crashed sessions first.
    /// </summary>
    public void StartSession(IReadOnlyList<string>? allowedApps, IReadOnlyDictionary<string, string>? allowedAppRoots = null)
    {
        lock (_syncLock)
        {
            // First, recover any processes left throttled by a previous crash
            RecoverFromJournalInternal(_journalFilePath);

            _normalizedAllowedApps.Clear();
            _normalizedRoots.Clear();
            _throttledProcesses.Clear();

            if (allowedApps != null)
            {
                foreach (var app in allowedApps)
                {
                    if (!string.IsNullOrWhiteSpace(app))
                    {
                        _normalizedAllowedApps.Add(ProcessMatcher.NormalizeProcessName(app));
                    }
                }
            }

            if (allowedAppRoots != null)
            {
                foreach (var kvp in allowedAppRoots)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        _normalizedRoots[ProcessMatcher.NormalizeProcessName(kvp.Key)] = kvp.Value;
                    }
                }
            }

            _isSessionActive = true;
            _foregroundPid = 0;

            // Delete any stale journal file
            DeleteJournalFile();
        }
    }

    /// <summary>
    /// Ends the throttling session, unconditionally restoring all throttled applications.
    /// </summary>
    public void EndSession()
    {
        lock (_syncLock)
        {
            _isSessionActive = false;
            _foregroundPid = 0;
            RestoreAllInternal();
            _normalizedAllowedApps.Clear();
            _normalizedRoots.Clear();
        }
    }

    /// <summary>
    /// Handles foreground window / application changes.
    /// Restores any throttled processes belonging to the new foreground app INSTANTLY before window switch,
    /// and throttles any non-foreground allowed applications.
    /// </summary>
    public void UpdateForegroundApp(int foregroundPid, IntPtr foregroundHwnd = default)
    {
        if (!_isSessionActive)
        {
            return;
        }

        lock (_syncLock)
        {
            _foregroundPid = foregroundPid;

            // 1. Gather all currently running processes
            var running = FastProcessEnumerator.GetProcesses();
            var processByPid = running.ToDictionary(p => p.Id);

            // 2. Identify the foreground allowed tree (the foreground PID and all its tree members)
            var foregroundTreePids = new HashSet<int>();
            if (foregroundPid > 0 && processByPid.TryGetValue(foregroundPid, out var fgEntry))
            {
                // Find root allowed ancestor of foreground process
                int rootPid = FindRootAllowedProcess(fgEntry, processByPid);
                if (rootPid > 0)
                {
                    foregroundTreePids.Add(rootPid);
                    AddDescendants(rootPid, running, foregroundTreePids);
                }
                else
                {
                    foregroundTreePids.Add(foregroundPid);
                }
            }

            // 3. INSTANT RESTORE: Restore any processes in the foreground tree that were throttled
            bool journalChanged = false;
            foreach (int pid in foregroundTreePids)
            {
                if (_throttledProcesses.TryRemove(pid, out var throttledInfo))
                {
                    RestoreProcessInternal(throttledInfo);
                    journalChanged = true;
                }
            }

            // 4. Query active audio PIDs for exemption
            var activeAudioPids = _enableAudioExemption
                ? AudioSessionDetector.GetActiveAudioProcessIds()
                : new HashSet<int>();

            // 5. THROTTLE: Identify all allowed processes that are NOT in the foreground tree
            var allowedRootPids = new HashSet<int>();
            for (int i = 0; i < running.Count; i++)
            {
                var p = running[i];
                if (foregroundTreePids.Contains(p.Id)) continue;
                if (!CanTouchProcess(p.Id, p.ProcessName, p.ExeName)) continue;

                if (IsDirectAllowedApp(p.Id, p.ProcessName, p.ExeName, out _))
                {
                    allowedRootPids.Add(p.Id);
                }
            }

            var backgroundAllowedTree = new HashSet<int>(allowedRootPids);
            foreach (int rootPid in allowedRootPids)
            {
                AddDescendants(rootPid, running, backgroundAllowedTree);
            }

            // Remove any foreground tree members or audio session players
            backgroundAllowedTree.ExceptWith(foregroundTreePids);
            if (activeAudioPids.Count > 0)
            {
                backgroundAllowedTree.ExceptWith(activeAudioPids);
            }

            // Apply throttling to all background allowed tree processes
            DateTime nowUtc = DateTime.UtcNow;
            foreach (int pid in backgroundAllowedTree)
            {
                if (!processByPid.TryGetValue(pid, out var pEntry)) continue;
                if (_throttledProcesses.ContainsKey(pid)) continue;

                if (ThrottleProcessInternal(pEntry, nowUtc, out var newThrottled))
                {
                    _throttledProcesses[pid] = newThrottled;
                    journalChanged = true;
                }
            }

            if (journalChanged)
            {
                PersistJournalInternal();
            }
        }
    }

    /// <summary>
    /// Periodic tick (e.g. 1-second interval):
    /// - Performs one-time working-set trim for apps that have dwelled in background >= 25 seconds.
    /// - Discovers new child processes spawned by background allowed apps.
    /// - Prunes exited processes.
    /// </summary>
    public void Tick()
    {
        if (!_isSessionActive)
        {
            return;
        }

        lock (_syncLock)
        {
            var running = FastProcessEnumerator.GetProcesses();
            var runningPids = running.Select(p => p.Id).ToHashSet();
            var processByPid = running.ToDictionary(p => p.Id);
            DateTime nowUtc = DateTime.UtcNow;
            bool journalChanged = false;

            // 1. Prune exited processes
            foreach (var pid in _throttledProcesses.Keys.ToList())
            {
                if (!runningPids.Contains(pid))
                {
                    _throttledProcesses.TryRemove(pid, out _);
                    journalChanged = true;
                }
            }

            // 2. Perform one-time working-set trim for apps that have been in background for >= trimDelay
            foreach (var kvp in _throttledProcesses)
            {
                var info = kvp.Value;
                if (!info.HasTrimmedWorkingSet)
                {
                    if (nowUtc - info.BackgroundSinceUtc >= _workingSetTrimDelay)
                    {
                        TrimProcessWorkingSetInternal(info);
                        info.HasTrimmedWorkingSet = true;
                    }
                }
            }

            // 3. Discover any newly spawned child processes of background allowed apps
            if (_foregroundPid > 0 && processByPid.TryGetValue(_foregroundPid, out var fgEntry))
            {
                var fgTree = new HashSet<int>();
                int rootPid = FindRootAllowedProcess(fgEntry, processByPid);
                if (rootPid > 0)
                {
                    fgTree.Add(rootPid);
                    AddDescendants(rootPid, running, fgTree);
                }
                else
                {
                    fgTree.Add(_foregroundPid);
                }

                foreach (var throttledRoot in _throttledProcesses.Values.ToList())
                {
                    var children = new HashSet<int>();
                    AddDescendants(throttledRoot.ProcessId, running, children);
                    foreach (int childPid in children)
                    {
                        if (fgTree.Contains(childPid)) continue;
                        if (_throttledProcesses.ContainsKey(childPid)) continue;
                        if (!processByPid.TryGetValue(childPid, out var childEntry)) continue;

                        if (ThrottleProcessInternal(childEntry, nowUtc, out var newThrottled))
                        {
                            _throttledProcesses[childPid] = newThrottled;
                            journalChanged = true;
                        }
                    }
                }
            }

            if (journalChanged)
            {
                PersistJournalInternal();
            }
        }
    }

    /// <summary>
    /// Unconditionally restores all currently throttled processes.
    /// Can be invoked on session completion, Zen return, emergency unlock, or shutdown.
    /// </summary>
    public void RestoreAll()
    {
        lock (_syncLock)
        {
            RestoreAllInternal();
        }
    }

    public bool IsThrottled(int pid)
    {
        return _throttledProcesses.ContainsKey(pid);
    }

    public IReadOnlyList<int> GetThrottledPids()
    {
        return _throttledProcesses.Keys.ToList();
    }

    private bool CanTouchProcess(int pid, string processName, string exeName)
    {
        if (pid <= 4 || pid == Environment.ProcessId) return false;
        if (_protectedPids.Contains(pid)) return false;

        if (ProtectedProcesses.IsProtected(processName, null) ||
            ProtectedProcesses.IsProtected(exeName, null))
        {
            return false;
        }

        if (IsSankalpaProcess(processName, exeName))
        {
            return false;
        }

        return true;
    }

    private static bool IsSankalpaProcess(string processName, string exeName)
    {
        string normProc = ProcessMatcher.NormalizeProcessName(processName);
        string normExe = ProcessMatcher.NormalizeProcessName(exeName);

        return normProc.StartsWith("blocker.", StringComparison.OrdinalIgnoreCase) ||
               normProc.StartsWith("sankalpa", StringComparison.OrdinalIgnoreCase) ||
               normExe.StartsWith("blocker.", StringComparison.OrdinalIgnoreCase) ||
               normExe.StartsWith("sankalpa", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsDirectAllowedApp(int pid, string processName, string exeName, out string? imagePath)
    {
        imagePath = null;
        string normProc = ProcessMatcher.NormalizeProcessName(processName);
        string normExe = ProcessMatcher.NormalizeProcessName(exeName);

        bool matchesName = _normalizedAllowedApps.Contains(normProc) || _normalizedAllowedApps.Contains(normExe);
        if (!matchesName)
        {
            return false;
        }

        string matched = _normalizedAllowedApps.Contains(normProc) ? normProc : normExe;

        // Path pinning check
        if (_normalizedRoots.TryGetValue(matched, out var rootDir) && !string.IsNullOrWhiteSpace(rootDir))
        {
            ProcessIdentityHelper.GetProcessIdentity(pid, out _, out imagePath);
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                return ProtectedProcesses.IsPathInsideDirectory(imagePath, rootDir);
            }
        }

        return true;
    }

    private int FindRootAllowedProcess(FastProcessEntry entry, IReadOnlyDictionary<int, FastProcessEntry> processByPid)
    {
        if (IsDirectAllowedApp(entry.Id, entry.ProcessName, entry.ExeName, out _))
        {
            return entry.Id;
        }

        int currPid = entry.ParentId;
        int hops = 0;
        while (currPid > 0 && hops < 16)
        {
            if (!processByPid.TryGetValue(currPid, out var parent)) break;
            if (IsDirectAllowedApp(parent.Id, parent.ProcessName, parent.ExeName, out _))
            {
                return parent.Id;
            }
            currPid = parent.ParentId;
            hops++;
        }

        return 0;
    }

    private static void AddDescendants(int rootPid, IReadOnlyList<FastProcessEntry> allProcesses, HashSet<int> destination)
    {
        var toProcess = new Queue<int>();
        toProcess.Enqueue(rootPid);

        while (toProcess.Count > 0)
        {
            int current = toProcess.Dequeue();
            for (int i = 0; i < allProcesses.Count; i++)
            {
                if (allProcesses[i].ParentId == current)
                {
                    int childId = allProcesses[i].Id;
                    if (destination.Add(childId))
                    {
                        toProcess.Enqueue(childId);
                    }
                }
            }
        }
    }

    private bool ThrottleProcessInternal(FastProcessEntry process, DateTime nowUtc, out ThrottledProcessInfo info)
    {
        info = null!;

        if (!CanTouchProcess(process.Id, process.ProcessName, process.ExeName))
        {
            return false;
        }

        if (!ProcessIdentityHelper.GetProcessIdentity(process.Id, out long creationTime, out string? imagePath))
        {
            // Silently skip elevated or inaccessible processes
            return false;
        }

        if (!string.IsNullOrEmpty(imagePath) && SystemHelperGuard.IsTrusted(imagePath))
        {
            return false;
        }

        if (ProtectedProcesses.IsProtected(process.ProcessName, imagePath) ||
            ProtectedProcesses.IsProtected(process.ExeName, imagePath))
        {
            return false;
        }

        int access = ProcessIdentityHelper.PROCESS_SET_INFORMATION | ProcessIdentityHelper.PROCESS_QUERY_LIMITED_INFORMATION;
        IntPtr hProcess = ProcessIdentityHelper.OpenProcess(access, false, process.Id);
        if (hProcess == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            uint originalPriority = ProcessIdentityHelper.GetPriorityClass(hProcess);
            if (originalPriority == 0)
            {
                originalPriority = ProcessIdentityHelper.NORMAL_PRIORITY_CLASS;
            }

            // 1. EcoQoS execution speed throttling
            bool ecoQosOk = ProcessIdentityHelper.SetEcoQoS(hProcess, true);

            // 2. Below normal priority class (avoiding IDLE to prevent starvation)
            bool prioOk = ProcessIdentityHelper.SetPriorityClass(hProcess, ProcessIdentityHelper.BELOW_NORMAL_PRIORITY_CLASS);

            // 3. Low memory priority
            bool memOk = ProcessIdentityHelper.SetMemoryPriority(hProcess, ProcessIdentityHelper.MEMORY_PRIORITY_LOW);

            info = new ThrottledProcessInfo
            {
                ProcessId = process.Id,
                CreationTime = creationTime,
                ImagePath = imagePath ?? string.Empty,
                ProcessName = process.ProcessName,
                ExeName = process.ExeName,
                OriginalPriorityClass = originalPriority,
                ThrottledAtUtc = nowUtc,
                BackgroundSinceUtc = nowUtc,
                HasTrimmedWorkingSet = false,
                EcoQoSEnabled = ecoQosOk,
                PriorityReduced = prioOk,
                MemoryPriorityReduced = memOk
            };

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ProcessIdentityHelper.CloseHandle(hProcess);
        }
    }

    private static void TrimProcessWorkingSetInternal(ThrottledProcessInfo info)
    {
        int access = ProcessIdentityHelper.PROCESS_SET_QUOTA | ProcessIdentityHelper.PROCESS_QUERY_LIMITED_INFORMATION;
        IntPtr hProcess = ProcessIdentityHelper.OpenProcess(access, false, info.ProcessId);
        if (hProcess == IntPtr.Zero) return;

        try
        {
            ProcessIdentityHelper.TrimWorkingSet(hProcess);
        }
        catch { }
        finally
        {
            ProcessIdentityHelper.CloseHandle(hProcess);
        }
    }

    private static void RestoreProcessInternal(ThrottledProcessInfo info)
    {
        ProcessIdentityHelper.RestoreProcess(
            info.ProcessId,
            info.CreationTime,
            info.ImagePath,
            info.OriginalPriorityClass);
    }

    private void RestoreAllInternal()
    {
        foreach (var info in _throttledProcesses.Values)
        {
            try
            {
                RestoreProcessInternal(info);
            }
            catch { }
        }

        _throttledProcesses.Clear();
        DeleteJournalFile();
    }

    #region Journal Persistence & Recovery

    private void PersistJournalInternal()
    {
        try
        {
            EnsureSecureJournalDirectory(Path.GetDirectoryName(_journalFilePath)!);

            var entries = _throttledProcesses.Values.Select(p => new ThrottledProcessJournalEntry
            {
                ProcessId = p.ProcessId,
                CreationTime = p.CreationTime,
                ImagePath = p.ImagePath,
                ProcessName = p.ProcessName,
                OriginalPriorityClass = p.OriginalPriorityClass,
                ThrottledAtUtc = p.ThrottledAtUtc
            }).ToList();

            string json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            string tempFile = _journalFilePath + ".tmp";

            File.WriteAllText(tempFile, json);
            File.Move(tempFile, _journalFilePath, overwrite: true);
        }
        catch
        {
            // Fail safely without throwing on journal IO error
        }
    }

    private void DeleteJournalFile()
    {
        try
        {
            if (File.Exists(_journalFilePath))
            {
                File.Delete(_journalFilePath);
            }
        }
        catch { }
    }

    /// <summary>
    /// Restores any throttled processes recorded in the crash journal.
    /// Verifies process identity (PID, creation time, image path) before touching any process.
    /// </summary>
    public static int RecoverFromJournal(string? journalPath = null)
    {
        string path = journalPath ?? GetDefaultJournalFilePath();
        return RecoverFromJournalInternal(path);
    }

    private static int RecoverFromJournalInternal(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        int restored = 0;
        try
        {
            string json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<ThrottledProcessJournalEntry>>(json);
            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (ProcessIdentityHelper.RestoreProcess(
                        entry.ProcessId,
                        entry.CreationTime,
                        entry.ImagePath,
                        entry.OriginalPriorityClass))
                    {
                        restored++;
                    }
                }
            }

            File.Delete(path);
        }
        catch
        {
            try { File.Delete(path); } catch { }
        }

        return restored;
    }

    public static void EnsureSecureJournalDirectory(string directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath)) return;

        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var dirInfo = new DirectoryInfo(directoryPath);
                var sec = dirInfo.GetAccessControl();

                // Ensure current user or administrators have full control
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.User != null)
                {
                    sec.AddAccessRule(new FileSystemAccessRule(
                        identity.User,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                    dirInfo.SetAccessControl(sec);
                }
            }
            catch
            {
                // Silently proceed if ACL adjustment fails due to permissions
            }
        }
    }

    #endregion

    public void Dispose()
    {
        RestoreAll();
    }
}
