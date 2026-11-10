using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Blocker.Core;
using Xunit;

namespace Sankalpa.Tests;

public class NotesLagOptimizationTests : IDisposable
{
    private readonly string _testTempDir;

    public NotesLagOptimizationTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), "SankalpaNotesLagTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        NotesVaultStore.SetVaultPathForTesting(null);
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, true);
            }
        }
        catch { }
    }

    // =========================================================================
    // 1. Vault store coalescing and background write flush
    // =========================================================================

    [Fact]
    public void NotesVaultStore_FlushSync_FlushesImmediatelyToDisk()
    {
        string vaultPath = Path.Combine(_testTempDir, "active_notes.json");
        NotesVaultStore.SetVaultPathForTesting(vaultPath);

        var cards = new List<NoteCard>
        {
            new NoteCard { Id = "test-1", Title = "Card 1", MarkdownContent = "Immediate Content" }
        };

        // Queue save with long debounce
        NotesVaultStore.QueueSave(cards, TimeSpan.FromSeconds(30));

        // File should not exist yet before flush
        Assert.False(File.Exists(vaultPath));

        // FlushSync writes immediately
        NotesVaultStore.FlushSync(2000);

        Assert.True(File.Exists(vaultPath));
        string savedJson = File.ReadAllText(vaultPath);
        Assert.Contains("Immediate Content", savedJson);
    }

    [Fact]
    public void NotesVaultStore_QueueSave_CoalescesRapidWrites()
    {
        string vaultPath = Path.Combine(_testTempDir, "coalesced_notes.json");
        NotesVaultStore.SetVaultPathForTesting(vaultPath);

        // Rapidly queue 5 versions
        for (int i = 1; i <= 5; i++)
        {
            var cards = new List<NoteCard>
            {
                new NoteCard { Id = "c-1", Title = $"Version {i}", MarkdownContent = $"Content v{i}" }
            };
            NotesVaultStore.QueueSave(cards, TimeSpan.FromMilliseconds(80));
            Thread.Sleep(10);
        }

        // Flush to finish coalesced write
        NotesVaultStore.FlushSync(3000);

        Assert.True(File.Exists(vaultPath));
        var loaded = NotesVaultStore.LoadNotesFromVault();
        Assert.Single(loaded);
        Assert.Equal("Version 5", loaded[0].Title);
        Assert.Equal("Content v5", loaded[0].MarkdownContent);
    }

    [Fact]
    public void NotesVaultStore_SuppressesIdenticalWrites()
    {
        string vaultPath = Path.Combine(_testTempDir, "suppress_notes.json");
        NotesVaultStore.SetVaultPathForTesting(vaultPath);

        var cards = new List<NoteCard>
        {
            new NoteCard { Id = "same-1", Title = "Static", MarkdownContent = "No Change" }
        };

        NotesVaultStore.QueueSave(cards);
        NotesVaultStore.FlushSync(2000);
        Assert.True(File.Exists(vaultPath));
        DateTime firstWriteTime = File.GetLastWriteTimeUtc(vaultPath);

        Thread.Sleep(50);

        // Queue exact same cards again
        NotesVaultStore.QueueSave(cards);
        NotesVaultStore.FlushSync(2000);

        DateTime secondWriteTime = File.GetLastWriteTimeUtc(vaultPath);
        Assert.Equal(firstWriteTime, secondWriteTime);
    }

    // =========================================================================
    // 2. Card diffing / incremental update
    // =========================================================================

    [Fact]
    public void NoteCardCollection_SyncCards_UpdatesExistingCardInPlace()
    {
        var target = new ObservableCollection<NoteCard>();
        var cardOriginal = new NoteCard { Id = "card-1", Title = "Initial", MarkdownContent = "First text" };
        target.Add(cardOriginal);

        var incoming = new List<NoteCard>
        {
            new NoteCard { Id = "card-1", Title = "Updated Title", MarkdownContent = "Second text updated" }
        };

        bool changed = NoteCardCollection.SyncCards(target, incoming);

        Assert.True(changed);
        Assert.Single(target);
        // Instance reference is preserved!
        Assert.Same(cardOriginal, target[0]);
        Assert.Equal("Updated Title", target[0].Title);
        Assert.Equal("Second text updated", target[0].MarkdownContent);
    }

    [Fact]
    public void NoteCardCollection_SyncCards_RemovesDeletedAndAddsNewCards()
    {
        var target = new ObservableCollection<NoteCard>
        {
            new NoteCard { Id = "card-1", Title = "One" },
            new NoteCard { Id = "card-2", Title = "Two" }
        };

        var incoming = new List<NoteCard>
        {
            new NoteCard { Id = "card-2", Title = "Two" },
            new NoteCard { Id = "card-3", Title = "Three New" }
        };

        int addedCount = 0;
        bool changed = NoteCardCollection.SyncCards(target, incoming, _ => addedCount++);

        Assert.True(changed);
        Assert.Equal(2, target.Count);
        Assert.Equal("card-2", target[0].Id);
        Assert.Equal("card-3", target[1].Id);
        Assert.Equal("Three New", target[1].Title);
        Assert.Equal(1, addedCount);
    }

    [Fact]
    public void NoteCardCollection_SyncCards_ReturnsFalseWhenEquivalent()
    {
        var now = DateTime.UtcNow;
        var target = new ObservableCollection<NoteCard>
        {
            new NoteCard { Id = "c-1", Title = "Same", MarkdownContent = "Same", UpdatedAtUtc = now }
        };

        var incoming = new List<NoteCard>
        {
            new NoteCard { Id = "c-1", Title = "Same", MarkdownContent = "Same", UpdatedAtUtc = now }
        };

        bool changed = NoteCardCollection.SyncCards(target, incoming);
        Assert.False(changed);
    }

    [Fact]
    public void NoteCard_IsContentEquivalent_DetectsChangesAccurately()
    {
        var dt = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var cardA = new NoteCard { Id = "id-1", Title = "T", MarkdownContent = "M", UpdatedAtUtc = dt };
        var cardB = new NoteCard { Id = "id-1", Title = "T", MarkdownContent = "M", UpdatedAtUtc = dt };

        Assert.True(cardA.IsContentEquivalent(cardB));

        // Different title
        cardB.Title = "Other";
        Assert.False(cardA.IsContentEquivalent(cardB));
        cardB.Title = "T";

        // Different content
        cardB.MarkdownContent = "Other content";
        Assert.False(cardA.IsContentEquivalent(cardB));
        cardB.MarkdownContent = "M";

        // Different timestamp
        cardB.UpdatedAtUtc = dt.AddMinutes(1);
        Assert.False(cardA.IsContentEquivalent(cardB));
    }

    // =========================================================================
    // 3. Image reference extraction and thumbnail caching
    // =========================================================================

    [Fact]
    public void NoteCardCollection_ExtractImageReferences_ExtractsAllSyntaxes()
    {
        string md = @"# Design Ideas
Here is an Obsidian snip:
![[Snip_20261110_123456.png]]
With size parameters:
![[Diagram.png|400]]
And standard markdown:
![Screenshot](Assets/screenshot.jpg)
![With Title](Images/photo.webp 'A scenic view')
Duplicate test:
![[Snip_20261110_123456.png]]";

        var refs = NoteCardCollection.ExtractImageReferences(md);

        Assert.Equal(4, refs.Count);
        Assert.Contains("Snip_20261110_123456.png", refs);
        Assert.Contains("Diagram.png", refs);
        Assert.Contains("Assets/screenshot.jpg", refs);
        Assert.Contains("Images/photo.webp", refs);
    }

    [Fact]
    public void NoteCardCollection_ExtractImageReferences_EmptyAndNullHandledSafely()
    {
        Assert.Empty(NoteCardCollection.ExtractImageReferences(null));
        Assert.Empty(NoteCardCollection.ExtractImageReferences(""));
        Assert.Empty(NoteCardCollection.ExtractImageReferences("   \r\n\t  "));
        Assert.Empty(NoteCardCollection.ExtractImageReferences("Just plain text with no images"));
    }

    [Fact]
    public void NotesAttachmentCache_KeyingAndRetrieval()
    {
        var cache = new NotesAttachmentCache<string>();
        string path = @"C:\Users\test\image.png";
        var writeTime = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);

        // Initial miss
        Assert.False(cache.TryGet(path, writeTime, 120, out string? cachedVal));
        Assert.Null(cachedVal);

        // Store value
        cache.Set(path, writeTime, 120, "BitmapThumbnail120");
        Assert.Equal(1, cache.Count);

        // Hit with same parameters
        Assert.True(cache.TryGet(path, writeTime, 120, out cachedVal));
        Assert.Equal("BitmapThumbnail120", cachedVal);

        // Miss with different target height
        Assert.False(cache.TryGet(path, writeTime, 480, out _));

        // Miss with newer file timestamp
        var newerWriteTime = writeTime.AddMinutes(5);
        Assert.False(cache.TryGet(path, newerWriteTime, 120, out _));

        // Clear cache
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(path, writeTime, 120, out _));
    }
}
