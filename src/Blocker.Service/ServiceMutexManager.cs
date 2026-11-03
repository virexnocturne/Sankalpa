using System;
using Blocker.Core;

namespace Blocker.Service;

/// <summary>
/// Service-specific singleton mutex manager for Blocker.Service.
/// Encapsulates Windows Service singleton lifecycle guarantees.
/// </summary>
public class ServiceMutexManager : SingletonMutexManager
{
    public const string DefaultMutexName = @"Global\BlockerServiceSingletonMutex";

    public ServiceMutexManager(
        string mutexName = DefaultMutexName,
        IMutexFactory? mutexFactory = null,
        Action<string>? logWarning = null,
        Action<string>? logError = null)
        : base(mutexName, mutexFactory, logWarning, logError)
    {
    }

    public ServiceMutexManager(
        IMutexHandle mutexHandle,
        bool createdNew,
        Action<string>? logWarning = null,
        Action<string>? logError = null,
        string mutexName = DefaultMutexName)
        : base(mutexHandle, createdNew, logWarning, logError, mutexName)
    {
    }
}
