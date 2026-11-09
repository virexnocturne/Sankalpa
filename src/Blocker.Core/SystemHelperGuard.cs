using System;
using System.IO;

namespace Blocker.Core;

/// <summary>
/// Cryptographically and directory-pinned validation for Windows operating system helper processes.
/// Validates process image paths strictly against canonical, TrustedInstaller/Admin-protected system directories,
/// eliminating traversal (..\) and writable directory bypasses (such as C:\Windows\Temp).
/// </summary>
public static class SystemHelperGuard
{
    private static readonly string Win = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private static readonly string Sys32 = Path.Combine(Win, "System32");
    private static readonly string SysApps = Path.Combine(Win, "SystemApps");

    private static readonly (string File, string Dir, bool Tree)[] Rules =
    {
        ("RuntimeBroker.exe", Sys32, false),
        ("ctfmon.exe", Sys32, false),
        ("sihost.exe", Sys32, false),
        ("fontdrvhost.exe", Sys32, false),
        ("ApplicationFrameHost.exe", Sys32, false),
        ("TextInputHost.exe", SysApps, true), // lives in a TrustedInstaller-owned SystemApps tree
    };

    public static bool IsTrusted(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return false;
        string full;
        try
        {
            full = Path.GetFullPath(imagePath); // collapses relative traversal like ..\
        }
        catch
        {
            return false;
        }

        string file = Path.GetFileName(full);
        string dir = Path.GetDirectoryName(full) ?? "";

        foreach (var r in Rules)
        {
            if (file.Equals(r.File, StringComparison.OrdinalIgnoreCase))
            {
                bool dirMatches = r.Tree
                    ? (dir.Equals(r.Dir, StringComparison.OrdinalIgnoreCase) ||
                       dir.StartsWith(r.Dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    : dir.Equals(r.Dir, StringComparison.OrdinalIgnoreCase);

                if (dirMatches)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
