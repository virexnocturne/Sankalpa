using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Sankalpa.Security.Tests;

public class SingletonMutexTests
{
    private sealed class MockMutexHandle : IMutexHandle
    {
        public bool WaitOneResult { get; set; } = true;
        public Exception? WaitOneException { get; set; }
        public Exception? ReleaseException { get; set; }
        public int WaitOneCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public TimeSpan LastWaitTimeout { get; private set; }

        public bool WaitOne(TimeSpan timeout)
        {
            WaitOneCalls++;
            LastWaitTimeout = timeout;
            if (WaitOneException != null)
            {
                throw WaitOneException;
            }
            return WaitOneResult;
        }

        public void ReleaseMutex()
        {
            ReleaseCalls++;
            if (ReleaseException != null)
            {
                throw ReleaseException;
            }
        }

        public void Dispose()
        {
            DisposeCalls++;
        }
    }

    private sealed class MockMutexFactory : IMutexFactory
    {
        public bool CreatedNewToReturn { get; set; } = true;
        public MockMutexHandle HandleToReturn { get; set; } = new();
        public Exception? CreateException { get; set; }
        public int CreateCalls { get; private set; }
        public string? LastCreatedName { get; private set; }
        public bool LastInitiallyOwned { get; private set; }

        public IMutexHandle CreateMutex(bool initiallyOwned, string name, out bool createdNew)
        {
            CreateCalls++;
            LastInitiallyOwned = initiallyOwned;
            LastCreatedName = name;
            if (CreateException != null)
            {
                throw CreateException;
            }
            createdNew = CreatedNewToReturn;
            return HandleToReturn;
        }
    }

    [Fact]
    public void SuccessfulAcquisition_WhenCreatedNew_AcquiresOwnershipWithoutWait()
    {
        var factory = new MockMutexFactory
        {
            CreatedNewToReturn = true
        };

        using var manager = new SingletonMutexManager("TestMutex_CreatedNew", factory);
        var result = manager.TryAcquire();

        Assert.True(result.IsAcquired);
        Assert.Equal(MutexAcquisitionStatus.Acquired, result.Status);
        Assert.True(manager.HasHandle);
        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(0, factory.HandleToReturn.WaitOneCalls);
    }

    [Fact]
    public void SuccessfulAcquisition_WhenExistingMutexNotOwned_AcquiresViaWaitOne()
    {
        var factory = new MockMutexFactory
        {
            CreatedNewToReturn = false,
            HandleToReturn = new MockMutexHandle { WaitOneResult = true }
        };

        using var manager = new SingletonMutexManager("TestMutex_WaitOneAcquired", factory);
        var result = manager.TryAcquire();

        Assert.True(result.IsAcquired);
        Assert.Equal(MutexAcquisitionStatus.Acquired, result.Status);
        Assert.True(manager.HasHandle);
        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(1, factory.HandleToReturn.WaitOneCalls);
        Assert.Equal(TimeSpan.Zero, factory.HandleToReturn.LastWaitTimeout);
    }

    [Fact]
    public void AnotherInstanceOwnsMutex_AcquisitionFails_DoesNotRun()
    {
        var errors = new List<string>();
        var factory = new MockMutexFactory
        {
            CreatedNewToReturn = false,
            HandleToReturn = new MockMutexHandle { WaitOneResult = false }
        };

        using var manager = new SingletonMutexManager(
            "TestMutex_AlreadyOwned",
            factory,
            logError: errors.Add);

        var result = manager.TryAcquire();

        Assert.False(result.IsAcquired);
        Assert.Equal(MutexAcquisitionStatus.AlreadyRunning, result.Status);
        Assert.False(manager.HasHandle);
        Assert.Equal(1, factory.HandleToReturn.WaitOneCalls);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, msg => msg.Contains("already running", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AbandonedMutex_DuringWaitOne_AcquiresOwnershipSuccessfully()
    {
        var warnings = new List<string>();
        var factory = new MockMutexFactory
        {
            CreatedNewToReturn = false,
            HandleToReturn = new MockMutexHandle
            {
                WaitOneException = new AbandonedMutexException()
            }
        };

        using var manager = new SingletonMutexManager(
            "TestMutex_AbandonedWait",
            factory,
            logWarning: warnings.Add);

        var result = manager.TryAcquire();

        Assert.True(result.IsAcquired);
        Assert.Equal(MutexAcquisitionStatus.AbandonedAcquired, result.Status);
        Assert.True(manager.HasHandle);
        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, msg => msg.Contains("abandoned", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AbandonedMutex_DuringCreation_AcquiresOwnershipSuccessfully()
    {
        var warnings = new List<string>();
        var factory = new MockMutexFactory
        {
            CreateException = new AbandonedMutexException()
        };

        using var manager = new SingletonMutexManager(
            "TestMutex_AbandonedCreation",
            factory,
            logWarning: warnings.Add);

        var result = manager.TryAcquire();

        Assert.True(result.IsAcquired);
        Assert.Equal(MutexAcquisitionStatus.AbandonedAcquired, result.Status);
        Assert.True(manager.HasHandle);
        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, msg => msg.Contains("abandoned", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnexpectedAcquisitionException_UnauthorizedAccess_FailsSafely()
    {
        var errors = new List<string>();
        var factory = new MockMutexFactory
        {
            CreateException = new UnauthorizedAccessException("Access denied to global mutex")
        };

        using var manager = new SingletonMutexManager(
            "TestMutex_Unauthorized",
            factory,
            logError: errors.Add);

        var result = manager.TryAcquire();

        Assert.False(result.IsAcquired);
        Assert.Equal(MutexAcquisitionStatus.Failed, result.Status);
        Assert.False(manager.HasHandle);
        Assert.IsType<UnauthorizedAccessException>(result.Exception);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, msg => msg.Contains("Access denied", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnexpectedAcquisitionException_WaitHandleCannotBeOpened_FailsSafely()
    {
        var errors = new List<string>();
        var factory = new MockMutexFactory
        {
            CreateException = new WaitHandleCannotBeOpenedException("Mutex cannot be opened")
        };

        using var manager = new SingletonMutexManager(
            "TestMutex_CannotBeOpened",
            factory,
            logError: errors.Add);

        var result = manager.TryAcquire();

        Assert.False(result.IsAcquired);
        Assert.Equal(MutexAcquisitionStatus.Failed, result.Status);
        Assert.False(manager.HasHandle);
        Assert.IsType<WaitHandleCannotBeOpenedException>(result.Exception);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void UnexpectedAcquisitionException_IOException_FailsSafely()
    {
        var errors = new List<string>();
        var factory = new MockMutexFactory
        {
            CreateException = new IOException("OS handle table full")
        };

        using var manager = new SingletonMutexManager(
            "TestMutex_IOException",
            factory,
            logError: errors.Add);

        var result = manager.TryAcquire();

        Assert.False(result.IsAcquired);
        Assert.Equal(MutexAcquisitionStatus.Failed, result.Status);
        Assert.False(manager.HasHandle);
        Assert.IsType<IOException>(result.Exception);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void UnexpectedAcquisitionException_WaitOneException_FailsSafely()
    {
        var errors = new List<string>();
        var factory = new MockMutexFactory
        {
            CreatedNewToReturn = false,
            HandleToReturn = new MockMutexHandle
            {
                WaitOneException = new InvalidOperationException("Wait failed unexpectedly")
            }
        };

        using var manager = new SingletonMutexManager(
            "TestMutex_WaitOneException",
            factory,
            logError: errors.Add);

        var result = manager.TryAcquire();

        Assert.False(result.IsAcquired);
        Assert.Equal(MutexAcquisitionStatus.Failed, result.Status);
        Assert.False(manager.HasHandle);
        Assert.IsType<InvalidOperationException>(result.Exception);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Cleanup_AfterSuccessfulAcquisition_ReleasesAndDisposes()
    {
        var factory = new MockMutexFactory
        {
            CreatedNewToReturn = true
        };

        var manager = new SingletonMutexManager("TestMutex_CleanupSuccess", factory);
        var result = manager.TryAcquire();

        Assert.True(result.IsAcquired);
        Assert.True(manager.HasHandle);

        manager.Dispose();

        Assert.False(manager.HasHandle);
        Assert.Equal(1, factory.HandleToReturn.ReleaseCalls);
        Assert.Equal(1, factory.HandleToReturn.DisposeCalls);
    }

    [Fact]
    public void Cleanup_AfterUnsuccessfulAcquisition_OnlyDisposes_NeverReleases()
    {
        var factory = new MockMutexFactory
        {
            CreatedNewToReturn = false,
            HandleToReturn = new MockMutexHandle { WaitOneResult = false }
        };

        var manager = new SingletonMutexManager("TestMutex_CleanupUnsuccessful", factory);
        var result = manager.TryAcquire();

        Assert.False(result.IsAcquired);
        Assert.False(manager.HasHandle);

        manager.Dispose();

        // Must NEVER call ReleaseMutex when not owned
        Assert.Equal(0, factory.HandleToReturn.ReleaseCalls);
        // Handle must still be disposed cleanly
        Assert.Equal(1, factory.HandleToReturn.DisposeCalls);
    }

    [Fact]
    public void Cleanup_AfterCreationException_DoesNotThrowAndDoesNotRelease()
    {
        var factory = new MockMutexFactory
        {
            CreateException = new UnauthorizedAccessException("Cannot create")
        };

        var manager = new SingletonMutexManager("TestMutex_CleanupException", factory);
        var result = manager.TryAcquire();

        Assert.False(result.IsAcquired);
        Assert.False(manager.HasHandle);

        // Disposing should not throw
        manager.Dispose();
        Assert.Equal(0, factory.HandleToReturn.ReleaseCalls);
    }

    [Fact]
    public void Cleanup_MultipleDisposeCalls_IsIdempotent()
    {
        var factory = new MockMutexFactory
        {
            CreatedNewToReturn = true
        };

        var manager = new SingletonMutexManager("TestMutex_IdempotentDispose", factory);
        manager.TryAcquire();

        manager.Dispose();
        manager.Dispose();
        manager.Dispose();

        // ReleaseMutex and Dispose should only be executed once
        Assert.Equal(1, factory.HandleToReturn.ReleaseCalls);
        Assert.Equal(1, factory.HandleToReturn.DisposeCalls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ServiceMutexManager_InheritsSingletonMutexManager_BehavesIdentically()
    {
        var factory = new MockMutexFactory
        {
            CreatedNewToReturn = true
        };

        using var serviceManager = new ServiceMutexManager("ServiceMutex_Test", factory);
        var result = serviceManager.TryAcquire();

        Assert.True(result.IsAcquired);
        Assert.True(serviceManager.HasHandle);
    }

    [Fact]
    public async Task RealSystemMutex_IntegrationTest_EnsuresSingletonExclusionAndHandoff()
    {
        string uniqueMutexName = $"Global\\Sankalpa_TestMutex_{Guid.NewGuid():N}";

        using var firstInstance = new SingletonMutexManager(uniqueMutexName);
        var firstResult = firstInstance.TryAcquire();

        Assert.True(firstResult.IsAcquired);
        Assert.True(firstInstance.HasHandle);

        // Second instance running on a distinct thread/process must fail to acquire because first instance owns it
        await Task.Run(() =>
        {
            using var secondInstance = new SingletonMutexManager(uniqueMutexName);
            var secondResult = secondInstance.TryAcquire();
            Assert.False(secondResult.IsAcquired);
            Assert.Equal(MutexAcquisitionStatus.AlreadyRunning, secondResult.Status);
            Assert.False(secondInstance.HasHandle);
        });

        // Release first instance
        firstInstance.Dispose();
        Assert.False(firstInstance.HasHandle);

        // Now another instance on a separate thread should be able to acquire ownership
        await Task.Run(() =>
        {
            using var thirdInstance = new SingletonMutexManager(uniqueMutexName);
            var thirdResult = thirdInstance.TryAcquire();
            Assert.True(thirdResult.IsAcquired);
            Assert.True(thirdInstance.HasHandle);
        });
    }
}
