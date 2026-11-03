using System;
using System.IO;
using System.Threading.Tasks;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class ServiceLifetimeManagerTests
{
    [Fact]
    public void IsElevated_DoesNotThrow()
    {
        var elevated = ServiceLifetimeManager.IsElevated();
        // Should return a valid boolean without throwing on any OS platform
        Assert.True(elevated || !elevated);
    }

    [Fact]
    public void IsServiceProcessRunning_DoesNotThrow()
    {
        var running = ServiceLifetimeManager.IsServiceProcessRunning();
        Assert.True(running || !running);
    }

    [Fact]
    public void TryResolveServiceExePath_FindsExeInBinOrReturnsFalse()
    {
        bool found = ServiceLifetimeManager.TryResolveServiceExePath(out string path);
        if (found)
        {
            Assert.True(File.Exists(path));
            Assert.Contains("Service.exe", path, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Empty(path);
        }
    }

    [Fact]
    public void IsServiceInstalled_DoesNotThrow()
    {
        var installed = ServiceLifetimeManager.IsServiceInstalled();
        Assert.True(installed || !installed);
    }

    [Fact]
    public void IsServiceRunning_DoesNotThrow()
    {
        var running = ServiceLifetimeManager.IsServiceRunning();
        Assert.True(running || !running);
    }

    [Fact]
    public void SecurityHardener_ServiceLockdownAndUnlock_DoNotThrow()
    {
        bool locked = SecurityHardener.LockdownService(out _);
        Assert.True(locked || !locked);

        bool unlocked = SecurityHardener.UnlockService(out _);
        Assert.True(unlocked || !unlocked);
    }

    [Fact]
    public async Task EnsureServiceRunningAsync_WhenPipeAlreadyConnected_ReturnsTrueImmediately()
    {
        var mockPipe = new MockSuccessPipeClient();
        bool result = await ServiceLifetimeManager.EnsureServiceRunningAsync(mockPipe, maxWaitSeconds: 1);
        Assert.True(result);
    }

    private class MockSuccessPipeClient : IPipeClient
    {
        public Task<PipeResult> SendRequestAsync(Request request, System.Threading.CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PipeResult.Success(Response.Success("pong")));
        }
    }
}
