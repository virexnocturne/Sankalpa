using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Blocker.Core;

public static class NoteCardCollection
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static string Serialize(List<NoteCard> cards)
    {
        if (cards == null || cards.Count == 0)
        {
            return "[]";
        }
        return JsonSerializer.Serialize(cards, JsonOptions);
    }

    public static List<NoteCard> Deserialize(string? jsonOrText)
    {
        if (string.IsNullOrWhiteSpace(jsonOrText))
        {
            return new List<NoteCard>();
        }

        string trimmed = jsonOrText.Trim();
        if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<NoteCard>>(trimmed, JsonOptions);
                if (list != null)
                {
                    return list;
                }
            }
            catch
            {
                // Fall through to plain-text backward compatibility
            }
        }

        // Backward compatibility for legacy single-string notes
        return new List<NoteCard>
        {
            new NoteCard
            {
                Title = "Session Reflection",
                MarkdownContent = jsonOrText,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            }
        };
    }

    /// <summary>
    /// Format 1: Session Bundle - All cards from a single session compiled into one markdown file.
    /// </summary>
    public static string GenerateSessionBundleMarkdown(string sessionDate, string duration, List<NoteCard> cards)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: Sankalpa Meditation Notes - {sessionDate}");
        sb.AppendLine($"date: {sessionDate}");
        sb.AppendLine($"duration: {duration}");
        sb.AppendLine($"cards_count: {cards.Count}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# 🧘 Meditation Notes — {sessionDate}");
        sb.AppendLine($"*Duration: {duration}*");
        sb.AppendLine();

        if (cards.Count == 0)
        {
            sb.AppendLine("*No notes were recorded during this session.*");
            return sb.ToString();
        }

        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            string title = string.IsNullOrWhiteSpace(card.Title) ? $"Note {i + 1}" : card.Title;
            sb.AppendLine($"## {title}");
            sb.AppendLine($"*Created: {card.CreatedAtUtc:yyyy-MM-dd HH:mm:ss UTC}*");
            sb.AppendLine();
            sb.AppendLine(card.MarkdownContent);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Format 2: Vault Mode - Writes each card as an individual markdown file into targetDirectory.
    /// Returns the list of created file paths.
    /// </summary>
    public static List<string> GenerateVaultFiles(string targetDirectory, string sessionDate, List<NoteCard> cards)
    {
        var createdPaths = new List<string>();
        if (!Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        string safeDate = sessionDate.Replace(":", "-").Replace("/", "-").Replace(" ", "_");

        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            string safeTitle = SanitizeFileName(string.IsNullOrWhiteSpace(card.Title) ? $"Note_{i + 1}" : card.Title);
            string fileName = $"{safeDate}_{safeTitle}.md";
            string filePath = Path.Combine(targetDirectory, fileName);

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"title: \"{card.Title}\"");
            sb.AppendLine($"date: {sessionDate}");
            sb.AppendLine($"session_date: {sessionDate}");
            sb.AppendLine($"card_id: {card.Id}");
            sb.AppendLine("tags: [sankalpa, meditation, focus]");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"# {card.Title}");
            sb.AppendLine();
            sb.AppendLine(card.MarkdownContent);
            sb.AppendLine();

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            createdPaths.Add(filePath);
        }

        return createdPaths;
    }

    /// <summary>
    /// Format 3: Master Journal - Consolidates notes across multiple sessions into a single chronological archive.
    /// </summary>
    public static string GenerateMasterJournalMarkdown(List<(string sessionDate, string duration, List<NoteCard> cards)> sessions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 📖 Sankalpa Meditation Master Journal");
        sb.AppendLine($"*Exported on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}*");
        sb.AppendLine();

        int totalNotes = 0;
        foreach (var (_, _, cards) in sessions)
        {
            totalNotes += cards.Count;
        }
        sb.AppendLine($"- **Total Sessions**: {sessions.Count}");
        sb.AppendLine($"- **Total Notes**: {totalNotes}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var (sessionDate, duration, cards) in sessions)
        {
            sb.AppendLine($"## Session: {sessionDate} ({duration})");
            sb.AppendLine();
            if (cards.Count == 0)
            {
                sb.AppendLine("*No notes.*");
            }
            else
            {
                foreach (var card in cards)
                {
                    string title = string.IsNullOrWhiteSpace(card.Title) ? "Untitled Note" : card.Title;
                    sb.AppendLine($"### {title}");
                    sb.AppendLine(card.MarkdownContent);
                    sb.AppendLine();
                }
            }
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (Array.IndexOf(invalid, c) < 0 && c != ' ')
            {
                sb.Append(c);
            }
            else if (c == ' ')
            {
                sb.Append('_');
            }
        }
        return sb.Length > 0 ? sb.ToString() : "Note";
    }
}
