using System.Diagnostics;

namespace Blocker.Core;

public static class ProtectedProcesses
{
    private static readonly string[] WellKnown =
    [
        "explorer",
        "csrss",
        "winlogon",
        "wininit",
        "services",
        "lsass",
        "svchost",
        "system",
        "smss",
        "dwm",
        "taskmgr",
        "screenclippinghost",
        "snippingtool",
        "audiodg",
        "blocker",
        "blocker.service",
        "sankalpa",
        "sankalpa.service"
    ];

    public static IReadOnlyList<string> Names
    {
        get
        {
            var list = new List<string>(WellKnown);
            string? current = GetCurrentProcessName();
            if (!string.IsNullOrWhiteSpace(current) && !list.Contains(current, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(current);
            }
            return list.AsReadOnly();
        }
    }

    public static bool Contains(string? processName) => IsProtected(processName);

    public static bool IsProtected(string? processName)
    {
        string normalized = ProcessMatcher.NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        foreach (var name in WellKnown)
        {
            if (string.Equals(normalized, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string? current = GetCurrentProcessName();
        if (!string.IsNullOrWhiteSpace(current) &&
            string.Equals(normalized, ProcessMatcher.NormalizeProcessName(current), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string? GetCurrentProcessName()
    {
        try
        {
            return Process.GetCurrentProcess().ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
