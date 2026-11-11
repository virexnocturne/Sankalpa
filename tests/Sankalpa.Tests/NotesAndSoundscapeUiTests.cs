using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blocker.Core;
using Blocker.Tests;
using Xunit;

namespace Sankalpa.Tests;

public class NotesAndSoundscapeUiTests
{
    private static string GetRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Blocker.sln")))
        {
            dir = Path.GetDirectoryName(dir)!;
        }
        return dir;
    }

    [Fact]
    public void UiLint_PassesOnNewNotesAndSoundscapeControls()
    {
        string root = GetRepoRoot();
        var filesToLint = new[]
        {
            Path.Combine(root, "src", "Blocker.App", "SoundscapePillControl.xaml"),
            Path.Combine(root, "src", "Blocker.App", "NotesDrawerControl.xaml"),
            Path.Combine(root, "src", "Blocker.App", "ZenShieldWindow.xaml")
        };

        foreach (var file in filesToLint)
        {
            Assert.True(File.Exists(file), $"File must exist: {file}");
        }

        UiLint.AssertPasses(filesToLint);
    }

    [Fact]
    public void NoteCard_MarkdownParsing_ExtractsHeadersTasksAndParagraphs()
    {
        string md = @"# Intentions
- [ ] Breathe deeply and focus
- [x] Complete task list
Contemplating silent awareness.";

        var card = new NoteCard("Morning Reflection", md);

        Assert.Equal("Morning Reflection", card.Title);
        Assert.Equal(4, card.Blocks.Count);

        // Header
        Assert.Equal(MarkdownBlockType.Header, card.Blocks[0].BlockType);
        Assert.Equal("Intentions", card.Blocks[0].Text);

        // Task 1 (unchecked)
        Assert.Equal(MarkdownBlockType.TaskItem, card.Blocks[1].BlockType);
        Assert.Equal("Breathe deeply and focus", card.Blocks[1].Text);
        Assert.False(card.Blocks[1].IsChecked);
        Assert.Equal(0, card.Blocks[1].TaskIndex);

        // Task 2 (checked)
        Assert.Equal(MarkdownBlockType.TaskItem, card.Blocks[2].BlockType);
        Assert.Equal("Complete task list", card.Blocks[2].Text);
        Assert.True(card.Blocks[2].IsChecked);
        Assert.Equal(1, card.Blocks[2].TaskIndex);

        // Paragraph
        Assert.Equal(MarkdownBlockType.Paragraph, card.Blocks[3].BlockType);
        Assert.Equal("Contemplating silent awareness.", card.Blocks[3].Text);
    }

    [Fact]
    public void NoteCard_ToggleTaskCheckbox_UpdatesContentAndParsedBlocks()
    {
        string md = "- [ ] First Item\n- [ ] Second Item";
        var card = new NoteCard("Tasks", md);

        // Check task 0
        bool toggled = card.ToggleTaskCheckbox(0, true);
        Assert.True(toggled);
        Assert.Contains("- [x] First Item", card.MarkdownContent);
        Assert.Contains("- [ ] Second Item", card.MarkdownContent);

        // Uncheck task 0
        toggled = card.ToggleTaskCheckbox(0, false);
        Assert.True(toggled);
        Assert.Contains("- [ ] First Item", card.MarkdownContent);

        // Check task 1
        toggled = card.ToggleTaskCheckbox(1, true);
        Assert.True(toggled);
        Assert.Contains("- [x] Second Item", card.MarkdownContent);
    }

    [Fact]
    public void NoteCardCollection_SerializeAndDeserialize_MaintainsCardList()
    {
        var cards = new List<NoteCard>
        {
            new NoteCard("Title 1", "Markdown body 1"),
            new NoteCard("Title 2", "# Header\n- [x] Done")
        };

        string json = NoteCardCollection.Serialize(cards);
        Assert.StartsWith("[", json);
        Assert.EndsWith("]", json);

        var deserialized = NoteCardCollection.Deserialize(json);
        Assert.Equal(2, deserialized.Count);
        Assert.Equal("Title 1", deserialized[0].Title);
        Assert.Equal("Markdown body 1", deserialized[0].MarkdownContent);
        Assert.Equal("Title 2", deserialized[1].Title);
        Assert.Equal("# Header\n- [x] Done", deserialized[1].MarkdownContent);
        Assert.Equal(2, deserialized[1].Blocks.Count);
    }

    [Fact]
    public void NoteCard_LongContent_CalculatesSeeMoreAccurately()
    {
        string shortMd = "Short line 1\nShort line 2";
        var shortCard = new NoteCard("Short", shortMd);
        Assert.False(shortCard.ShowSeeMore);

        string longMd = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5\nLine 6\nLine 7";
        var longCard = new NoteCard("Long", longMd);
        Assert.True(longCard.ShowSeeMore);
        Assert.Equal(4, longCard.VisibleBlocks.Count());
        Assert.Equal("see more...", longCard.SeeMoreText);

        longCard.IsFullExpanded = true;
        Assert.Equal(7, longCard.VisibleBlocks.Count());
        Assert.Equal("see less", longCard.SeeMoreText);
    }

    [Fact]
    public void NoteCard_RelativeTime_CalculatesAppropriateLabels()
    {
        var now = DateTime.UtcNow;
        Assert.Equal("Just now", NoteCard.FormatRelativeTime(now));
        Assert.Equal("5m ago", NoteCard.FormatRelativeTime(now.AddMinutes(-5)));
        Assert.Equal("2h ago", NoteCard.FormatRelativeTime(now.AddHours(-2)));
        Assert.Equal("3d ago", NoteCard.FormatRelativeTime(now.AddDays(-3)));
    }

    [Fact]
    public void DeletingLastNote_SetsCurrentNoteToEmptyString()
    {
        // 1. Logical verification with NoteCardCollection
        var cards = new List<NoteCard> { new NoteCard("Session Reflection", "Breathe deeply") };
        Assert.Single(cards);

        // Delete the last note
        cards.RemoveAt(0);
        string serialized = cards.Count > 0 ? NoteCardCollection.Serialize(cards) : string.Empty;

        Assert.Equal(string.Empty, serialized);
        Assert.Empty(NoteCardCollection.Deserialize(serialized));

        // 2. Source contract verification in MeditationViewModel.Notes.cs
        string root = GetRepoRoot();
        string vmPath = Path.Combine(root, "src", "Blocker.App", "MeditationViewModel.Notes.cs");
        Assert.True(File.Exists(vmPath));

        string vmCode = File.ReadAllText(vmPath);
        Assert.Contains("NotesCollection.Remove(card);", vmCode);
        Assert.Contains("SaveActiveNotes();", vmCode);
        Assert.Contains("_currentNote = string.Empty;", vmCode);
    }

    [Fact]
    public void SavingNote_TriggersTransientAutoSaveStatus()
    {
        string root = GetRepoRoot();
        string vmPath = Path.Combine(root, "src", "Blocker.App", "MeditationViewModel.Notes.cs");
        Assert.True(File.Exists(vmPath));

        string vmCode = File.ReadAllText(vmPath);

        // AutoSaveStatus starts empty by default
        Assert.Contains("private string _autoSaveStatus = string.Empty;", vmCode);

        // SaveActiveNotes sets "Saved" and starts 2.5s timer
        Assert.Contains("AutoSaveStatus = \"Saved\";", vmCode);
        Assert.Contains("_transientSaveTimer.Start();", vmCode);
        Assert.Contains("TimeSpan.FromSeconds(2.5)", vmCode);
        Assert.Contains("AutoSaveStatus = string.Empty;", vmCode);
    }

    [Fact]
    public void MainWindow_ClockSizing_And_SideShift_ConfiguredCorrectly()
    {
        string root = GetRepoRoot();
        string mainWindowXaml = Path.Combine(root, "src", "Blocker.App", "MainWindow.xaml");
        Assert.True(File.Exists(mainWindowXaml));

        string xaml = File.ReadAllText(mainWindowXaml);

        // Enlarge countdown timer clock to FontSize72 and Padding 48,28
        Assert.Contains("FontSize=\"{DynamicResource FontSize72}\"", xaml);
        Assert.Contains("Padding=\"48,28\"", xaml);

        // Smooth side-shift animation when Notes drawer is open (no violent snapping)
        Assert.Contains("Binding=\"{Binding Meditation.IsNotesDrawerOpen}\"", xaml);
        Assert.Contains("TranslateTransform", xaml);
        Assert.Contains("To=\"-170\"", xaml);
        Assert.DoesNotContain("Value=\"24,24,340,24\"", xaml);
    }

    [Fact]
    public void MainWindow_Shortcuts_NoDuplicateKeyBindingForToggleNotesDrawer()
    {
        string root = GetRepoRoot();
        string mainWindowXaml = Path.Combine(root, "src", "Blocker.App", "MainWindow.xaml");
        string mainWindowCs = Path.Combine(root, "src", "Blocker.App", "MainWindow.xaml.cs");
        Assert.True(File.Exists(mainWindowXaml));
        Assert.True(File.Exists(mainWindowCs));

        string xaml = File.ReadAllText(mainWindowXaml);
        string cs = File.ReadAllText(mainWindowCs);

        // MainWindow.xaml should NOT have a duplicate KeyBinding for ToggleNotesDrawerCommand
        Assert.DoesNotContain("Command=\"{Binding Meditation.ToggleNotesDrawerCommand}\"", xaml);

        // MainWindow.xaml.cs should handle Ctrl+Shift+N and Ctrl+N cleanly
        Assert.Contains("ToggleNotesDrawerCommand", cs);
        Assert.Contains("FocusComposer", cs);
        Assert.Contains("CancelNoteComposerCommand", cs);
    }

    [Fact]
    public void MainWindow_NotesDrawer_IsDrawerOpenBinding_BindsDirectlyToIsNotesDrawerOpen()
    {
        string root = GetRepoRoot();
        string mainWindowXaml = Path.Combine(root, "src", "Blocker.App", "MainWindow.xaml");
        Assert.True(File.Exists(mainWindowXaml));

        string xaml = File.ReadAllText(mainWindowXaml);

        // NotesDrawerControl has DataContext="{Binding Meditation}".
        // Therefore, IsDrawerOpen must bind directly to IsNotesDrawerOpen (not Meditation.IsNotesDrawerOpen, which fails because DataContext is already MeditationViewModel).
        Assert.DoesNotContain("IsDrawerOpen=\"{Binding Meditation.IsNotesDrawerOpen", xaml);
        Assert.Contains("IsDrawerOpen=\"{Binding IsNotesDrawerOpen", xaml);
    }

    [Fact]
    public void NotesDrawerControl_HasFocusComposerMethod_ForFastKeyboardNoteEntry()
    {
        string root = GetRepoRoot();
        string drawerCs = Path.Combine(root, "src", "Blocker.App", "NotesDrawerControl.xaml.cs");
        Assert.True(File.Exists(drawerCs));

        string cs = File.ReadAllText(drawerCs);
        Assert.Contains("public void FocusComposer()", cs);
        Assert.Contains("MarkdownBox.Focus()", cs);
        Assert.Contains("Keyboard.Focus(MarkdownBox)", cs);
    }

    [Fact]
    public void MainWindow_GlobalAltNHotkey_RegisteredViaWin32()
    {
        string root = GetRepoRoot();
        string mainWindowCs = Path.Combine(root, "src", "Blocker.App", "MainWindow.xaml.cs");
        Assert.True(File.Exists(mainWindowCs));

        string cs = File.ReadAllText(mainWindowCs);

        Assert.Contains("RegisterHotKey", cs);
        Assert.Contains("UnregisterHotKey", cs);
        Assert.Contains("0x9001", cs);
        Assert.Contains("0x0312", cs);
        Assert.Contains("ToggleQuickNotes", cs);
    }

    [Fact]
    public void SummaryBadgeButtonStyle_DefinedInControlsXaml_AndSafelyReferencedInMeditationEditDialog()
    {
        string root = GetRepoRoot();
        string controlsXaml = Path.Combine(root, "src", "Blocker.App", "Controls.xaml");
        string dialogCs = Path.Combine(root, "src", "Blocker.App", "MeditationEditDialog.xaml.cs");

        Assert.True(File.Exists(controlsXaml));
        Assert.True(File.Exists(dialogCs));

        string xaml = File.ReadAllText(controlsXaml);
        string cs = File.ReadAllText(dialogCs);

        // Defined at application-wide Controls.xaml level
        Assert.Contains("x:Key=\"SummaryBadgeButtonStyle\"", xaml);

        // Referenced safely via TryFindResource fallback to prevent unhandled crash when adding apps
        Assert.Contains("TryFindResource(\"SummaryBadgeButtonStyle\")", cs);
    }

    [Fact]
    public void ZenQuickNotesOverlay_UsesQuickNotesTitle_AndContextMenuPresets()
    {
        string root = GetRepoRoot();
        string overlayXaml = Path.Combine(root, "src", "Blocker.App", "ZenQuickNotesOverlay.xaml");
        string overlayCs = Path.Combine(root, "src", "Blocker.App", "ZenQuickNotesOverlay.xaml.cs");

        Assert.True(File.Exists(overlayXaml));
        Assert.True(File.Exists(overlayCs));

        string xaml = File.ReadAllText(overlayXaml);
        string cs = File.ReadAllText(overlayCs);

        // Window title and headers renamed from "Session Reflections" to "Quick Notes"
        Assert.Contains("Title=\"Quick Notes\"", xaml);
        Assert.Contains("Text=\"Quick Notes\"", xaml);
        Assert.Contains("Text=\"New Quick Note\"", xaml);

        // Context menu presets present on text area
        Assert.Contains("<ContextMenu", xaml);
        Assert.Contains("Insert Task (- [ ])", xaml);
        Assert.Contains("Heading 1 (# )", xaml);
        Assert.Contains("Format Bold (**bold**)", xaml);

        // Live preview elements
        Assert.Contains("ComposerPreviewContainer", xaml);
        Assert.Contains("ComposerPreviewPanel", xaml);
        Assert.Contains("ComposerEditTabBtn", xaml);
        Assert.Contains("ComposerPreviewTabBtn", xaml);

        // C# implementation contains live preview switching and context menu actions
        Assert.Contains("SwitchComposerToPreviewMode", cs);
        Assert.Contains("RenderMarkdownToPanel", cs);
        Assert.Contains("InsertTask_Click", cs);
        Assert.Contains("InsertH1_Click", cs);
    }

    [Fact]
    public void NotesDrawerControl_ParityWithZenQuickNotesOverlay_HasPreviewTabsContextMenuAndImageSupport()
    {
        string root = GetRepoRoot();
        string drawerXaml = Path.Combine(root, "src", "Blocker.App", "NotesDrawerControl.xaml");
        string drawerCs = Path.Combine(root, "src", "Blocker.App", "NotesDrawerControl.xaml.cs");
        string managerCs = Path.Combine(root, "src", "Blocker.App", "NotesAttachmentManager.cs");

        Assert.True(File.Exists(drawerXaml));
        Assert.True(File.Exists(drawerCs));
        Assert.True(File.Exists(managerCs));

        string xaml = File.ReadAllText(drawerXaml);
        string cs = File.ReadAllText(drawerCs);
        string manager = File.ReadAllText(managerCs);

        // 1. Live preview controls in drawer
        Assert.Contains("ComposerPreviewContainer", xaml);
        Assert.Contains("ComposerPreviewPanel", xaml);
        Assert.Contains("ComposerEditTabBtn", xaml);
        Assert.Contains("ComposerPreviewTabBtn", xaml);

        // 2. ContextMenu presets on MarkdownBox
        Assert.Contains("<ContextMenu", xaml);
        Assert.Contains("Insert Task (- [ ])", xaml);
        Assert.Contains("Insert Bullet (- )", xaml);
        Assert.Contains("Insert Numbered List (1. )", xaml);
        Assert.Contains("Heading 1 (# )", xaml);
        Assert.Contains("Heading 2 (## )", xaml);
        Assert.Contains("Heading 3 (### )", xaml);
        Assert.Contains("Format Bold (**bold**)", xaml);
        Assert.Contains("Format Italic (*italic*)", xaml);
        Assert.Contains("Format Strikethrough (~~text~~)", xaml);
        Assert.Contains("Format Highlight (==text==)", xaml);
        Assert.Contains("Format Inline Code (`code`)", xaml);
        Assert.Contains("Insert Quote (&gt; )", xaml);

        // 3. Image attachment converter and rendering in cards
        Assert.Contains("AttachmentImageConverter", xaml);
        Assert.Contains("ImagePath", xaml);

        // 4. Code-behind parity
        Assert.Contains("SwitchComposerToPreviewMode", cs);
        Assert.Contains("RenderMarkdownToPanel", cs);
        Assert.Contains("InsertTask_Click", cs);
        Assert.Contains("InsertH1_Click", cs);
        Assert.Contains("InsertBold_Click", cs);
        Assert.Contains("NotesAttachmentManager", cs);
        Assert.Contains("OnMarkdownBoxPasting", cs);

        // 5. NotesAttachmentManager capabilities
        Assert.Contains("TrySaveClipboardImage", manager);
        Assert.Contains("ResolveAttachmentPath", manager);
        Assert.Contains("CleanAttachmentReference", manager);
        Assert.Contains("GetAttachmentsDirectory", manager);
    }

    [Fact]
    public void MeditationViewModel_AutoDetectsTitle_FromHeadingsOrFirstLine_SourceContract()
    {
        string root = GetRepoRoot();
        string vmPath = Path.Combine(root, "src", "Blocker.App", "MeditationViewModel.Notes.cs");
        Assert.True(File.Exists(vmPath));

        string vmCode = File.ReadAllText(vmPath);

        // Title auto-detection in SaveNoteCardCommand
        Assert.Contains("SaveNoteCardCommand = new RelayCommand", vmCode);
        Assert.Contains("tr.StartsWith(\"# \")", vmCode);
        Assert.Contains("tr.StartsWith(\"## \")", vmCode);
        Assert.Contains("tr.StartsWith(\"### \")", vmCode);
        Assert.Contains("clean.Length > 40", vmCode);
        Assert.Contains("title = \"Untitled Note\";", vmCode);
    }

    [Fact]
    public void NotePersistence_SavesAndRestoresNotes_FromVaultFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "SankalpaVaultTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string vaultFile = Path.Combine(tempDir, "active_notes.json");

        try
        {
            var initialCards = new List<NoteCard>
            {
                new NoteCard("Deep Work Session", "# Focus Goals\n- [x] Review architecture\n- [ ] Fix bugs\n![[Pasted_Image_123.png]]"),
                new NoteCard("Evening Reflections", "Calm and peaceful session.")
            };

            // 1. Serialize to vault file
            string json = NoteCardCollection.Serialize(initialCards);
            File.WriteAllText(vaultFile, json);

            // 2. Read back from vault file (simulating app restart)
            Assert.True(File.Exists(vaultFile));
            string loadedJson = File.ReadAllText(vaultFile);
            var restoredCards = NoteCardCollection.Deserialize(loadedJson);

            Assert.Equal(2, restoredCards.Count);
            Assert.Equal("Deep Work Session", restoredCards[0].Title);
            Assert.Contains("- [x] Review architecture", restoredCards[0].MarkdownContent);
            Assert.Contains("- [ ] Fix bugs", restoredCards[0].MarkdownContent);
            Assert.Equal("Evening Reflections", restoredCards[1].Title);
            Assert.Equal("Calm and peaceful session.", restoredCards[1].MarkdownContent);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void PasswordSecurity_HashAndVerify_InverseTesting()
    {
        string password = "MindfulFocus2026!";
        PasswordSecurity.HashPassword(password, out string salt, out string hash);

        // Positive check: correct password verifies
        Assert.True(PasswordSecurity.VerifyPassword(password, salt, hash));

        // Inverse test 1: wrong password fails
        Assert.False(PasswordSecurity.VerifyPassword("WrongPassword123", salt, hash));

        // Inverse test 2: empty password fails
        Assert.False(PasswordSecurity.VerifyPassword("", salt, hash));

        // Inverse test 3: corrupted salt fails
        PasswordSecurity.HashPassword("OtherPassword", out string wrongSalt, out _);
        Assert.False(PasswordSecurity.VerifyPassword(password, wrongSalt, hash));

        // Inverse test 4: corrupted hash fails
        Assert.False(PasswordSecurity.VerifyPassword(password, salt, hash + "bad"));
    }

    [Fact]
    public void MeditationSettings_PersistInUiSettings_AcrossRestarts()
    {
        string tempSettingsFile = Path.Combine(Path.GetTempPath(), "ui-settings-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var settings = new UiSettings
            {
                MeditationDurationMinutes = 45,
                MeditationLockMode = "Password",
                MeditationSoundscape = "Forest",
                MeditationPasswordHash = "test_hash_12345"
            };

            UiSettingsStore.SaveToFile(settings, tempSettingsFile);

            var loaded = UiSettingsStore.LoadFromFile(tempSettingsFile);

            Assert.Equal(45, loaded.MeditationDurationMinutes);
            Assert.Equal("Password", loaded.MeditationLockMode);
            Assert.Equal("Forest", loaded.MeditationSoundscape);
            Assert.Equal("test_hash_12345", loaded.MeditationPasswordHash);
        }
        finally
        {
            if (File.Exists(tempSettingsFile))
            {
                File.Delete(tempSettingsFile);
            }
        }
    }

    [Fact]
    public void BlockedApps_ExclusionFromWishlist_SourceContract()
    {
        string root = GetRepoRoot();
        string dlgPath = Path.Combine(root, "src", "Blocker.App", "MeditationEditDialog.xaml.cs");
        string pickerPath = Path.Combine(root, "src", "Blocker.App", "AppPickerDialog.xaml.cs");

        Assert.True(File.Exists(dlgPath));
        Assert.True(File.Exists(pickerPath));

        string dlgCode = File.ReadAllText(dlgPath);
        string pickerCode = File.ReadAllText(pickerPath);

        // MeditationEditDialog must filter out blocked apps
        Assert.Contains("IsAppBlocked", dlgCode);
        Assert.Contains("_blockedApps", dlgCode);

        // AppPickerDialog must have BlockedByProfilePickerRow and block selection
        Assert.Contains("BlockedByProfilePickerRow", pickerCode);
        Assert.Contains("(Blocked by Block Profile)", pickerCode);
    }

    [Fact]
    public void BlockEditorDialog_PickApps_AppendsWithoutClearing_SourceContract()
    {
        string root = GetRepoRoot();
        string editorPath = Path.Combine(root, "src", "Blocker.App", "BlockEditorDialog.xaml.cs");
        Assert.True(File.Exists(editorPath));

        string editorCode = File.ReadAllText(editorPath);

        // In PickApps_Click, Apps.Clear() must NOT be called before adding newly picked apps
        Assert.DoesNotContain("Apps.Clear();", editorCode.Substring(editorCode.IndexOf("private void PickApps_Click")));
    }

    [Fact]
    public void TypingChallenge_And_TaskView_HookContracts()
    {
        string root = GetRepoRoot();
        string shieldPath = Path.Combine(root, "src", "Blocker.App", "ZenShieldWindow.xaml.cs");
        string hookPath = Path.Combine(root, "src", "Blocker.Core", "LowLevelKeyboardHook.cs");

        Assert.True(File.Exists(shieldPath));
        Assert.True(File.Exists(hookPath));

        string shieldCode = File.ReadAllText(shieldPath);
        string hookCode = File.ReadAllText(hookPath);

        // Typing Challenge string check accepts both "TypingChallenge" and "Challenge"
        Assert.Contains("TypingChallenge", shieldCode);
        Assert.Contains("MultitaskingViewFrame", shieldCode);

        // LowLevelKeyboardHook blocks VK_TAB on winDown (Win+Tab / Task View)
        Assert.Contains("vk == VK_TAB && (winDown || altDown)", hookCode);
    }

    [Fact]
    public void MeditationViewModel_PasswordUnlock_UsesInlineError_AndNeverSpawnsDarkDialogWindow()
    {
        string root = GetRepoRoot();
        string vmPath = Path.Combine(root, "src", "Blocker.App", "MeditationViewModel.cs");
        string helperPath = Path.Combine(root, "src", "Blocker.App", "MeditationUnlockDialogHelper.cs");
        string dialogPath = Path.Combine(root, "src", "Blocker.App", "PasswordUnlockDialog.xaml.cs");
        Assert.True(File.Exists(vmPath));
        Assert.True(File.Exists(helperPath));
        Assert.True(File.Exists(dialogPath));

        string vmCode = File.ReadAllText(vmPath);
        string helperCode = File.ReadAllText(helperPath);
        string dialogCode = File.ReadAllText(dialogPath);

        // Verification must use the shared helper + inline SetError rather than spawning DarkDialogWindow
        Assert.DoesNotContain("DarkDialogWindow.Show(\"Incorrect password. Access denied.\"", vmCode);
        Assert.DoesNotContain("DarkDialogWindow.Show(\"Incorrect password. Access denied.\"", helperCode);
        Assert.Contains("MeditationUnlockDialogHelper.RequestUnlock", vmCode);
        Assert.Contains("dialogResult == true && dlg.IsVerified", helperCode);
        Assert.Contains("Closing += PasswordUnlockDialog_Closing;", dialogCode);
        Assert.Contains("SetError(\"Incorrect password. Access denied.\");", dialogCode);
        Assert.Contains("e.Cancel = true;", dialogCode);
    }

    [Fact]
    public void DarkDialogWindow_InheritsTopmost_ToPreventOrphaningBehindZenShield()
    {
        string root = GetRepoRoot();
        string dlgPath = Path.Combine(root, "src", "Blocker.App", "DarkDialogWindow.xaml.cs");
        Assert.True(File.Exists(dlgPath));

        string dlgCode = File.ReadAllText(dlgPath);

        // DarkDialogWindow must propagate Topmost = true if owner or any visible app window is Topmost
        Assert.Contains("win.Topmost = true;", dlgCode);
        Assert.Contains("targetOwner.Topmost", dlgCode);
    }
}
