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
}
