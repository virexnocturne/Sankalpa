using System;
using System.IO;
using System.Threading;

namespace Blocker.Core;

/// <summary>
/// Abstraction over a system mutex handle for testability.
/// </summary>
public interface IMutexHandle : IDisposable
{
    bool WaitOne(TimeSpan timeout);
    void ReleaseMutex();
}

/// <summary>
/// Factory for creating mutex handles.
/// </summary>
public interface IMutexFactory
{
    IMutexHandle CreateMutex(bool initiallyOwned, string name, out bool createdNew);
}

/// <summary>
/// Wrapper for standard System.Threading.Mutex.
/// </summary>
public sealed class SystemMutexHandle : IMutexHandle
{
    private readonly Mutex _mutex;
    private bool _disposed;

    public SystemMutexHandle(Mutex mutex)
    {
        _mutex = mutex ?? throw new ArgumentNullException(nameof(mutex));
    }

    public Mutex UnderlyingMutex => _mutex;

    public bool WaitOne(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _mutex.WaitOne(timeout);
    }

    public void ReleaseMutex()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mutex.ReleaseMutex();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _mutex.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Default factory creating OS system mutexes.
/// </summary>
public sealed class DefaultMutexFactory : IMutexFactory
{
    public static readonly DefaultMutexFactory Instance = new();

    public IMutexHandle CreateMutex(bool initiallyOwned, string name, out bool createdNew)
    {
        var mutex = new Mutex(initiallyOwned, name, out createdNew);
        return new SystemMutexHandle(mutex);
    }
}

/// <summary>
/// Status outcome of attempting to acquire a singleton mutex.
/// </summary>
public enum MutexAcquisitionStatus
{
    Acquired,
    AlreadyRunning,
    AbandonedAcquired,
    Failed
}

/// <summary>
/// Detailed result of mutex acquisition attempt.
/// </summary>
public sealed class MutexAcquisitionResult
{
    public bool IsAcquired => Status == MutexAcquisitionStatus.Acquired || Status == MutexAcquisitionStatus.AbandonedAcquired;
    public MutexAcquisitionStatus Status { get; }
    public Exception? Exception { get; }
    public string? Message { get; }

    public MutexAcquisitionResult(MutexAcquisitionStatus status, string? message = null, Exception? exception = null)
    {
        Status = status;
        Message = message;
        Exception = exception;
    }

    public static MutexAcquisitionResult Acquired(string? message = null) =>
        new(MutexAcquisitionStatus.Acquired, message ?? "Mutex acquired successfully.");

    public static MutexAcquisitionResult Abandoned(string? message = null) =>
        new(MutexAcquisitionStatus.AbandonedAcquired, message ?? "Abandoned mutex acquired.");

    public static MutexAcquisitionResult AlreadyRunning(string? message = null) =>
        new(MutexAcquisitionStatus.AlreadyRunning, message ?? "Another instance of the service is already running.");

    public static MutexAcquisitionResult Failed(Exception exception, string? message = null) =>
        new(MutexAcquisitionStatus.Failed, message ?? exception.Message, exception);
}

/// <summary>
/// Interface for singleton mutex management.
/// </summary>
public interface ISingletonMutex : IDisposable
{
    string MutexName { get; }
    bool HasHandle { get; }
    MutexAcquisitionResult TryAcquire();
    void Release();
}

/// <summary>
/// Manages singleton application/service lifecycle using a named mutex with proper
/// handling of AbandonedMutexException, acquisition failures, and deterministic cleanup.
/// </summary>
public class SingletonMutexManager : ISingletonMutex
{
    private readonly string _mutexName;
    private readonly IMutexFactory? _mutexFactory;
    private readonly Action<string>? _logWarning;
    private readonly Action<string>? _logError;

    private IMutexHandle? _mutexHandle;
    private bool _hasHandle;
    private bool _disposed;
    private readonly bool _preSuppliedHandle;
    private readonly bool _preSuppliedCreatedNew;

    public string MutexName => _mutexName;
    public bool HasHandle => _hasHandle;
    public IMutexHandle? MutexHandle => _mutexHandle;

    public SingletonMutexManager(
        string mutexName,
        IMutexFactory? mutexFactory = null,
        Action<string>? logWarning = null,
        Action<string>? logError = null)
    {
        _mutexName = mutexName ?? throw new ArgumentNullException(nameof(mutexName));
        _mutexFactory = mutexFactory ?? DefaultMutexFactory.Instance;
        _logWarning = logWarning;
        _logError = logError;
    }

    public SingletonMutexManager(
        IMutexHandle mutexHandle,
        bool createdNew,
        Action<string>? logWarning = null,
        Action<string>? logError = null,
        string mutexName = "InjectedMutex")
    {
        _mutexName = mutexName;
        _mutexHandle = mutexHandle ?? throw new ArgumentNullException(nameof(mutexHandle));
        _preSuppliedHandle = true;
        _preSuppliedCreatedNew = createdNew;
        _logWarning = logWarning;
        _logError = logError;
    }

    public MutexAcquisitionResult TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hasHandle)
        {
            return MutexAcquisitionResult.Acquired("Mutex is already acquired by this instance.");
        }

        try
        {
            bool createdNew;

            if (_preSuppliedHandle)
            {
                createdNew = _preSuppliedCreatedNew;
            }
            else
            {
                try
                {
                    _mutexHandle = _mutexFactory!.CreateMutex(true, _mutexName, out createdNew);
                }
                catch (AbandonedMutexException ex)
                {
                    _hasHandle = true;
                    if (ex.Mutex != null && _mutexHandle == null)
                    {
                        _mutexHandle = new SystemMutexHandle(ex.Mutex);
                    }
                    _logWarning?.Invoke("Acquired abandoned mutex during initial creation.");
                    return MutexAcquisitionResult.Abandoned("Acquired abandoned mutex during initial creation.");
                }
            }

            if (createdNew)
            {
                _hasHandle = true;
                return MutexAcquisitionResult.Acquired("Mutex created and acquired as initial owner.");
            }

            // createdNew is false: mutex already exists, do NOT assume ownership.
            // Attempt non-blocking WaitOne(TimeSpan.Zero).
            try
            {
                if (_mutexHandle == null)
                {
                    _hasHandle = false;
                    _logError?.Invoke("Mutex handle was null when attempting wait.");
                    return MutexAcquisitionResult.Failed(new InvalidOperationException("Mutex handle is null."));
                }

                bool acquired = _mutexHandle.WaitOne(TimeSpan.Zero);
                if (acquired)
                {
                    _hasHandle = true;
                    return MutexAcquisitionResult.Acquired("Acquired existing mutex via WaitOne.");
                }
                else
                {
                    _hasHandle = false;
                    _logError?.Invoke("Another instance of Blocker Service is already running. Exiting.");
                    return MutexAcquisitionResult.AlreadyRunning();
                }
            }
            catch (AbandonedMutexException)
            {
                _hasHandle = true;
                _logWarning?.Invoke("Acquired abandoned mutex during wait.");
                return MutexAcquisitionResult.Abandoned("Acquired abandoned mutex during wait.");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _hasHandle = false;
            _logError?.Invoke($"Access denied when opening mutex: {ex.Message}");
            return MutexAcquisitionResult.Failed(ex);
        }
        catch (WaitHandleCannotBeOpenedException ex)
        {
            _hasHandle = false;
            _logError?.Invoke($"Mutex handle cannot be opened: {ex.Message}");
            return MutexAcquisitionResult.Failed(ex);
        }
        catch (IOException ex)
        {
            _hasHandle = false;
            _logError?.Invoke($"IO error with mutex: {ex.Message}");
            return MutexAcquisitionResult.Failed(ex);
        }
        catch (Exception ex)
        {
            _hasHandle = false;
            _logError?.Invoke($"Unexpected error acquiring mutex: {ex.Message}");
            return MutexAcquisitionResult.Failed(ex);
        }
    }

    public void Release()
    {
        if (_hasHandle && _mutexHandle != null)
        {
            try
            {
                _mutexHandle.ReleaseMutex();
            }
            catch (Exception ex)
            {
                _logWarning?.Invoke($"Exception while releasing mutex: {ex.Message}");
            }
            finally
            {
                _hasHandle = false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Release();
        }
        finally
        {
            _mutexHandle?.Dispose();
            _mutexHandle = null;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
