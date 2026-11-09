using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Blocker.Core;

public class ProcessSuspender : IDisposable
{
    public record Entry(SafeProcessHandle Handle, string Name, DateTime StartUtc, bool FromAllowedApp);

    private const int PROCESS_TERMINATE = 0x0001;
    private const int PROCESS_SUSPEND_RESUME = 0x0800;
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int PROCESS_SUSPEND_WITH_TERMINATE = PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE; // 0x1801
    private const int PROCESS_SUSPEND_QUERY = PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION; // 0x1800
    private const int ERROR_ACCESS_DENIED = 5;
    private const uint STILL_ACTIVE = 259;

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(int processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle hProcess, int dwFlags, [Out] StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle hProcess,
        out long lpCreationTime,
        out long lpExitTime,
        out long lpKernelTime,
        out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    private readonly ConcurrentDictionary<int, Entry> _suspendedProcesses = new();

    public static ProcessSuspender Default { get; } = new();

    public static Action<int>? OnMeditationSuspensionSummary { get; set; }

    public IReadOnlyCollection<int> SuspendedPids => (IReadOnlyCollection<int>)_suspendedProcesses.Keys;

    public bool IsSuspended(int pid) => _suspendedProcesses.ContainsKey(pid);

    internal void AddTestEntry(int pid, Entry entry) => _suspendedProcesses[pid] = entry;

    public bool SuspendProcess(int pid, string name, bool fromAllowedApp = false)
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

        SafeProcessHandle handle = OpenProcess(PROCESS_SUSPEND_WITH_TERMINATE, false, pid);
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (err == ERROR_ACCESS_DENIED)
            {
                handle = OpenProcess(PROCESS_SUSPEND_QUERY, false, pid);
                fromAllowedApp = false;
            }
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            string? path = null;
            if (QueryFullProcessImageName(handle, 0, sb, ref size))
            {
                path = sb.ToString();
            }

            if (string.IsNullOrEmpty(path))
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    path = proc.MainModule?.FileName;
                }
                catch { }
            }

            // Defense-in-depth: run SystemHelperGuard.IsTrusted(path). If trusted, do NOT suspend and return false.
            if (!string.IsNullOrEmpty(path) && SystemHelperGuard.IsTrusted(path))
            {
                handle.Dispose();
                return false;
            }

            DateTime startUtc = DateTime.UtcNow;
            if (GetProcessTimes(handle, out long creationTime, out _, out _, out _) && creationTime > 0)
            {
                try
                {
                    startUtc = DateTime.FromFileTimeUtc(creationTime);
                }
                catch
                {
                    startUtc = DateTime.UtcNow;
                }
            }

            int status = NtSuspendProcess(handle.DangerousGetHandle());
            if (status == 0) // STATUS_SUCCESS
            {
                var entry = new Entry(handle, name, startUtc, fromAllowedApp);
                if (_suspendedProcesses.TryAdd(pid, entry))
                {
                    return true;
                }

                handle.Dispose();
                return true;
            }

            handle.Dispose();
            return false;
        }
        catch
        {
            handle.Dispose();
            return false;
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

    public static bool ShouldTerminate(Entry e, DateTime sessionStart) => e.FromAllowedApp && e.StartUtc >= sessionStart;

    public (int Resumed, int Terminated) FinishSession(DateTime sessionStartUtc)
    {
        int r = 0, t = 0;
        foreach (var pid in _suspendedProcesses.Keys.ToList())
        {
            if (!_suspendedProcesses.TryRemove(pid, out var e)) continue;
            using (e.Handle)
            {
                var h = e.Handle.DangerousGetHandle();
                if (ShouldTerminate(e, sessionStartUtc) && TerminateProcess(h, 1))
                {
                    t++;
                }
                else if (NtResumeProcess(h) == 0)
                {
                    r++;
                }
            }
        }
        return (r, t);
    }

    public int PruneExited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        int pruned = 0;
        foreach (var pid in _suspendedProcesses.Keys.ToList())
        {
            if (!_suspendedProcesses.TryGetValue(pid, out var e)) continue;

            bool exited = false;
            if (e.Handle == null || e.Handle.IsInvalid || e.Handle.IsClosed)
            {
                exited = true;
            }
            else
            {
                var h = e.Handle.DangerousGetHandle();
                if (!GetExitCodeProcess(h, out uint exitCode) || exitCode != STILL_ACTIVE || WaitForSingleObject(h, 0) == 0)
                {
                    exited = true;
                }
            }

            if (exited)
            {
                if (_suspendedProcesses.TryRemove(pid, out var removed))
                {
                    removed.Handle?.Dispose();
                    pruned++;
                }
            }
        }
        return pruned;
    }

    public bool ResumeProcess(int pid)
    {
        if (!_suspendedProcesses.TryRemove(pid, out var entry))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            entry.Handle?.Dispose();
            return false;
        }

        using (entry.Handle)
        {
            try
            {
                if (entry.Handle != null && !entry.Handle.IsInvalid && !entry.Handle.IsClosed)
                {
                    int status = NtResumeProcess(entry.Handle.DangerousGetHandle());
                    return status == 0;
                }

                using var handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
                if (handle.IsInvalid)
                {
                    return true;
                }

                int resumeStatus = NtResumeProcess(handle.DangerousGetHandle());
                return resumeStatus == 0;
            }
            catch
            {
                return false;
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
