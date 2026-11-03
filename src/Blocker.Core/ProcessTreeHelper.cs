using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Blocker.Core;

public static class ProcessTreeHelper
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
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

    public static HashSet<int> GetCurrentAndAncestorProcessIds()
    {
        var ancestors = new HashSet<int>();
        int currentPid = Environment.ProcessId;
        ancestors.Add(currentPid);

        try
        {
            var parentMap = new Dictionary<int, int>();
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1))
            {
                try
                {
                    var entry = new PROCESSENTRY32();
                    entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
                    if (Process32First(snapshot, ref entry))
                    {
                        do
                        {
                            parentMap[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                        } while (Process32Next(snapshot, ref entry));
                    }
                }
                finally
                {
                    CloseHandle(snapshot);
                }

                int pid = currentPid;
                while (parentMap.TryGetValue(pid, out int parentPid) && parentPid > 0 && parentPid != pid)
                {
                    if (!ancestors.Add(parentPid))
                    {
                        break; // Avoid infinite loop on cyclic/corrupted parent references
                    }
                    pid = parentPid;
                }
            }
        }
        catch
        {
            // Fail safe with currentPid
        }

        return ancestors;
    }

    public static (int BrowserPid, string BrowserExeName)? ResolveAncestorBrowser(
        int pid,
        IReadOnlyDictionary<int, int> parentMap,
        IReadOnlyDictionary<int, string> nameMap,
        ISet<string>? knownBrowserExes = null)
    {
        var browserExes = knownBrowserExes ?? new HashSet<string>(
            BrowserCatalog.SupportedBrowsers.Select(b => b.ExeName),
            StringComparer.OrdinalIgnoreCase);

        var visited = new HashSet<int>();
        int cur = pid;
        while (parentMap.TryGetValue(cur, out int parentPid) && parentPid > 0 && visited.Add(parentPid))
        {
            if (nameMap.TryGetValue(parentPid, out var parentName))
            {
                if (browserExes.Contains(parentName))
                {
                    return (parentPid, parentName);
                }
            }
            cur = parentPid;
        }

        // If no known browser ancestor was found, return immediate parent if available
        if (parentMap.TryGetValue(pid, out int immediateParentPid) && immediateParentPid > 0)
        {
            string immediateParentName = nameMap.TryGetValue(immediateParentPid, out var name) ? name : string.Empty;
            return (immediateParentPid, immediateParentName);
        }

        return null;
    }

    public static (int ParentPid, string ParentExeName)? GetParentProcessInfo(int pid)
    {
        return GetAncestorBrowserInfo(pid);
    }

    public static (int BrowserPid, string BrowserExeName)? GetAncestorBrowserInfo(int pid, IEnumerable<string>? knownBrowserExes = null)
    {
        try
        {
            var parentMap = new Dictionary<int, int>();
            var nameMap = new Dictionary<int, string>();

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1))
            {
                try
                {
                    var entry = new PROCESSENTRY32();
                    entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
                    if (Process32First(snapshot, ref entry))
                    {
                        do
                        {
                            int pId = (int)entry.th32ProcessID;
                            parentMap[pId] = (int)entry.th32ParentProcessID;
                            nameMap[pId] = entry.szExeFile;
                        } while (Process32Next(snapshot, ref entry));
                    }
                }
                finally
                {
                    CloseHandle(snapshot);
                }

                var browserExes = knownBrowserExes != null
                    ? new HashSet<string>(knownBrowserExes, StringComparer.OrdinalIgnoreCase)
                    : null;

                return ResolveAncestorBrowser(pid, parentMap, nameMap, browserExes);
            }
        }
        catch
        {
            // Fail safe
        }

        return null;
    }
}
