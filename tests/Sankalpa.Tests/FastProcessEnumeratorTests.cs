using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class FastProcessEnumeratorTests
{
    [Fact]
    public void EnumerateProcesses_FindsCurrentProcess()
    {
        var destination = new List<FastProcessEntry>();
        var runningNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nameCache = new Dictionary<string, (string ProcessName, string ExeName)>(StringComparer.OrdinalIgnoreCase);

        bool success = FastProcessEnumerator.EnumerateProcesses(destination, runningNames, nameCache);

        Assert.True(success);
        Assert.NotEmpty(destination);
        Assert.Contains(destination, p => p.Id == Environment.ProcessId);

        // Verify runningNames has values
        Assert.NotEmpty(runningNames);

        // Verify nameCache has cached process entries
        if (OperatingSystem.IsWindows())
        {
            Assert.NotEmpty(nameCache);
        }
    }

    [Fact]
    public void EnumerateProcesses_ReusesNameCacheWithoutThrowing()
    {
        var destination = new List<FastProcessEntry>();
        var runningNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nameCache = new Dictionary<string, (string ProcessName, string ExeName)>(StringComparer.OrdinalIgnoreCase);

        FastProcessEnumerator.EnumerateProcesses(destination, runningNames, nameCache);
        int initialCacheCount = nameCache.Count;

        // Second pass should hit cache
        FastProcessEnumerator.EnumerateProcesses(destination, runningNames, nameCache);

        Assert.NotEmpty(destination);
        Assert.True(nameCache.Count >= initialCacheCount);
    }

    [Fact]
    public void GetProcesses_ReturnsProcessList()
    {
        var procs = FastProcessEnumerator.GetProcesses();
        Assert.NotEmpty(procs);
        Assert.Contains(procs, p => p.Id == Environment.ProcessId);
    }
}
