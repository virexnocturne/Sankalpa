using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Blocker.Core;

public readonly record struct FastProcessEntry(int Id, string ProcessName, string ExeName);

/// <summary>
/// High-performance zero-allocation process enumerator using Windows Toolhelp32 snapshot API
/// with cached string interning. Avoids allocating hundreds of managed Process objects every tick.
/// </summary>
public static class FastProcessEnumerator
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    /// <summary>
    /// Enumerates currently running processes into the provided destination list.
    /// Reuses existing strings from nameCache to achieve near-zero GC allocations.
    /// Falls back safely to Process.GetProcesses() if native snapshot fails or on non-Windows platforms.
    /// </summary>
    public static bool EnumerateProcesses(
        List<FastProcessEntry> destination,
        HashSet<string>? runningNames = null,
        Dictionary<string, (string ProcessName, string ExeName)>? nameCache = null)
    {
        destination.Clear();
        runningNames?.Clear();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snapshot != IntPtr.Zero && snapshot != INVALID_HANDLE_VALUE)
                {
                    try
                    {
                        var entry = new PROCESSENTRY32();
                        entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();

                        if (Process32First(snapshot, ref entry))
                        {
                            do
                            {
                                string rawExe = entry.szExeFile;
                                if (string.IsNullOrEmpty(rawExe)) continue;

                                string procName;
                                string exeName;

                                if (nameCache != null)
                                {
                                    if (!nameCache.TryGetValue(rawExe, out var tuple))
                                    {
                                        procName = rawExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                            ? rawExe.Substring(0, rawExe.Length - 4)
                                            : rawExe;
                                        exeName = rawExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                            ? rawExe
                                            : rawExe + ".exe";
                                        tuple = (procName, exeName);
                                        nameCache[rawExe] = tuple;
                                    }
                                    procName = tuple.ProcessName;
                                    exeName = tuple.ExeName;
                                }
                                else
                                {
                                    procName = rawExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                        ? rawExe.Substring(0, rawExe.Length - 4)
                                        : rawExe;
                                    exeName = rawExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                        ? rawExe
                                        : rawExe + ".exe";
                                }

                                destination.Add(new FastProcessEntry((int)entry.th32ProcessID, procName, exeName));
                                runningNames?.Add(procName);
                            } while (Process32Next(snapshot, ref entry));
                        }
                        return true;
                    }
                    finally
                    {
                        CloseHandle(snapshot);
                    }
                }
            }
            catch
            {
                // Fall back to managed enumeration below
            }
        }

        // Managed fallback
        try
        {
            var procs = Process.GetProcesses();
            foreach (var p in procs)
            {
                try
                {
                    int id = p.Id;
                    string name = p.ProcessName;
                    string exe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
                    destination.Add(new FastProcessEntry(id, name, exe));
                    runningNames?.Add(name);
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Convenience helper to return a standalone list of running processes.
    /// </summary>
    public static List<FastProcessEntry> GetProcesses()
    {
        var list = new List<FastProcessEntry>(512);
        EnumerateProcesses(list);
        return list;
    }
}
