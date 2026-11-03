using System;
using System.Collections.Generic;
using System.Diagnostics;
using Blocker.Core;

namespace Blocker.Service;

public static class BenchmarkRunner
{
    public static void Run()
    {
        Console.WriteLine("===============================================================");
        Console.WriteLine("        SANKALPA PERFORMANCE & MEMORY PROFILING");
        Console.WriteLine("===============================================================");

        var db = new BlockerDatabase();

        // 1. Current Process.GetProcesses()
        Console.WriteLine("\n[TEST 1] Baseline Process.GetProcesses() (300 iterations):");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long memBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 300; i++)
        {
            var procs = Process.GetProcesses();
            foreach (var pr in procs)
            {
                var name = pr.ProcessName;
                pr.Dispose();
            }
        }
        sw.Stop();
        long memAfter = GC.GetAllocatedBytesForCurrentThread();
        double msPerTickProc = sw.Elapsed.TotalMilliseconds / 300.0;
        double kbPerTickProc = (memAfter - memBefore) / (300.0 * 1024.0);
        Console.WriteLine($"  - Total time (300 ticks): {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  - Latency per tick:       {msPerTickProc:F3} ms/tick");
        Console.WriteLine($"  - Heap garbage allocated: {kbPerTickProc:F2} KB/tick ({(kbPerTickProc * 86.4 / 1024.0):F2} GB/day)");

        // 2. Production FastProcessEnumerator
        Console.WriteLine("\n[TEST 2] FastProcessEnumerator with Zero-Allocation Cache (300 iterations):");
        var pooledList = new List<FastProcessEntry>(512);
        var runningNames = new HashSet<string>(512, StringComparer.OrdinalIgnoreCase);
        var nameCache = new Dictionary<string, (string ProcessName, string ExeName)>(512, StringComparer.OrdinalIgnoreCase);

        // Warm up cache
        FastProcessEnumerator.EnumerateProcesses(pooledList, runningNames, nameCache);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        memBefore = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        for (int i = 0; i < 300; i++)
        {
            FastProcessEnumerator.EnumerateProcesses(pooledList, runningNames, nameCache);
        }
        sw.Stop();
        memAfter = GC.GetAllocatedBytesForCurrentThread();
        double msPerTickFast = sw.Elapsed.TotalMilliseconds / 300.0;
        double kbPerTickFast = (memAfter - memBefore) / (300.0 * 1024.0);
        Console.WriteLine($"  - Total time (300 ticks): {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  - Latency per tick:       {msPerTickFast:F3} ms/tick");
        Console.WriteLine($"  - Heap garbage allocated: {kbPerTickFast:F2} KB/tick ({(kbPerTickFast * 86.4 / 1024.0):F2} GB/day)");

        Console.WriteLine("\n===============================================================");
        Console.WriteLine("                   FINAL COMPARISON");
        Console.WriteLine("===============================================================");
        Console.WriteLine($"Process Enumeration Memory: {kbPerTickProc / Math.Max(0.001, kbPerTickFast):F1}x less garbage ({kbPerTickProc:F1} KB -> {kbPerTickFast:F2} KB)");
        Console.WriteLine($"Process Enumeration Speed:  {msPerTickProc / Math.Max(0.001, msPerTickFast):F2}x faster ({msPerTickProc:F2} ms -> {msPerTickFast:F2} ms)");
        Console.WriteLine("===============================================================\n");
    }
}
