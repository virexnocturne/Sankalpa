using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Blocker.Core;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Blocker.App;

public class MeditationJournalItemViewModel
{
    public int Id { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime StartLocal => StartUtc.ToLocalTime();
    public string DisplayDate => StartLocal.ToString("MMM d, yyyy - h:mm tt");
    public long DurationSeconds { get; set; }
    public long DurationMinutes => Math.Max(1, DurationSeconds / 60);
    public string Soundscape { get; set; } = string.Empty;
    public string DurationAndSoundscape => $"{DurationMinutes} mins" + (string.IsNullOrWhiteSpace(Soundscape) ? "" : $" • {Soundscape}");
    public bool Completed { get; set; }
    public string StatusText => Completed ? "Completed" : "Interrupted";
    public Brush StatusBadgeBackground => Completed ? new SolidColorBrush(Color.FromArgb(40, 91, 213, 138)) : new SolidColorBrush(Color.FromArgb(40, 229, 179, 65));
    public Brush StatusBadgeForeground => Completed ? new SolidColorBrush(Color.FromRgb(91, 213, 138)) : new SolidColorBrush(Color.FromRgb(229, 179, 65));
    public string Notes { get; set; } = string.Empty;
    public string NoteSnippet
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Notes)) return "(No reflections entered)";
            var clean = Notes.Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length > 65 ? clean[..65] + "..." : clean;
        }
    }
}

public partial class UniversalNotesDialog : Window
{
    private readonly List<MeditationJournalItemViewModel> _allSessions = new();
    private readonly IPipeClient? _pipeClient;

    public UniversalNotesDialog(
        IEnumerable<MeditationHistoryItemDto>? historyItems = null,
        IPipeClient? pipeClient = null)
    {
        InitializeComponent();
        WindowHelper.EnableDarkMode(this);

        _pipeClient = pipeClient;

        if (historyItems != null)
        {
            PopulateFromDtos(historyItems);
        }
        else if (_pipeClient != null)
        {
            Loaded += async (_, _) => await LoadFromPipeAsync();
        }

        RefreshListAndStats();
    }

    private void PopulateFromDtos(IEnumerable<MeditationHistoryItemDto> items)
    {
        _allSessions.Clear();
        foreach (var item in items.OrderByDescending(x => x.StartUtc))
        {
            _allSessions.Add(new MeditationJournalItemViewModel
            {
                Id = item.Id,
                StartUtc = item.StartUtc,
                DurationSeconds = item.CompletedDurationSeconds > 0 ? item.CompletedDurationSeconds : item.TargetDurationSeconds,
                Completed = item.Completed,
                Soundscape = item.Soundscape ?? "Silent",
                Notes = item.Notes ?? string.Empty
            });
        }
    }

    private async Task LoadFromPipeAsync()
    {
        if (_pipeClient == null) return;
        try
        {
            var req = new Request { Command = CommandNames.GetMeditationHistory };
            var res = await _pipeClient.SendRequestAsync(req);
            if (res.Connected && res.Response != null && res.Response.Ok && !string.IsNullOrWhiteSpace(res.Response.Data))
            {
                var dtos = JsonSerializer.Deserialize<List<MeditationHistoryItemDto>>(res.Response.Data, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (dtos != null)
                {
                    PopulateFromDtos(dtos);
                    RefreshListAndStats();
                }
            }
        }
        catch
        {
            // Fallback gracefully if pipe request fails
        }
    }

    public void AddSessionRecord(MeditationJournalItemViewModel session)
    {
        _allSessions.Insert(0, session);
        RefreshListAndStats();
    }

    private void RefreshListAndStats()
    {
        string filter = SearchBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? _allSessions
            : _allSessions.Where(s =>
                s.DisplayDate.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                s.Notes.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                s.Soundscape.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                s.StatusText.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        SessionsListBox.ItemsSource = filtered;

        // Calculate Stats
        int completedCount = _allSessions.Count(s => s.Completed);
        long totalMinutes = _allSessions.Where(s => s.Completed).Sum(s => s.DurationMinutes);
        SummaryStatsTextBlock.Text = $"{completedCount} completed session{(completedCount == 1 ? "" : "s")} • {totalMinutes} total minutes meditated";

        if (filtered.Count > 0 && SessionsListBox.SelectedItem == null)
        {
            SessionsListBox.SelectedIndex = 0;
        }
        else if (filtered.Count == 0)
        {
            NoSelectionPlaceholder.Visibility = Visibility.Visible;
            SelectedDetailPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshListAndStats();
    }

    private void SessionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionsListBox.SelectedItem is MeditationJournalItemViewModel selected)
        {
            NoSelectionPlaceholder.Visibility = Visibility.Collapsed;
            SelectedDetailPanel.Visibility = Visibility.Visible;

            DetailTitleTextBlock.Text = selected.StartLocal.ToString("MMMM d, yyyy - h:mm tt");
            DetailMetaTextBlock.Text = $"{selected.DurationMinutes} mins meditated • {selected.Soundscape}";

            DetailBadgeText.Text = selected.StatusText;
            DetailBadgeText.Foreground = selected.StatusBadgeForeground;

            ReflectionDetailTextBox.Text = string.IsNullOrWhiteSpace(selected.Notes)
                ? "(No reflection notes recorded for this meditation session.)"
                : selected.Notes;
        }
        else
        {
            NoSelectionPlaceholder.Visibility = Visibility.Visible;
            SelectedDetailPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ExportMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (_allSessions.Count == 0)
        {
            MessageBox.Show(this, "There are no meditation session notes to export yet.", "Meditation Journal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Meditation Journal to Markdown",
            FileName = $"sankalpa-meditation-journal-{DateTime.Now:yyyy-MM-dd}.md",
            Filter = "Markdown Files (*.md)|*.md|Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
            DefaultExt = ".md"
        };

        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Sankalpa Meditation Journal & Contemplation Notes");
                sb.AppendLine();
                sb.AppendLine($"*Exported on {DateTime.Now:MMMM d, yyyy 'at' h:mm tt}*");
                sb.AppendLine();

                int completedCount = _allSessions.Count(s => s.Completed);
                long totalMinutes = _allSessions.Where(s => s.Completed).Sum(s => s.DurationMinutes);
                sb.AppendLine("## Summary Statistics");
                sb.AppendLine($"- **Completed Sessions**: {completedCount}");
                sb.AppendLine($"- **Total Time Meditated**: {totalMinutes} minutes");
                sb.AppendLine($"- **Total Sessions Logged**: {_allSessions.Count}");
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();

                foreach (var s in _allSessions.OrderByDescending(x => x.StartUtc))
                {
                    sb.AppendLine($"## Session: {s.DisplayDate}");
                    sb.AppendLine($"- **Status**: {s.StatusText}");
                    sb.AppendLine($"- **Duration**: {s.DurationMinutes} minutes");
                    if (!string.IsNullOrWhiteSpace(s.Soundscape))
                    {
                        sb.AppendLine($"- **Soundscape**: {s.Soundscape}");
                    }
                    sb.AppendLine();
                    sb.AppendLine("### Reflection & Contemplation");
                    sb.AppendLine();
                    if (string.IsNullOrWhiteSpace(s.Notes))
                    {
                        sb.AppendLine("*(No reflection notes entered)*");
                    }
                    else
                    {
                        sb.AppendLine(s.Notes.Trim());
                    }
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                }

                File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                ExportStatusMessage.Text = $"Exported {Path.GetFileName(dialog.FileName)} successfully.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to export journal: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
