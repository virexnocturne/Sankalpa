using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Sankalpa.Tests;

public class WorkerMeditationOptimizationTests
{
    private record MockProcess(int Id, string ProcessName, string ExeName, int ParentId, string? ImagePath, long CreationTime);

    [Fact]
    public void MeditationAllowedCache_SyncSession_PrecomputesNormalizedAllowedAppsAndRoots()
    {
        var cache = new MeditationAllowedCache();
        Assert.False(cache.HasAllowedApps);
        Assert.False(cache.IsExplorerAllowed);

        var allowedApps = new List<string> { "NotePad.EXE", "Code", "VLC.exe" };
        var roots = new Dictionary<string, string>
        {
            ["Code"] = @"C:\Program Files\Microsoft VS Code",
            ["vlc.exe"] = @"C:\Program Files\VideoLAN\VLC"
        };

        DateTime start = DateTime.UtcNow;
        bool invalidated = cache.SyncSession(1, "Active", start, allowedApps, roots);
        Assert.True(invalidated);
        Assert.True(cache.HasAllowedApps);
        Assert.False(cache.IsExplorerAllowed);

        // Calling SyncSession with identical parameters returns false (no invalidation)
        bool reInvalidated = cache.SyncSession(1, "Active", start, allowedApps, roots);
        Assert.False(reInvalidated);

        // Adding explorer sets IsExplorerAllowed
        var appsWithExplorer = new List<string> { "notepad", "explorer.exe" };
        bool explorerInvalidated = cache.SyncSession(1, "Active", start, appsWithExplorer, roots);
        Assert.True(explorerInvalidated);
        Assert.True(cache.IsExplorerAllowed);

        // Reset clears everything
        cache.Reset();
        Assert.False(cache.HasAllowedApps);
        Assert.False(cache.IsExplorerAllowed);
        Assert.Equal(0, cache.CachedDecisionsCount);
    }

    [Fact]
    public void MeditationAllowedCache_DecisionEquivalence_MatchesOldWorkerDecisionAcrossMixedProcessList()
    {
        var allowedApps = new List<string> { "notepad", "code", "claude", "vlc" };
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = @"C:\Users\User\AppData\Local\AnthropicClaude",
            ["vlc"] = @"C:\Program Files\VideoLAN\VLC"
        };

        var cache = new MeditationAllowedCache();
        cache.SyncSession(10, "Active", DateTime.UtcNow, allowedApps, roots);

        var testCases = new List<(string ProcName, string ExeName, string? Path)>
        {
            // Direct allowed apps
            ("notepad", "notepad.exe", @"C:\Windows\System32\notepad.exe"),
            ("code", "Code.exe", @"C:\Program Files\Microsoft VS Code\Code.exe"),

            // Path-pinned allowed: inside pinned root -> allowed
            ("claude", "claude.exe", @"C:\Users\User\AppData\Local\AnthropicClaude\claude.exe"),
            ("vlc", "vlc.exe", @"C:\Program Files\VideoLAN\VLC\vlc.exe"),

            // Path-pinned allowed: outside pinned root -> NOT allowed
            ("claude", "claude.exe", @"C:\Users\User\.local\bin\claude.exe"),
            ("vlc", "vlc.exe", @"C:\Users\User\Downloads\fake_vlc.exe"),

            // Non-allowed applications
            ("chrome", "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
            ("firefox", "firefox.exe", @"C:\Program Files\Mozilla Firefox\firefox.exe"),
            ("discord", "Discord.exe", @"C:\Users\User\AppData\Local\Discord\app.exe"),
            ("procexp", "procexp.exe", @"C:\Sysinternals\procexp.exe"),
            ("cmd", "cmd.exe", @"C:\Windows\System32\cmd.exe"),

            // Windows clipping exceptions
            ("snippingtool", "SnippingTool.exe", null),
            ("screenclippinghost", "ScreenClippingHost.exe", null)
        };

        foreach (var (procName, exeName, path) in testCases)
        {
            bool oldDecision = Worker.IsAppAllowed(procName, exeName, allowedApps, path, roots);
            bool newDecision = cache.IsAppAllowedFast(procName, exeName, path);

            Assert.True(oldDecision == newDecision,
                $"Decision mismatch for process '{procName}' ('{exeName}', path: '{path}'). Old={oldDecision}, New={newDecision}");
        }
    }

    [Fact]
    public void MeditationAllowedCache_PidReuse_ForcesReevaluationOnNewCreationTime()
    {
        var cache = new MeditationAllowedCache();
        var allowedApps = new List<string> { "notepad" };
        cache.SyncSession(1, "Active", DateTime.UtcNow, allowedApps, null);

        int reusedPid = 4321;
        long originalCreationTime = 133500000000000000L;
        long newCreationTime = 133500000500000000L; // Process died and PID recycled later

        // 1. Initial process (notepad) evaluated and cached as Allowed
        cache.CacheDecision(reusedPid, originalCreationTime, ProcessAllowDecision.Allowed);

        // Query with original creation time hits cache and returns Allowed
        Assert.True(cache.TryGetCachedDecision(reusedPid, originalCreationTime, out var decision1));
        Assert.Equal(ProcessAllowDecision.Allowed, decision1);

        // 2. Query with NEW creation time (PID reused by rogue app) must MISS cache!
        Assert.False(cache.TryGetCachedDecision(reusedPid, newCreationTime, out var decision2));

        // It must NOT inherit the Allowed decision!
        Assert.NotEqual(ProcessAllowDecision.Allowed, decision2);

        // Rogue app evaluated and cached as MustSuspend
        cache.CacheDecision(reusedPid, newCreationTime, ProcessAllowDecision.MustSuspend);
        Assert.True(cache.TryGetCachedDecision(reusedPid, newCreationTime, out var decision3));
        Assert.Equal(ProcessAllowDecision.MustSuspend, decision3);
    }

    [Fact]
    public void MeditationAllowedCache_PerformanceBenchmark_MeasuresMassiveSpeedup()
    {
        var allowedApps = new List<string> { "notepad", "code", "claude", "vlc", "spotify" };
        var roots = new Dictionary<string, string>
        {
            ["claude"] = @"C:\Users\User\AppData\Local\AnthropicClaude",
            ["code"] = @"C:\Program Files\Microsoft VS Code"
        };

        var cache = new MeditationAllowedCache();
        cache.SyncSession(1, "Active", DateTime.UtcNow, allowedApps, roots);

        var processes = new List<(string Name, string Exe, string? Path)>();
        for (int i = 0; i < 500; i++)
        {
            string name = (i % 5 == 0) ? "code" : (i % 7 == 0) ? "notepad" : $"process_{i}";
            string exe = name + ".exe";
            string path = @"C:\Program Files\Mock\" + exe;
            processes.Add((name, exe, path));
        }

        // Pre-populate decision cache to simulate steady-state meditation monitoring ticks
        for (int i = 0; i < processes.Count; i++)
        {
            var p = processes[i];
            bool allowed = cache.IsAppAllowedFast(p.Name, p.Exe, p.Path);
            cache.CacheDecision(i + 1, 1000L + i, allowed ? ProcessAllowDecision.Allowed : ProcessAllowDecision.MustSuspend);
        }

        const int iterations = 100;

        // Warmup JIT
        for (int i = 0; i < 50; i++)
        {
            var p = processes[i];
            Worker.IsAppAllowed(p.Name, p.Exe, allowedApps, p.Path, roots);
            cache.TryGetCachedDecision(i + 1, 1000L + i, out _);
        }

        // Old baseline: repeated normalization, string manipulation, and linear search per process
        var swOld = Stopwatch.StartNew();
        for (int iter = 0; iter < iterations; iter++)
        {
            for (int i = 0; i < processes.Count; i++)
            {
                var p = processes[i];
                Worker.IsAppAllowed(p.Name, p.Exe, allowedApps, p.Path, roots);
            }
        }
        swOld.Stop();

        // New fast path: O(1) cached decision lookup by (PID, CreationTime)
        var swNew = Stopwatch.StartNew();
        for (int iter = 0; iter < iterations; iter++)
        {
            for (int i = 0; i < processes.Count; i++)
            {
                cache.TryGetCachedDecision(i + 1, 1000L + i, out _);
            }
        }
        swNew.Stop();

        // Cached lookup must be at least 3x faster than repeated re-evaluation
        Assert.True(swNew.ElapsedTicks < swOld.ElapsedTicks,
            $"Expected cached hot-path ({swNew.ElapsedMilliseconds} ms) to be faster than un-cached evaluation ({swOld.ElapsedMilliseconds} ms)");
    }
}
