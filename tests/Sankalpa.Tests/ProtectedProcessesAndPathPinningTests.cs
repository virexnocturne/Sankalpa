using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Sankalpa.Tests;

public class ProtectedProcessesAndPathPinningTests
{
    private readonly string _winDir = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private readonly string _sys32Dir;

    public ProtectedProcessesAndPathPinningTests()
    {
        _sys32Dir = Path.Combine(_winDir, "System32");
    }

    [Fact]
    public void IsProtected_ValidSystem32Binaries_ReturnsTrue()
    {
        Assert.True(ProtectedProcesses.IsProtected("svchost", Path.Combine(_sys32Dir, "svchost.exe")));
        Assert.True(ProtectedProcesses.IsProtected("csrss", Path.Combine(_sys32Dir, "csrss.exe")));
        Assert.True(ProtectedProcesses.IsProtected("dwm", Path.Combine(_sys32Dir, "dwm.exe")));
        Assert.True(ProtectedProcesses.IsProtected("lsass", Path.Combine(_sys32Dir, "lsass.exe")));
        Assert.True(ProtectedProcesses.IsProtected("services", Path.Combine(_sys32Dir, "services.exe")));
    }

    [Fact]
    public void IsProtected_ImpostersInTempOrUserFolders_ReturnsFalse()
    {
        // Temp folder
        Assert.False(ProtectedProcesses.IsProtected("svchost", Path.Combine(_winDir, "Temp", "svchost.exe")));

        // Traversal attempt
        Assert.False(ProtectedProcesses.IsProtected("svchost", Path.Combine(_sys32Dir, "..", "Temp", "svchost.exe")));

        // User folder
        Assert.False(ProtectedProcesses.IsProtected("svchost", @"C:\Users\Public\svchost.exe"));
        Assert.False(ProtectedProcesses.IsProtected("csrss", @"C:\Users\Public\csrss.exe"));
    }

    [Fact]
    public void IsProtected_Explorer_MustBeDirectlyInWindowsRoot()
    {
        Assert.True(ProtectedProcesses.IsProtected("explorer", Path.Combine(_winDir, "explorer.exe")));
        Assert.False(ProtectedProcesses.IsProtected("explorer", Path.Combine(_sys32Dir, "explorer.exe")));
        Assert.False(ProtectedProcesses.IsProtected("explorer", @"C:\Users\Public\explorer.exe"));
    }

    [Fact]
    public void IsProtected_ProductBinaries_VerifiedAgainstProductDirs()
    {
        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string legitimateApp = Path.Combine(progFiles, "Sankalpa", "Sankalpa.exe");
        string imposterApp = @"C:\Users\Public\Downloads\Sankalpa.exe";

        Assert.True(ProtectedProcesses.IsProtected("sankalpa", legitimateApp));
        Assert.True(ProtectedProcesses.IsOwnProcess("sankalpa", legitimateApp));

        Assert.False(ProtectedProcesses.IsProtected("sankalpa", imposterApp));
        Assert.False(ProtectedProcesses.IsOwnProcess("sankalpa", imposterApp));
    }

    [Fact]
    public void IsProtected_FileNameMismatchWithProcessName_ReturnsFalse()
    {
        // Game or tool renamed to svchost in System32 (if somehow dropped there)
        Assert.False(ProtectedProcesses.IsProtected("svchost", Path.Combine(_sys32Dir, "game.exe")));
    }

    [Fact]
    public void IsProtected_SystemKernelProcess_ReturnsTrueWithoutImagePath()
    {
        Assert.True(ProtectedProcesses.IsProtected("system", null));
        Assert.True(ProtectedProcesses.IsProtected("system", ""));
    }

    [Fact]
    public void IsPathInsideDirectory_ValidSubpathsAndTraversal_BehavesCorrectly()
    {
        string root = @"C:\Users\Alice\AppData\Local\AnthropicClaude";
        string validSub = @"C:\Users\Alice\AppData\Local\AnthropicClaude\app-1.0.42\Claude.exe";
        string outside = @"C:\Users\Alice\.local\bin\claude.exe";
        string traversalOutside = @"C:\Users\Alice\AppData\Local\AnthropicClaude\..\..\..\.local\bin\claude.exe";

        Assert.True(ProtectedProcesses.IsPathInsideDirectory(validSub, root));
        Assert.False(ProtectedProcesses.IsPathInsideDirectory(outside, root));
        Assert.False(ProtectedProcesses.IsPathInsideDirectory(traversalOutside, root));
        Assert.False(ProtectedProcesses.IsPathInsideDirectory("", root));
        Assert.False(ProtectedProcesses.IsPathInsideDirectory(validSub, ""));
    }

    [Fact]
    public void ProcessMatcher_IsBlocked_WithImagePath_ProtectsSystemBinariesAndBlocksImposters()
    {
        var blockedList = new List<string> { "svchost", "chrome", "notepad" };

        string legitimateSvchost = Path.Combine(_sys32Dir, "svchost.exe");
        string fakeSvchost = Path.Combine(_winDir, "Temp", "svchost.exe");

        // Legitimate svchost in System32 is protected -> NOT blocked
        Assert.False(ProcessMatcher.IsBlocked("svchost", blockedList, null, legitimateSvchost));

        // Imposter svchost in Temp is NOT protected -> IS blocked!
        Assert.True(ProcessMatcher.IsBlocked("svchost", blockedList, null, fakeSvchost));
    }

    [Fact]
    public void Worker_IsAppAllowed_WithPathPinning_DistinguishesGuiFromCli()
    {
        var allowedApps = new List<string> { "claude", "vlc", "notepad" };
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = @"C:\Users\Alice\AppData\Local\AnthropicClaude",
            ["vlc"] = @"C:\Program Files\VideoLAN\VLC"
        };

        string guiClaudePath = @"C:\Users\Alice\AppData\Local\AnthropicClaude\app-1.0.99\Claude.exe";
        string cliClaudePath = @"C:\Users\Alice\.local\bin\claude.exe";
        string vlcPath = @"C:\Program Files\VideoLAN\VLC\vlc.exe";
        string fakeVlcPath = @"C:\Temp\vlc.exe";

        // GUI Claude inside pinned root -> Allowed
        Assert.True(Worker.IsAppAllowed("claude", "claude.exe", allowedApps, guiClaudePath, roots));

        // CLI Claude outside pinned root -> NOT Allowed!
        Assert.False(Worker.IsAppAllowed("claude", "claude.exe", allowedApps, cliClaudePath, roots));

        // VLC inside pinned root -> Allowed
        Assert.True(Worker.IsAppAllowed("vlc", "vlc.exe", allowedApps, vlcPath, roots));

        // Fake VLC outside pinned root -> NOT Allowed!
        Assert.False(Worker.IsAppAllowed("vlc", "vlc.exe", allowedApps, fakeVlcPath, roots));

        // App without pinned root (notepad) -> falls back to name matching -> Allowed
        Assert.True(Worker.IsAppAllowed("notepad", "notepad.exe", allowedApps, @"C:\Custom\notepad.exe", roots));

        // App with null imagePath -> falls back to name matching -> Allowed
        Assert.True(Worker.IsAppAllowed("claude", "claude.exe", allowedApps, null, roots));
    }

    [Fact]
    public void Worker_IsDescendedFromAllowedApp_WithPathPinning_RejectsImposterParent()
    {
        var allowedApps = new List<string> { "claude" };
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = @"C:\Users\Alice\AppData\Local\AnthropicClaude"
        };

        var processMap = new Dictionary<int, FastProcessEntry>
        {
            [100] = new FastProcessEntry(100, "claude", "claude.exe", 0), // Root CLI parent
            [101] = new FastProcessEntry(101, "cmd", "cmd.exe", 100),
            [102] = new FastProcessEntry(102, "node", "node.exe", 101)
        };

        // When parent claude (PID 100) is the CLI binary:
        string cliPath = @"C:\Users\Alice\.local\bin\claude.exe";
        string? GetCliImagePath(int pid) => pid == 100 ? cliPath : @"C:\Windows\System32\cmd.exe";

        bool isDescendedFromCli = Worker.IsDescendedFromAllowedApp(101, allowedApps, processMap, GetCliImagePath, roots);
        Assert.False(isDescendedFromCli);

        // When parent claude (PID 100) is the legitimate GUI binary:
        string guiPath = @"C:\Users\Alice\AppData\Local\AnthropicClaude\app-1.0\Claude.exe";
        string? GetGuiImagePath(int pid) => pid == 100 ? guiPath : @"C:\Windows\System32\cmd.exe";

        bool isDescendedFromGui = Worker.IsDescendedFromAllowedApp(101, allowedApps, processMap, GetGuiImagePath, roots);
        Assert.True(isDescendedFromGui);
    }

    [Fact]
    public void ConfirmTimerReadyRequest_Serialization_IncludesAllowedAppRoots()
    {
        var req = new ConfirmTimerReadyRequest
        {
            SessionId = 42,
            VisibleWindowPids = new List<int> { 1234, 5678 },
            AllowedAppRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = @"C:\Users\Alice\AppData\Local\AnthropicClaude",
                ["vlc"] = @"C:\Program Files\VideoLAN\VLC"
            }
        };

        string json = JsonSerializer.Serialize(req);
        var deserialized = JsonSerializer.Deserialize<ConfirmTimerReadyRequest>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(42, deserialized.SessionId);
        Assert.Equal(2, deserialized.VisibleWindowPids.Count);
        Assert.Equal(2, deserialized.AllowedAppRoots.Count);
        Assert.Equal(@"C:\Users\Alice\AppData\Local\AnthropicClaude", deserialized.AllowedAppRoots["claude"]);
        Assert.Equal(@"C:\Program Files\VideoLAN\VLC", deserialized.AllowedAppRoots["vlc"]);
    }
}
