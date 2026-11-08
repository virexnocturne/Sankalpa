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

    private static readonly HashSet<string> WellKnownSet = new(WellKnown, StringComparer.OrdinalIgnoreCase);
    private static readonly string? OwnProcessName;
    private static readonly string? OwnNormalizedProcessName;
    private static readonly IReadOnlyList<string> CachedNames;

    static ProtectedProcesses()
    {
        OwnProcessName = GetCurrentProcessName();
        OwnNormalizedProcessName = !string.IsNullOrEmpty(OwnProcessName)
            ? ProcessMatcher.NormalizeProcessName(OwnProcessName)
            : null;

        var list = new List<string>(WellKnown);
        if (!string.IsNullOrWhiteSpace(OwnProcessName) && !list.Contains(OwnProcessName, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(OwnProcessName);
        }
        CachedNames = list.AsReadOnly();
    }

    public static IReadOnlyList<string> Names => CachedNames;

    public static bool Contains(string? processName) => IsProtected(processName);

    public static bool IsProtected(string? processName)
    {
        string normalized = ProcessMatcher.NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        if (WellKnownSet.Contains(normalized))
        {
            return true;
        }

        if (OwnNormalizedProcessName != null &&
            string.Equals(normalized, OwnNormalizedProcessName, StringComparison.OrdinalIgnoreCase))
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
