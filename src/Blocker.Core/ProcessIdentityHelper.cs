using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Blocker.Core;

/// <summary>
/// Native Win32 process identity, priority, EcoQoS, memory priority, and working-set helpers.
/// Provides safe, unified access to kernel32 and psapi process management APIs.
/// </summary>
public static class ProcessIdentityHelper
{
    public const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const int PROCESS_QUERY_INFORMATION = 0x0400;
    public const int PROCESS_SET_INFORMATION = 0x0200;
    public const int PROCESS_SET_QUOTA = 0x0100;

    public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    public const uint IDLE_PRIORITY_CLASS = 0x00000040;

    public const int ProcessMemoryPriority = 9;
    public const int ProcessPowerThrottling = 4;

    public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    public const uint MEMORY_PRIORITY_VERY_LOW = 1;
    public const uint MEMORY_PRIORITY_LOW = 2;
    public const uint MEMORY_PRIORITY_MEDIUM = 3;
    public const uint MEMORY_PRIORITY_BELOW_NORMAL = 4;
    public const uint MEMORY_PRIORITY_NORMAL = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_PRIORITY_INFORMATION
    {
        public uint MemoryPriority;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(
        IntPtr hProcess,
        out long lpCreationTime,
        out long lpExitTime,
        out long lpKernelTime,
        out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        int dwFlags,
        [Out] StringBuilder lpExeName,
        ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation,
        uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref MEMORY_PRIORITY_INFORMATION processInformation,
        uint processInformationSize);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessWorkingSetSize(
        IntPtr hProcess,
        IntPtr dwMinimumWorkingSetSize,
        IntPtr dwMaximumWorkingSetSize);

    /// <summary>
    /// Reads creation time and image path for a process in a single OpenProcess call.
    /// Silently handles access denied or non-Windows platforms.
    /// </summary>
    public static bool GetProcessIdentity(int processId, out long creationTime, out string? imagePath)
    {
        creationTime = 0;
        imagePath = null;

        if (processId <= 4 || !OperatingSystem.IsWindows())
        {
            return false;
        }

        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            // Silently return false for unopenable / elevated / protected processes
            return false;
        }

        try
        {
            if (GetProcessTimes(hProcess, out long cTime, out _, out _, out _))
            {
                creationTime = cTime;
            }

            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
            {
                imagePath = sb.ToString();
            }

            return creationTime > 0 || !string.IsNullOrEmpty(imagePath);
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Enables or disables EcoQoS (Execution Speed Power Throttling) on a process handle.
    /// </summary>
    public static bool SetEcoQoS(IntPtr hProcess, bool enable)
    {
        if (!OperatingSystem.IsWindows() || hProcess == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enable ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
            };

            return SetProcessInformation(
                hProcess,
                ProcessPowerThrottling,
                ref state,
                (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets the memory priority on a process handle (e.g. MEMORY_PRIORITY_LOW or MEMORY_PRIORITY_NORMAL).
    /// </summary>
    public static bool SetMemoryPriority(IntPtr hProcess, uint memoryPriority)
    {
        if (!OperatingSystem.IsWindows() || hProcess == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var info = new MEMORY_PRIORITY_INFORMATION
            {
                MemoryPriority = memoryPriority
            };

            return SetProcessInformation(
                hProcess,
                ProcessMemoryPriority,
                ref info,
                (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Trims the working set of a process to release unused physical RAM back to the OS.
    /// </summary>
    public static bool TrimWorkingSet(IntPtr hProcess)
    {
        if (!OperatingSystem.IsWindows() || hProcess == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (EmptyWorkingSet(hProcess))
            {
                return true;
            }
        }
        catch { }

        try
        {
            return SetProcessWorkingSetSize(hProcess, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Instantly restores a process to normal operation: Normal/original priority, Normal memory priority, and disables EcoQoS.
    /// Verifies process identity before modifying state.
    /// </summary>
    public static bool RestoreProcess(int processId, long expectedCreationTime = 0, string? expectedImagePath = null, uint originalPriority = NORMAL_PRIORITY_CLASS)
    {
        if (processId <= 4 || !OperatingSystem.IsWindows())
        {
            return false;
        }

        int access = PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION;
        IntPtr hProcess = OpenProcess(access, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // Verify identity if expected creation time or image path was specified
            if (expectedCreationTime > 0)
            {
                if (!GetProcessTimes(hProcess, out long actualCreationTime, out _, out _, out _) || actualCreationTime != expectedCreationTime)
                {
                    // PID was reused by another process! Do not touch!
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(expectedImagePath))
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                {
                    if (!string.Equals(sb.ToString(), expectedImagePath, StringComparison.OrdinalIgnoreCase))
                    {
                        // Image path mismatch! Do not touch!
                        return false;
                    }
                }
            }

            // 1. Restore priority class
            uint targetPriority = (originalPriority != 0 && originalPriority != IDLE_PRIORITY_CLASS)
                ? originalPriority
                : NORMAL_PRIORITY_CLASS;
            SetPriorityClass(hProcess, targetPriority);

            // 2. Restore memory priority to Normal (5)
            SetMemoryPriority(hProcess, MEMORY_PRIORITY_NORMAL);

            // 3. Disable EcoQoS
            SetEcoQoS(hProcess, false);

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }
}
