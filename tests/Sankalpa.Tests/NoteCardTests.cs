using System;
using System.Collections.Generic;
using System.IO;
using Blocker.Core;
using Xunit;

namespace Sankalpa.Tests;

public class NoteCardTests
{
    [Fact]
    public void NoteCardCollection_SerializeAndDeserialize_RoundTripsSuccessfully()
    {
        // Arrange
        var originalCards = new List<NoteCard>
        {
            new NoteCard
            {
                Id = "card-1",
                Title = "Vipassana Insights",
                MarkdownContent = "Observed breath sensations and subtle body vibrations.",
                CreatedAtUtc = new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 11, 6, 10, 15, 0, DateTimeKind.Utc)
            },
            new NoteCard
            {
                Id = "card-2",
                Title = "Action Items",
                MarkdownContent = "- [ ] Drink green tea\n- [ ] 10m walking meditation",
                CreatedAtUtc = new DateTime(2026, 11, 6, 10, 20, 0, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 11, 6, 10, 25, 0, DateTimeKind.Utc)
            }
        };

        // Act
        string serializedJson = NoteCardCollection.Serialize(originalCards);
        List<NoteCard> deserializedCards = NoteCardCollection.Deserialize(serializedJson);

        // Assert
        Assert.NotNull(deserializedCards);
        Assert.Equal(2, deserializedCards.Count);

        Assert.Equal(originalCards[0].Id, deserializedCards[0].Id);
        Assert.Equal(originalCards[0].Title, deserializedCards[0].Title);
        Assert.Equal(originalCards[0].MarkdownContent, deserializedCards[0].MarkdownContent);

        Assert.Equal(originalCards[1].Id, deserializedCards[1].Id);
        Assert.Equal(originalCards[1].Title, deserializedCards[1].Title);
        Assert.Equal(originalCards[1].MarkdownContent, deserializedCards[1].MarkdownContent);
    }

    [Fact]
    public void NoteCardCollection_Deserialize_LegacyPlainTextFallback()
    {
        // Arrange
        string legacyNote = "Just sat and breathed quietly for 20 minutes.";

        // Act
        List<NoteCard> cards = NoteCardCollection.Deserialize(legacyNote);

        // Assert
        Assert.NotNull(cards);
        Assert.Single(cards);
        Assert.Equal("Session Reflection", cards[0].Title);
        Assert.Equal(legacyNote, cards[0].MarkdownContent);
        Assert.False(string.IsNullOrWhiteSpace(cards[0].Id));
    }

    [Fact]
    public void NoteCardCollection_Deserialize_EmptyOrNullReturnsEmptyList()
    {
        // Act & Assert
        Assert.Empty(NoteCardCollection.Deserialize(null));
        Assert.Empty(NoteCardCollection.Deserialize(""));
        Assert.Empty(NoteCardCollection.Deserialize("   "));
        Assert.Empty(NoteCardCollection.Deserialize("[]"));
    }

    [Fact]
    public void NoteCard_ToggleTaskCheckbox_TogglesCorrectIndex()
    {
        // Arrange
        string initialContent = "# My Tasks\n- [ ] Meditate for 15 mins\n- [ ] Drink a glass of water\n- [ ] Journal thoughts";
        var card = new NoteCard("Tasks Note", initialContent);

        // Act 1: Toggle index 1 to checked (true)
        bool toggleResult = card.ToggleTaskCheckbox(1, true);

        // Assert 1: Index 1 is checked, index 0 and 2 remain unchecked
        Assert.True(toggleResult);
        Assert.Contains("- [ ] Meditate for 15 mins", card.MarkdownContent);
        Assert.Contains("- [x] Drink a glass of water", card.MarkdownContent);
        Assert.Contains("- [ ] Journal thoughts", card.MarkdownContent);

        // Act 2: Toggle index 1 back to unchecked (false)
        bool untoggleResult = card.ToggleTaskCheckbox(1, false);

        // Assert 2: Index 1 returns to unchecked
        Assert.True(untoggleResult);
        Assert.Contains("- [ ] Meditate for 15 mins", card.MarkdownContent);
        Assert.Contains("- [ ] Drink a glass of water", card.MarkdownContent);
        Assert.Contains("- [ ] Journal thoughts", card.MarkdownContent);
    }

    [Fact]
    public void NoteCard_ToggleTaskCheckbox_InvalidIndexReturnsFalse()
    {
        var card = new NoteCard("Tasks", "- [ ] Only one task");
        Assert.False(card.ToggleTaskCheckbox(-1, true));
        Assert.False(card.ToggleTaskCheckbox(5, true));
    }

    [Fact]
    public void NoteCard_MarkdownParsing_HeadersAndSubheaders_ParsedCorrectly()
    {
        string md = "# Main Header\n## Subheader 2\n### Subheader 3\nParagraph text here";
        var card = new NoteCard("Headers Note", md);

        Assert.Equal(4, card.Blocks.Count);

        Assert.Equal(MarkdownBlockType.Header, card.Blocks[0].BlockType);
        Assert.Equal("Main Header", card.Blocks[0].Text);

        Assert.Equal(MarkdownBlockType.Subheader, card.Blocks[1].BlockType);
        Assert.Equal("Subheader 2", card.Blocks[1].Text);

        Assert.Equal(MarkdownBlockType.Subheader, card.Blocks[2].BlockType);
        Assert.Equal("Subheader 3", card.Blocks[2].Text);

        Assert.Equal(MarkdownBlockType.Paragraph, card.Blocks[3].BlockType);
        Assert.Equal("Paragraph text here", card.Blocks[3].Text);
    }

    [Fact]
    public void NoteCard_MarkdownParsing_BulletItems_ParsedCorrectly()
    {
        string md = "- Dash item\n* Asterisk item\n• Unicode bullet item";
        var card = new NoteCard("Bullets Note", md);

        Assert.Equal(3, card.Blocks.Count);

        Assert.Equal(MarkdownBlockType.BulletItem, card.Blocks[0].BlockType);
        Assert.Equal("Dash item", card.Blocks[0].Text);

        Assert.Equal(MarkdownBlockType.BulletItem, card.Blocks[1].BlockType);
        Assert.Equal("Asterisk item", card.Blocks[1].Text);

        Assert.Equal(MarkdownBlockType.BulletItem, card.Blocks[2].BlockType);
        Assert.Equal("Unicode bullet item", card.Blocks[2].Text);
    }

    [Fact]
    public void NoteCard_MarkdownParsing_Tasks_StandardAndUnicode_ParsedCorrectly()
    {
        string md = "- [ ] Dash unchecked\n* [ ] Star unchecked\n- [x] Dash checked\n* [x] Star checked\n[ ] Bracket unchecked\n[x] Bracket checked\n☐ Ballot unchecked\n☑ Ballot checked";
        var card = new NoteCard("Tasks Note", md);

        Assert.Equal(8, card.Blocks.Count);

        // 0: - [ ]
        Assert.Equal(MarkdownBlockType.TaskItem, card.Blocks[0].BlockType);
        Assert.Equal("Dash unchecked", card.Blocks[0].Text);
        Assert.False(card.Blocks[0].IsChecked);
        Assert.Equal(0, card.Blocks[0].TaskIndex);

        // 1: * [ ]
        Assert.Equal(MarkdownBlockType.TaskItem, card.Blocks[1].BlockType);
        Assert.Equal("Star unchecked", card.Blocks[1].Text);
        Assert.False(card.Blocks[1].IsChecked);
        Assert.Equal(1, card.Blocks[1].TaskIndex);

        // 2: - [x]
        Assert.Equal(MarkdownBlockType.TaskItem, card.Blocks[2].BlockType);
        Assert.Equal("Dash checked", card.Blocks[2].Text);
        Assert.True(card.Blocks[2].IsChecked);
        Assert.Equal(2, card.Blocks[2].TaskIndex);

        // 3: * [x]
        Assert.Equal(MarkdownBlockType.TaskItem, card.Blocks[3].BlockType);
        Assert.Equal("Star checked", card.Blocks[3].Text);
        Assert.True(card.Blocks[3].IsChecked);
        Assert.Equal(3, card.Blocks[3].TaskIndex);

        // 4: [ ]
        Assert.Equal(MarkdownBlockType.TaskItem, card.Blocks[4].BlockType);
        Assert.Equal("Bracket unchecked", card.Blocks[4].Text);
        Assert.False(card.Blocks[4].IsChecked);
        Assert.Equal(4, card.Blocks[4].TaskIndex);

        // 5: [x]
        Assert.Equal(MarkdownBlockType.TaskItem, card.Blocks[5].BlockType);
        Assert.Equal("Bracket checked", card.Blocks[5].Text);
        Assert.True(card.Blocks[5].IsChecked);
        Assert.Equal(5, card.Blocks[5].TaskIndex);

        // 6: ☐
        Assert.Equal(MarkdownBlockType.TaskItem, card.Blocks[6].BlockType);
        Assert.Equal("Ballot unchecked", card.Blocks[6].Text);
        Assert.False(card.Blocks[6].IsChecked);
        Assert.Equal(6, card.Blocks[6].TaskIndex);

        // 7: ☑
        Assert.Equal(MarkdownBlockType.TaskItem, card.Blocks[7].BlockType);
        Assert.Equal("Ballot checked", card.Blocks[7].Text);
        Assert.True(card.Blocks[7].IsChecked);
        Assert.Equal(7, card.Blocks[7].TaskIndex);
    }

    [Fact]
    public void NoteCard_ToggleTaskCheckbox_UnicodeBallotBoxAndStandardMarkdown_TogglesCleanly()
    {
        string md = "- [ ] Dash task\n* [ ] Star task\n☐ Ballot task\n☑ Checked ballot task";
        var card = new NoteCard("Mixed Tasks Note", md);

        // Toggle index 0 (- [ ]) -> checked
        Assert.True(card.ToggleTaskCheckbox(0, true));
        Assert.Contains("- [x] Dash task", card.MarkdownContent);

        // Toggle index 1 (* [ ]) -> checked
        Assert.True(card.ToggleTaskCheckbox(1, true));
        Assert.Contains("* [x] Star task", card.MarkdownContent);

        // Toggle index 2 (☐) -> checked
        Assert.True(card.ToggleTaskCheckbox(2, true));
        Assert.Contains("☑ Ballot task", card.MarkdownContent);

        // Toggle index 3 (☑) -> unchecked
        Assert.True(card.ToggleTaskCheckbox(3, false));
        Assert.Contains("☐ Checked ballot task", card.MarkdownContent);

        // Untoggle index 0 back to false
        Assert.True(card.ToggleTaskCheckbox(0, false));
        Assert.Contains("- [ ] Dash task", card.MarkdownContent);

        // Untoggle index 2 back to false
        Assert.True(card.ToggleTaskCheckbox(2, false));
        Assert.Contains("☐ Ballot task", card.MarkdownContent);
    }

    [Fact]
    public void NoteCardCollection_GenerateSessionBundleMarkdown_FormatsCorrectly()
    {
        // Arrange
        var cards = new List<NoteCard>
        {
            new NoteCard("Focused Breathing", "Settled into an upright posture and focused on breath."),
            new NoteCard("Mind Wandering", "Noticed distractions regarding tomorrow's deadline, gently returned.")
        };
        string sessionDate = "Nov 6, 2026";
        string duration = "20m";

        // Act
        string bundleMarkdown = NoteCardCollection.GenerateSessionBundleMarkdown(sessionDate, duration, cards);

        // Assert
        // Frontmatter checks
        Assert.Contains("---", bundleMarkdown);
        Assert.Contains($"title: Sankalpa Meditation Notes - {sessionDate}", bundleMarkdown);
        Assert.Contains($"date: {sessionDate}", bundleMarkdown);
        Assert.Contains($"duration: {duration}", bundleMarkdown);
        Assert.Contains($"cards_count: {cards.Count}", bundleMarkdown);

        // Header and duration in body
        Assert.Contains($"# 🧘 Meditation Notes — {sessionDate}", bundleMarkdown);
        Assert.Contains($"*Duration: {duration}*", bundleMarkdown);

        // Card titles and markdown contents
        Assert.Contains($"## {cards[0].Title}", bundleMarkdown);
        Assert.Contains(cards[0].MarkdownContent, bundleMarkdown);
        Assert.Contains($"## {cards[1].Title}", bundleMarkdown);
        Assert.Contains(cards[1].MarkdownContent, bundleMarkdown);
    }

    [Fact]
    public void NoteCardCollection_GenerateVaultFiles_WritesIndividualMarkdownFiles()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"sankalpa_vault_test_{Guid.NewGuid():N}");
        try
        {
            var cards = new List<NoteCard>
            {
                new NoteCard("Deep Breath", "Took deep inhalations to calm the nervous system."),
                new NoteCard("Stillness Reflection", "Maintained physical immobility for 10 minutes.")
            };
            string sessionDate = "Nov 6, 2026";

            // Act
            List<string> createdFiles = NoteCardCollection.GenerateVaultFiles(tempDir, sessionDate, cards);

            // Assert
            Assert.Equal(2, createdFiles.Count);
            foreach (string filePath in createdFiles)
            {
                Assert.True(File.Exists(filePath), $"Expected file {filePath} to exist.");
            }

            // Inspect file names
            string fileName1 = Path.GetFileName(createdFiles[0]);
            string fileName2 = Path.GetFileName(createdFiles[1]);

            Assert.Equal("Nov_6,_2026_Deep_Breath.md", fileName1);
            Assert.Equal("Nov_6,_2026_Stillness_Reflection.md", fileName2);

            // Inspect file contents
            string content1 = File.ReadAllText(createdFiles[0]);
            Assert.Contains("---", content1);
            Assert.Contains("title: \"Deep Breath\"", content1);
            Assert.Contains($"session_date: {sessionDate}", content1);
            Assert.Contains($"card_id: {cards[0].Id}", content1);
            Assert.Contains("tags: [sankalpa, meditation, focus]", content1);
            Assert.Contains("# Deep Breath", content1);
            Assert.Contains("Took deep inhalations to calm the nervous system.", content1);

            string content2 = File.ReadAllText(createdFiles[1]);
            Assert.Contains("---", content2);
            Assert.Contains("title: \"Stillness Reflection\"", content2);
            Assert.Contains($"session_date: {sessionDate}", content2);
            Assert.Contains($"card_id: {cards[1].Id}", content2);
            Assert.Contains("# Stillness Reflection", content2);
            Assert.Contains("Maintained physical immobility for 10 minutes.", content2);
        }
        finally
        {
            // Clean up temporary directory
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void NoteCardCollection_GenerateMasterJournalMarkdown_CombinesMultipleSessions()
    {
        // Arrange
        var session1Cards = new List<NoteCard>
        {
            new NoteCard("Morning Calm", "Gentle morning meditation before starting work.")
        };

        var session2Cards = new List<NoteCard>
        {
            new NoteCard("Evening Wind Down", "Body relaxation scan before sleep."),
            new NoteCard("Gratitude", "Grateful for productive pairing sessions.")
        };

        var sessions = new List<(string sessionDate, string duration, List<NoteCard> cards)>
        {
            ("Nov 5, 2026", "15m", session1Cards),
            ("Nov 6, 2026", "30m", session2Cards)
        };

        // Act
        string masterJournal = NoteCardCollection.GenerateMasterJournalMarkdown(sessions);

        // Assert
        Assert.Contains("# 📖 Sankalpa Meditation Master Journal", masterJournal);
        Assert.Contains("- **Total Sessions**: 2", masterJournal);
        Assert.Contains("- **Total Notes**: 3", masterJournal);

        // Assert chronological headers and session entries
        Assert.Contains("## Session: Nov 5, 2026 (15m)", masterJournal);
        Assert.Contains("### Morning Calm", masterJournal);
        Assert.Contains("Gentle morning meditation before starting work.", masterJournal);

        Assert.Contains("## Session: Nov 6, 2026 (30m)", masterJournal);
        Assert.Contains("### Evening Wind Down", masterJournal);
        Assert.Contains("Body relaxation scan before sleep.", masterJournal);
        Assert.Contains("### Gratitude", masterJournal);
        Assert.Contains("Grateful for productive pairing sessions.", masterJournal);
    }

    [Fact]
    public void NoteCard_MarkdownParsing_BulletPrefixedTasks_ParsedAndToggledCorrectly()
    {
        string md = "• [ ] Bullet task unchecked\n• [x] Bullet task checked";
        var card = new NoteCard("Tasks Note", md);

        Assert.Equal(2, card.Blocks.Count);

        Assert.Equal(MarkdownBlockType.TaskItem, card.Blocks[0].BlockType);
        Assert.Equal("Bullet task unchecked", card.Blocks[0].Text);
        Assert.False(card.Blocks[0].IsChecked);

        Assert.Equal(MarkdownBlockType.TaskItem, card.Blocks[1].BlockType);
        Assert.Equal("Bullet task checked", card.Blocks[1].Text);
        Assert.True(card.Blocks[1].IsChecked);

        // Toggle index 0
        Assert.True(card.ToggleTaskCheckbox(0, true));
        Assert.Contains("• [x] Bullet task unchecked", card.MarkdownContent);

        // Toggle back
        Assert.True(card.ToggleTaskCheckbox(0, false));
        Assert.Contains("• [ ] Bullet task unchecked", card.MarkdownContent);
    }

    [Fact]
    public void NoteCard_MarkdownParsing_ObsidianHeadings_ParsedCorrectly()
    {
        string md = "# Top Level Header\n## Second Level\n### Third Level\n#### Fourth Level";
        var card = new NoteCard("Obsidian Headings", md);

        Assert.Equal(4, card.Blocks.Count);
        Assert.Equal(MarkdownBlockType.Header, card.Blocks[0].BlockType);
        Assert.Equal("Top Level Header", card.Blocks[0].Text);

        Assert.Equal(MarkdownBlockType.Subheader, card.Blocks[1].BlockType);
        Assert.Equal("Second Level", card.Blocks[1].Text);

        Assert.Equal(MarkdownBlockType.Subheader, card.Blocks[2].BlockType);
        Assert.Equal("Third Level", card.Blocks[2].Text);

        Assert.Equal(MarkdownBlockType.Subheader, card.Blocks[3].BlockType);
        Assert.Equal("Fourth Level", card.Blocks[3].Text);
    }

    [Fact]
    public void NoteCard_MarkdownParsing_ImageTags_WikilinkAndStandard_ParsedCorrectly()
    {
        string md = "# Header\n![[Pasted_Image_20261106_120000000.png]]\n![Alt Description](images/chart.png)\n- [ ] Task below";
        var card = new NoteCard("Images Note", md);

        Assert.Equal(4, card.Blocks.Count);

        Assert.Equal(MarkdownBlockType.Header, card.Blocks[0].BlockType);

        Assert.Equal(MarkdownBlockType.Image, card.Blocks[1].BlockType);
        Assert.Equal("![[Pasted_Image_20261106_120000000.png]]", card.Blocks[1].Text);
        Assert.Equal("Pasted_Image_20261106_120000000.png", card.Blocks[1].ImagePath);

        Assert.Equal(MarkdownBlockType.Image, card.Blocks[2].BlockType);
        Assert.Equal("![Alt Description](images/chart.png)", card.Blocks[2].Text);
        Assert.Equal("images/chart.png", card.Blocks[2].ImagePath);

        Assert.Equal(MarkdownBlockType.TaskItem, card.Blocks[3].BlockType);
    }

    [Fact]
    public void NoteCard_MarkdownParsing_ImageWikilink_WithPipeSize_ExtractsCleanPath()
    {
        string md = "![[Pasted_Image_20261106_120000000.png|300]]";
        var card = new NoteCard("Pipe Image", md);

        Assert.Single(card.Blocks);
        Assert.Equal(MarkdownBlockType.Image, card.Blocks[0].BlockType);
        Assert.Equal("Pasted_Image_20261106_120000000.png", card.Blocks[0].ImagePath);
    }
}
