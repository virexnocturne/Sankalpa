using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Blocker.Core;

public class ProcessSuspender : IDisposable
{
    private const int PROCESS_SUSPEND_RESUME = 0x0800;

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private readonly ConcurrentDictionary<int, string> _suspendedProcesses = new();

    public static ProcessSuspender Default { get; } = new();

    public static Action<int>? OnMeditationSuspensionSummary { get; set; }

    public IReadOnlyCollection<int> SuspendedPids => (IReadOnlyCollection<int>)_suspendedProcesses.Keys;

    public bool IsSuspended(int pid) => _suspendedProcesses.ContainsKey(pid);

    public bool SuspendProcess(int pid, string name)
    {
        if (pid <= 4)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                name = proc.ProcessName;
            }
            catch
            {
                return false;
            }
        }

        if (ProtectedProcesses.IsProtected(name))
        {
            return false;
        }

        if (_suspendedProcesses.ContainsKey(pid))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            int status = NtSuspendProcess(handle);
            if (status == 0) // STATUS_SUCCESS
            {
                _suspendedProcesses[pid] = name;
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    public int SuspendProcesses(IEnumerable<int> pids)
    {
        if (pids == null) return 0;

        int count = 0;
        foreach (var pid in pids)
        {
            if (SuspendProcess(pid, string.Empty))
            {
                count++;
            }
        }
        return count;
    }

    public bool ResumeProcess(int pid)
    {
        if (!_suspendedProcesses.TryRemove(pid, out _))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
            if (handle == IntPtr.Zero)
            {
                return true;
            }

            int status = NtResumeProcess(handle);
            return status == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    public int ResumeAll()
    {
        int count = 0;
        var pids = _suspendedProcesses.Keys.ToList();
        foreach (var pid in pids)
        {
            if (ResumeProcess(pid))
            {
                count++;
            }
        }
        return count;
    }

    public void Dispose()
    {
        ResumeAll();
    }
}
