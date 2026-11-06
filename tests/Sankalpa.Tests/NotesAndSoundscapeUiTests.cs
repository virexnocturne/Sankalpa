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
}
