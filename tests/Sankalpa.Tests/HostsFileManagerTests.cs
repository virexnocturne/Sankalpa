using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Blocker.Core.Tests;

[SupportedOSPlatform("windows")]
public class HostsFileManagerTests : IDisposable
{
    private static Response Handle(Request? request, IBlockStore store, IClientIdentity? clientIdentity = null)
        => CommandHandler.Handle(request, store, clientIdentity ?? ClientIdentity.StandardUser());

    private readonly string _testDir;
    private readonly string _hostsPath;

    public HostsFileManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"Blocker_HostsTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _hostsPath = Path.Combine(_testDir, "hosts");
        File.WriteAllText(_hostsPath, "# Sample hosts file\r\n127.0.0.1 localhost\r\n::1 localhost\r\n");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void TestA_EnableThenDisableAllBlocks_HostsClean()
    {
        var manager = new HostsFileManager(_hostsPath)
        {
            RetryDelayMs = 1
        };

        // Enable blocking
        bool applyOk = manager.SyncHosts(new[] { "example.com" });
        Assert.True(applyOk);
        Assert.Equal(HostsState.Blocking, manager.State);
        Assert.True(manager.CurrentDomainCount >= 3); // example.com, www.example.com, m.example.com
        Assert.Null(manager.LastError);

        string contentWithBlock = File.ReadAllText(_hostsPath);
        Assert.Contains(HostsFileBuilder.BeginMarker, contentWithBlock);
        Assert.Contains("127.0.0.1 example.com", contentWithBlock);

        // Disable all blocks
        bool disableOk = manager.SyncHosts(Array.Empty<string>());
        Assert.True(disableOk);
        Assert.Equal(HostsState.Idle, manager.State);
        Assert.Equal(0, manager.CurrentDomainCount);
        Assert.Null(manager.LastError);

        string cleanContent = File.ReadAllText(_hostsPath);
        Assert.DoesNotContain(HostsFileBuilder.BeginMarker, cleanContent);
        Assert.Contains("localhost", cleanContent);
    }

    [Fact]
    public async Task TestB_RemovalFails3TimesThenSucceeds_EventuallyCleanWithoutStateChange()
    {
        var manager = new HostsFileManager(_hostsPath)
        {
            MaxRetries = 6,
            RetryDelayMs = 25
        };

        // 1. Initial applied block
        manager.SyncHosts(new[] { "example.com" });
        Assert.Equal(HostsState.Blocking, manager.State);

        // 2. Lock the hosts file in a background task and release after ~25ms
        using var streamLock = new FileStream(_hostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var unlockTask = Task.Run(async () =>
        {
            await Task.Delay(25);
            streamLock.Dispose();
        });

        // Removal call
        bool cleaned = manager.SyncHosts(Array.Empty<string>());
        await unlockTask;

        Assert.True(cleaned);
        Assert.Equal(HostsState.Idle, manager.State);
        Assert.Equal(0, manager.CurrentDomainCount);
        Assert.Null(manager.LastError);

        string content = File.ReadAllText(_hostsPath);
        Assert.DoesNotContain(HostsFileBuilder.BeginMarker, content);
    }

    [Fact]
    public void TestC_BlockAddedManuallyWhileNothingActive_GetsRemoved()
    {
        var manager = new HostsFileManager(_hostsPath)
        {
            RetryDelayMs = 1
        };

        // Manually write rogue block into the hosts file while no blocks are active
        string rogue = "# hosts header\r\n# BLOCKER-BEGIN\r\n127.0.0.1 rogue.com\r\n::1 rogue.com\r\n# BLOCKER-END\r\n";
        File.WriteAllText(_hostsPath, rogue);

        // Level-triggered sync with empty desired domains
        bool ok = manager.SyncHosts(Array.Empty<string>());
        Assert.True(ok);
        Assert.Equal(HostsState.Idle, manager.State);
        Assert.Equal(0, manager.CurrentDomainCount);

        string actual = File.ReadAllText(_hostsPath);
        Assert.DoesNotContain(HostsFileBuilder.BeginMarker, actual);
        Assert.DoesNotContain("rogue.com", actual);
    }

    [Fact]
    public void TestD_PartialOrTamperedBlock_Repaired()
    {
        var manager = new HostsFileManager(_hostsPath)
        {
            RetryDelayMs = 1
        };

        // Apply initial valid block
        manager.SyncHosts(new[] { "example.com" });

        // Manually tamper with the block section (delete the IPv6 ::1 line)
        string content = File.ReadAllText(_hostsPath);
        string tampered = content.Replace("::1 example.com", "# tampered deletion");
        File.WriteAllText(_hostsPath, tampered);

        // Level-triggered sync
        bool ok = manager.SyncHosts(new[] { "example.com" });
        Assert.True(ok);
        Assert.Equal(HostsState.Blocking, manager.State);
        Assert.True(manager.IsBlockIntact(new[] { "example.com" }));

        string repairedContent = File.ReadAllText(_hostsPath);
        Assert.Contains("::1 example.com", repairedContent);
        Assert.Contains("127.0.0.1 example.com", repairedContent);
    }

    [Fact]
    public void TestE_EqualState_ZeroWrites()
    {
        var manager = new HostsFileManager(_hostsPath)
        {
            RetryDelayMs = 1
        };

        // 1. Initial write
        manager.SyncHosts(new[] { "example.com" });
        DateTime firstWriteTime = File.GetLastWriteTimeUtc(_hostsPath);

        Thread.Sleep(50); // Ensure timer resolution can advance

        // Second call with equal desired domains
        bool secondOk = manager.SyncHosts(new[] { "example.com" });
        Assert.True(secondOk);
        DateTime secondWriteTime = File.GetLastWriteTimeUtc(_hostsPath);

        Assert.Equal(firstWriteTime, secondWriteTime); // ZERO writes!

        // 2. Test zero writes on Idle state
        manager.SyncHosts(Array.Empty<string>());
        DateTime cleanWriteTime = File.GetLastWriteTimeUtc(_hostsPath);

        Thread.Sleep(50);

        bool thirdOk = manager.SyncHosts(Array.Empty<string>());
        Assert.True(thirdOk);
        DateTime thirdWriteTime = File.GetLastWriteTimeUtc(_hostsPath);

        Assert.Equal(cleanWriteTime, thirdWriteTime); // ZERO writes!
    }

    [Fact]
    public void TestF_FailureKeepsErrorState_SuccessReturnsToIdleOrBlocking()
    {
        var manager = new HostsFileManager(_hostsPath)
        {
            MaxRetries = 2,
            RetryDelayMs = 1
        };

        // Lock file exclusively to force all retries to fail
        using (var lockStream = new FileStream(_hostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool result = manager.SyncHosts(new[] { "example.com" });
            Assert.False(result);
            Assert.Equal(HostsState.Error, manager.State);
            Assert.NotNull(manager.LastError);
            Assert.Contains("IOException", manager.LastError);
        }

        // Lock is released now; next sync must succeed and recover
        bool recoverOk = manager.SyncHosts(new[] { "example.com" });
        Assert.True(recoverOk);
        Assert.Equal(HostsState.Blocking, manager.State);
        Assert.Null(manager.LastError);

        // Turn off blocks -> must return to Idle
        bool idleOk = manager.SyncHosts(Array.Empty<string>());
        Assert.True(idleOk);
        Assert.Equal(HostsState.Idle, manager.State);
        Assert.Null(manager.LastError);
    }

    [Fact]
    public void TestG_HostsState_And_ElevatedFlag_AreIndependent()
    {
        // 1. Service non-elevated, but HostsState is Blocking
        var store1 = new BlockerDatabase(
            customDbPath: Path.Combine(_testDir, "test1.db"),
            customHostsManager: new HostsFileManager(_hostsPath));
        store1.ServiceElevated = false;
        store1.HostsState = HostsState.Blocking;
        store1.HostsDomainCount = 4;
        store1.HostsLastError = null;

        var resp1 = Handle(new Request { Command = CommandNames.GetStatus }, store1);
        Assert.True(resp1.Ok);
        var status1 = JsonSerializer.Deserialize<StatusData>(resp1.Data!);
        Assert.NotNull(status1);
        Assert.False(status1.ServiceElevated);
        Assert.Equal(HostsState.Blocking, status1.HostsState);
        Assert.Equal(4, status1.HostsDomainCount);
        Assert.Null(status1.HostsLastError);

        // 2. Service elevated, but HostsState is Error
        var store2 = new BlockerDatabase(
            customDbPath: Path.Combine(_testDir, "test2.db"),
            customHostsManager: new HostsFileManager(_hostsPath));
        store2.ServiceElevated = true;
        store2.HostsState = HostsState.Error;
        store2.HostsDomainCount = 0;
        store2.HostsLastError = "IOException: The process cannot access the file.";

        var resp2 = Handle(new Request { Command = CommandNames.GetStatus }, store2);
        Assert.True(resp2.Ok);
        var status2 = JsonSerializer.Deserialize<StatusData>(resp2.Data!);
        Assert.NotNull(status2);
        Assert.True(status2.ServiceElevated);
        Assert.Equal(HostsState.Error, status2.HostsState);
        Assert.Equal("IOException: The process cannot access the file.", status2.HostsLastError);

        // 3. Service non-elevated, and HostsState is Idle
        var store3 = new BlockerDatabase(
            customDbPath: Path.Combine(_testDir, "test3.db"),
            customHostsManager: new HostsFileManager(_hostsPath));
        store3.ServiceElevated = false;
        store3.HostsState = HostsState.Idle;
        store3.HostsDomainCount = 0;
        store3.HostsLastError = null;

        var resp3 = Handle(new Request { Command = CommandNames.GetStatus }, store3);
        Assert.True(resp3.Ok);
        var status3 = JsonSerializer.Deserialize<StatusData>(resp3.Data!);
        Assert.NotNull(status3);
        Assert.False(status3.ServiceElevated);
        Assert.Equal(HostsState.Idle, status3.HostsState);
        Assert.Null(status3.HostsLastError);
    }

    [Fact]
    public void TestHostsStatus_ReturnsStateDomainCountAndDomainsInFile()
    {
        var manager = new HostsFileManager(_hostsPath) { RetryDelayMs = 1 };
        manager.SyncHosts(new[] { "example.com" });

        var store = new BlockerDatabase(
            customDbPath: Path.Combine(_testDir, "status_test.db"),
            customHostsManager: manager);
        store.HostsState = manager.State;
        store.HostsDomainCount = manager.CurrentDomainCount;

        var resp = Handle(new Request { Command = CommandNames.HostsStatus }, store);
        Assert.True(resp.Ok);
        var info = JsonSerializer.Deserialize<HostsStatusInfo>(resp.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(info);
        Assert.Equal(HostsState.Blocking, info.HostsState);
        Assert.True(info.DomainCount > 0);
        Assert.Contains(info.Domains, d => d.Equals("example.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TestCleanHosts_WorksWhenHostsStateIsError_AndNoProfileActive()
    {
        var manager = new HostsFileManager(_hostsPath) { RetryDelayMs = 1 };
        // Apply block
        manager.SyncHosts(new[] { "example.com" });

        var store = new BlockerDatabase(
            customDbPath: Path.Combine(_testDir, "clean_test.db"),
            customHostsManager: manager);
        store.HostsState = HostsState.Error;
        store.HostsLastError = "IOException: Temporary lock occurred.";

        // Execute CleanHosts while state is Error
        var resp = Handle(new Request { Command = CommandNames.CleanHosts }, store);
        Assert.True(resp.Ok);
        Assert.Equal(HostsState.Idle, store.HostsState);
        Assert.Null(store.HostsLastError);

        string content = File.ReadAllText(_hostsPath);
        Assert.DoesNotContain(HostsFileBuilder.BeginMarker, content);
    }
}
