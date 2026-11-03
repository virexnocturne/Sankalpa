using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Blocker.App;
using Blocker.Core;
using Xunit;

namespace Blocker.Tests;

public class MainUiEnhancementTests
{
    private class MockPipeClient : IPipeClient
    {
        public List<Request> RecordedRequests { get; } = new();
        public Func<Request, Response>? Handler { get; set; }

        public Task<PipeResult> SendRequestAsync(Request request, CancellationToken cancellationToken = default)
        {
            RecordedRequests.Add(request);
            var response = Handler != null ? Handler(request) : Response.Success();
            return Task.FromResult(new PipeResult
            {
                Connected = true,
                Response = response
            });
        }
    }

    [Fact]
    public void ProfileViewModel_BeginEdit_SetsIsEditingAndCopiesName()
    {
        var profile = new ProfileViewModel
        {
            Name = "Deep Work"
        };

        Assert.False(profile.IsEditingName);
        Assert.Equal("Deep Work", profile.EditingName);

        profile.BeginEditName();

        Assert.True(profile.IsEditingName);
        Assert.Equal("Deep Work", profile.EditingName);
    }

    [Fact]
    public void ProfileViewModel_CancelEdit_RevertsAndExitsEditMode()
    {
        var profile = new ProfileViewModel
        {
            Name = "Deep Work"
        };
        profile.BeginEditName();
        profile.EditingName = "Temporary Typing";

        profile.CancelEditName();

        Assert.False(profile.IsEditingName);
        Assert.Equal("Deep Work", profile.EditingName);
        Assert.Equal("Deep Work", profile.Name);
    }

    [Fact]
    public void ProfileViewModel_StandaloneCommit_TrimsAndUpdates()
    {
        var profile = new ProfileViewModel
        {
            Name = "Study"
        };
        profile.BeginEditName();
        profile.EditingName = "   Study Session   ";

        profile.SaveNameCommand.Execute(null);

        Assert.False(profile.IsEditingName);
        Assert.Equal("Study Session", profile.Name);
        Assert.Equal("Study Session", profile.EditingName);
    }

    [Fact]
    public void ProfileViewModel_StandaloneCommit_EmptyOrWhitespace_Reverts()
    {
        var profile = new ProfileViewModel
        {
            Name = "Gaming"
        };
        profile.BeginEditName();
        profile.EditingName = "    ";

        profile.SaveNameCommand.Execute(null);

        Assert.False(profile.IsEditingName);
        Assert.Equal("Gaming", profile.Name);
        Assert.Equal("Gaming", profile.EditingName);
    }

    [Fact]
    public async Task MainViewModel_CommitRename_TrimsAndSavesValidName()
    {
        var mockPipe = new MockPipeClient();
        var mainVm = new MainViewModel(mockPipe);

        var profile = new ProfileViewModel
        {
            Id = 42,
            Name = "Old Block"
        };
        mainVm.Profiles.Add(profile);

        profile.BeginEditName();
        profile.EditingName = "   New Productive Block   ";

        bool ok = await mainVm.CommitRenameProfileAsync(profile, profile.EditingName);

        Assert.True(ok);
        Assert.Equal("New Productive Block", profile.Name);
        Assert.False(profile.IsEditingName);
        Assert.Null(mainVm.ErrorMessage);
        Assert.Contains("Renamed block", mainVm.StatusMessage);

        var renameReq = mockPipe.RecordedRequests.FirstOrDefault(r => r.Command == CommandNames.RenameProfile);
        Assert.NotNull(renameReq);
        Assert.Equal("42", renameReq.Args?["id"]);
        Assert.Equal("New Productive Block", renameReq.Args?["name"]);
        Assert.Equal("Old Block", renameReq.Args?["old_name"]);
    }

    [Fact]
    public async Task MainViewModel_CommitRename_RejectsEmptyOrWhitespace()
    {
        var mockPipe = new MockPipeClient();
        var mainVm = new MainViewModel(mockPipe);

        var profile = new ProfileViewModel
        {
            Id = 10,
            Name = "Original"
        };
        mainVm.Profiles.Add(profile);

        profile.BeginEditName();
        profile.EditingName = "    ";

        bool ok = await mainVm.CommitRenameProfileAsync(profile, profile.EditingName);

        Assert.False(ok);
        Assert.Equal("Original", profile.Name);
        Assert.Equal("Original", profile.EditingName);
        Assert.False(profile.IsEditingName);
        Assert.Contains("cannot be empty", mainVm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mockPipe.RecordedRequests);
    }

    [Fact]
    public async Task MainViewModel_CommitRename_RejectsDuplicateName()
    {
        var mockPipe = new MockPipeClient();
        var mainVm = new MainViewModel(mockPipe);

        var p1 = new ProfileViewModel { Id = 1, Name = "Deep Work" };
        var p2 = new ProfileViewModel { Id = 2, Name = "Study" };
        mainVm.Profiles.Add(p1);
        mainVm.Profiles.Add(p2);

        p2.BeginEditName();
        p2.EditingName = "deep work"; // Case-insensitive duplicate check

        bool ok = await mainVm.CommitRenameProfileAsync(p2, p2.EditingName);

        Assert.False(ok);
        Assert.Equal("Study", p2.Name);
        Assert.Equal("Study", p2.EditingName);
        Assert.False(p2.IsEditingName);
        Assert.Contains("already exists", mainVm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mockPipe.RecordedRequests);
    }

    [Fact]
    public async Task MainViewModel_CommitRename_SameName_ExitsEditModeWithoutNetworkCall()
    {
        var mockPipe = new MockPipeClient();
        var mainVm = new MainViewModel(mockPipe);

        var profile = new ProfileViewModel { Id = 5, Name = "Coding" };
        mainVm.Profiles.Add(profile);

        profile.BeginEditName();
        profile.EditingName = "  Coding  "; // Trims to same name

        bool ok = await mainVm.CommitRenameProfileAsync(profile, profile.EditingName);

        Assert.True(ok);
        Assert.Equal("Coding", profile.Name);
        Assert.False(profile.IsEditingName);
        Assert.Empty(mockPipe.RecordedRequests);
    }

    [Fact]
    public void MainViewModel_RenameProfileCommand_TriggersCommitFromViewModel()
    {
        var mockPipe = new MockPipeClient();
        var mainVm = new MainViewModel(mockPipe);

        var profile = new ProfileViewModel { Id = 8, Name = "Before Rename" };
        mainVm.Profiles.Add(profile);

        profile.BeginEditName();
        profile.EditingName = "After Rename";

        mainVm.RenameProfileCommand.Execute(profile);

        var req = mockPipe.RecordedRequests.FirstOrDefault(r => r.Command == CommandNames.RenameProfile);
        Assert.NotNull(req);
        Assert.Equal("After Rename", req.Args?["name"]);
    }

    [Fact]
    public void TabRouting_MainViewModel_OpenAppsTabCommand_RoutesToTab0()
    {
        var mainVm = new MainViewModel(new MockPipeClient());
        var profile = new ProfileViewModel { Name = "TabTest" };
        mainVm.Profiles.Add(profile);

        ProfileViewModel? targetProfile = null;
        int openedTab = -1;
        mainVm.RequestOpenEditorDialogWithTab = (p, tab) =>
        {
            targetProfile = p;
            openedTab = tab;
        };

        mainVm.OpenAppsTabCommand.Execute(profile);

        Assert.Same(profile, targetProfile);
        Assert.Equal(0, openedTab);
    }

    [Fact]
    public void TabRouting_MainViewModel_OpenSitesTabCommand_RoutesToTab1()
    {
        var mainVm = new MainViewModel(new MockPipeClient());
        var profile = new ProfileViewModel { Name = "TabTest" };
        mainVm.Profiles.Add(profile);

        ProfileViewModel? targetProfile = null;
        int openedTab = -1;
        mainVm.RequestOpenEditorDialogWithTab = (p, tab) =>
        {
            targetProfile = p;
            openedTab = tab;
        };

        mainVm.OpenSitesTabCommand.Execute(profile);

        Assert.Same(profile, targetProfile);
        Assert.Equal(1, openedTab);
    }

    [Fact]
    public void TabRouting_MainViewModel_OpenScheduleTabCommand_RoutesToTab2()
    {
        var mainVm = new MainViewModel(new MockPipeClient());
        var profile = new ProfileViewModel { Name = "TabTest" };
        mainVm.Profiles.Add(profile);

        ProfileViewModel? targetProfile = null;
        int openedTab = -1;
        mainVm.RequestOpenEditorDialogWithTab = (p, tab) =>
        {
            targetProfile = p;
            openedTab = tab;
        };

        mainVm.OpenScheduleTabCommand.Execute(profile);

        Assert.Same(profile, targetProfile);
        Assert.Equal(2, openedTab);
    }

    [Fact]
    public void TabRouting_ProfileViewModel_OpenAppsAndSitesCommands_RouteCorrectly()
    {
        var mainVm = new MainViewModel(new MockPipeClient());
        var profile = new ProfileViewModel { Name = "BadgeClickTest" };
        mainVm.Profiles.Add(profile); // Wires up RequestOpenTab

        ProfileViewModel? targetProfile = null;
        int openedTab = -1;
        mainVm.RequestOpenEditorDialogWithTab = (p, tab) =>
        {
            targetProfile = p;
            openedTab = tab;
        };

        // Click on Apps badge button
        profile.OpenAppsCommand.Execute(null);
        Assert.Same(profile, targetProfile);
        Assert.Equal(0, openedTab);

        // Click on Sites badge button
        profile.OpenSitesCommand.Execute(null);
        Assert.Same(profile, targetProfile);
        Assert.Equal(1, openedTab);
    }

    [Fact]
    public void ProfileViewModel_Badges_ReflectAppsSitesAndExceptions()
    {
        var profile = new ProfileViewModel();

        // 0 items
        Assert.Equal("0 apps", profile.AppsBadgeText);
        Assert.Equal("0 sites", profile.SitesBadgeText);
        Assert.False(profile.HasExceptions);

        // Add 1 app
        profile.BlockedProcesses.Add("notepad.exe");
        Assert.Equal("1 app", profile.AppsBadgeText);

        // Add 2nd app
        profile.BlockedProcesses.Add("calc.exe");
        Assert.Equal("2 apps", profile.AppsBadgeText);

        // Add 1 site
        profile.WebRules.Add(new WebRule { Pattern = "reddit.com", Allow = false });
        Assert.Equal("1 site", profile.SitesBadgeText);

        // Add 2nd site
        profile.WebRules.Add(new WebRule { Pattern = "youtube.com", Allow = false });
        Assert.Equal("2 sites", profile.SitesBadgeText);

        // Add 1 exception
        profile.WebRules.Add(new WebRule { Pattern = "reddit.com/r/csharp", Allow = true });
        Assert.True(profile.HasExceptions);
        Assert.Equal("1 exception", profile.ExceptionsBadgeText);

        // Block all websites mode
        profile.BlockAllWebsites = true;
        Assert.Equal("all sites", profile.SitesBadgeText);
    }

    [Fact]
    public void BlockEditorDialog_InitialTab_SelectsAppsOrWebsites()
    {
        RunOnStaThread(() =>
        {
            var profile = new ProfileViewModel { Name = "DialogTabTest" };

            // Tab 0: Applications
            var dlgApps = new BlockEditorDialog(profile, initialTab: 0);
            Assert.Equal(0, dlgApps.SelectedTabIndex);
            Assert.Equal(Visibility.Visible, dlgApps.AppsVisibility);
            Assert.Equal(Visibility.Collapsed, dlgApps.WebsitesVisibility);

            // Tab 1: Websites
            var dlgWebsites = new BlockEditorDialog(profile, initialTab: 1);
            Assert.Equal(1, dlgWebsites.SelectedTabIndex);
            Assert.Equal(Visibility.Collapsed, dlgWebsites.AppsVisibility);
            Assert.Equal(Visibility.Visible, dlgWebsites.WebsitesVisibility);

            // Tab 2: Schedule
            var dlgSchedule = new BlockEditorDialog(profile, initialTab: 2);
            Assert.Equal(2, dlgSchedule.SelectedTabIndex);
            Assert.Equal(Visibility.Visible, dlgSchedule.ScheduleVisibility);
            Assert.Equal(Visibility.Collapsed, dlgSchedule.AppsVisibility);

            // Tab 3: Lock
            var dlgLock = new BlockEditorDialog(profile, initialTab: 3);
            Assert.Equal(3, dlgLock.SelectedTabIndex);
            Assert.Equal(Visibility.Visible, dlgLock.LockVisibility);
            Assert.Equal(Visibility.Collapsed, dlgLock.WebsitesVisibility);

            // Dynamic tab switching via SelectedTabIndex
            dlgApps.SelectedTabIndex = 1;
            Assert.Equal(1, dlgApps.SelectedTabIndex);
            Assert.Equal(Visibility.Collapsed, dlgApps.AppsVisibility);
            Assert.Equal(Visibility.Visible, dlgApps.WebsitesVisibility);
        });
    }

    [Fact]
    public void OpenLockTabCommand_RoutesToTab3_LockProtection()
    {
        ProfileViewModel? targetProfile = null;
        int targetTab = -1;

        var mock = new MockPipeClient();
        var vm = new MainViewModel(mock);
        vm.RequestOpenEditorDialogWithTab = (p, tab) =>
        {
            targetProfile = p;
            targetTab = tab;
        };

        var profile = new ProfileViewModel { Id = 42, Name = "Focus Block" };
        vm.OpenLockTabCommand.Execute(profile);

        Assert.Same(profile, targetProfile);
        Assert.Equal(3, targetTab);
    }

    [Fact]
    public async Task DynamicProfileSync_DetectsExtensionCreatedBlock_ReloadsAutomatically()
    {
        var mock = new MockPipeClient();
        var initialProfiles = new List<Profile>
        {
            new() { Id = 1, Name = "Initial Block", Enabled = true }
        };

        var expandedProfiles = new List<Profile>
        {
            new() { Id = 1, Name = "Initial Block", Enabled = true },
            new() { Id = 2, Name = "Extension Created Block", Enabled = false, BlockedDomains = new List<string> { "youtube.com" } }
        };

        bool returnExpanded = false;

        mock.Handler = req =>
        {
            if (req.Command == CommandNames.ListProfiles)
            {
                var list = returnExpanded ? expandedProfiles : initialProfiles;
                return Response.Success(System.Text.Json.JsonSerializer.Serialize(list));
            }

            if (req.Command == CommandNames.GetStatus)
            {
                var statuses = returnExpanded
                    ? expandedProfiles.Select(p => new ProfileStatusItem { Id = p.Id, Name = p.Name, Enabled = p.Enabled, IsActive = p.Enabled }).ToList()
                    : initialProfiles.Select(p => new ProfileStatusItem { Id = p.Id, Name = p.Name, Enabled = p.Enabled, IsActive = p.Enabled }).ToList();

                var status = new StatusData
                {
                    Active = false,
                    ProfileStatuses = statuses
                };
                return Response.Success(System.Text.Json.JsonSerializer.Serialize(status));
            }

            return Response.Success();
        };

        var vm = new MainViewModel(mock);

        // First poll: loads initial profiles
        await vm.PollStatusAsync();
        Assert.Single(vm.Profiles);
        Assert.Equal("Initial Block", vm.Profiles[0].Name);

        // Extension creates a new block in the service database
        returnExpanded = true;

        // Next 1-second poll: dynamic sync detects count change from status.ProfileStatuses and reloads automatically
        await vm.PollStatusAsync();
        Assert.Equal(2, vm.Profiles.Count);
        Assert.Equal("Extension Created Block", vm.Profiles[1].Name);
    }

    private static void EnsureWpfApplication()
    {
        if (Application.Current == null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Sankalpa;component/Theme.Black.xaml", UriKind.Absolute)
                });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Sankalpa;component/Typography.xaml", UriKind.Absolute)
                });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Sankalpa;component/Icons.xaml", UriKind.Absolute)
                });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Sankalpa;component/Controls.xaml", UriKind.Absolute)
                });
            }
            catch
            {
            }
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureWpfApplication();
                action();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught != null)
        {
            throw new AggregateException(caught);
        }
    }
}
