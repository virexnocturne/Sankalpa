using System;
using System.IO;
using Blocker.Core;
using Xunit;

namespace Sankalpa.Tests;

public class SystemHelperGuardTests
{
    [Fact]
    public void IsTrusted_CorrectSystem32Paths_ReturnsTrue()
    {
        string win = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        string sys32 = Path.Combine(win, "System32");

        Assert.True(SystemHelperGuard.IsTrusted(Path.Combine(sys32, "ctfmon.exe")));
        Assert.True(SystemHelperGuard.IsTrusted(Path.Combine(sys32, "RuntimeBroker.exe")));
        Assert.True(SystemHelperGuard.IsTrusted(Path.Combine(sys32, "sihost.exe")));
        Assert.True(SystemHelperGuard.IsTrusted(Path.Combine(sys32, "fontdrvhost.exe")));
        Assert.True(SystemHelperGuard.IsTrusted(Path.Combine(sys32, "ApplicationFrameHost.exe")));
    }

    [Fact]
    public void IsTrusted_CaseVariants_ReturnsTrue()
    {
        string win = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        string sys32 = Path.Combine(win, "System32");

        Assert.True(SystemHelperGuard.IsTrusted(Path.Combine(sys32, "CTFMON.EXE")));
        Assert.True(SystemHelperGuard.IsTrusted(Path.Combine(sys32, "runtimebroker.exe")));
    }

    [Fact]
    public void IsTrusted_SystemAppsTree_ReturnsTrue()
    {
        string win = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        string sysAppsSubdir = Path.Combine(win, "SystemApps", "MicrosoftWindows.Client.CBS_cw5n1h2txyewy");

        Assert.True(SystemHelperGuard.IsTrusted(Path.Combine(sysAppsSubdir, "TextInputHost.exe")));
    }

    [Fact]
    public void IsTrusted_TempOrUserWritableLocations_ReturnsFalse()
    {
        string win = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        // Windows Temp
        Assert.False(SystemHelperGuard.IsTrusted(Path.Combine(win, "Temp", "ctfmon.exe")));

        // Traversal trick: C:\Windows\System32\..\Temp\ctfmon.exe
        Assert.False(SystemHelperGuard.IsTrusted(Path.Combine(win, "System32", "..", "Temp", "ctfmon.exe")));

        // Subdirectory of System32 where helper should not be
        Assert.False(SystemHelperGuard.IsTrusted(Path.Combine(win, "System32", "spool", "ctfmon.exe")));

        // User profile
        string userProfile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Assert.False(SystemHelperGuard.IsTrusted(Path.Combine(userProfile, "ctfmon.exe")));
    }

    [Fact]
    public void IsTrusted_InvalidOrDoubleExtension_ReturnsFalse()
    {
        string win = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        string sys32 = Path.Combine(win, "System32");

        Assert.False(SystemHelperGuard.IsTrusted(Path.Combine(sys32, "ctfmon.exe.exe")));
        Assert.False(SystemHelperGuard.IsTrusted(Path.Combine(sys32, "fake_ctfmon.exe")));
        Assert.False(SystemHelperGuard.IsTrusted(""));
        Assert.False(SystemHelperGuard.IsTrusted(null));
    }
}
