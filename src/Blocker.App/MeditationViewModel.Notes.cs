using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Blocker.Core;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Blocker.App;

public partial class MeditationViewModel
{
    private readonly DispatcherTimer _noteDebounceTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };
    private readonly DispatcherTimer _transientSaveTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2.5)
    };

    private string _currentNote = string.Empty;
    private string _autoSaveStatus = string.Empty;
    private bool _isNotesDrawerOpen = false;
    private bool _isComposerOpen = false;
    private string? _editingNoteId;
    private string _composerTitle = string.Empty;
    private string _composerMarkdown = string.Empty;
    private ZenQuickNotesOverlay? _quickNotesOverlay;
    private bool _isUpdatingFromOverlay;

    public ObservableCollection<NoteCard> NotesCollection { get; } = new();

    public ICommand ExportNotesCommand { get; private set; } = null!;
    public ICommand ToggleQuickNotesCommand { get; private set; } = null!;
    public ICommand ToggleNotesDrawerCommand { get; private set; } = null!;
    public ICommand NewNoteCommand { get; private set; } = null!;
    public ICommand EditNoteCommand { get; private set; } = null!;
    public ICommand DeleteNoteCommand { get; private set; } = null!;
    public ICommand SaveNoteCardCommand { get; private set; } = null!;
    public ICommand CancelNoteComposerCommand { get; private set; } = null!;
    public ICommand ToggleCardExpandCommand { get; private set; } = null!;
    public ICommand ToggleSeeMoreCommand { get; private set; } = null!;

    public bool HasNoNotes => NotesCollection.Count == 0;
    public bool HasNotes => NotesCollection.Count > 0;
    public string NotesCountText => $"{NotesCollection.Count} note{(NotesCollection.Count == 1 ? "" : "s")}";

    public bool IsNotesDrawerOpen
    {
        get => _isNotesDrawerOpen;
        set => SetProperty(ref _isNotesDrawerOpen, value);
    }

    public bool IsComposerOpen
    {
        get => _isComposerOpen;
        set
        {
            if (SetProperty(ref _isComposerOpen, value))
            {
                OnPropertyChanged(nameof(IsComposerClosed));
            }
        }
    }

    public bool IsComposerClosed => !_isComposerOpen;

    public string? EditingNoteId
    {
        get => _editingNoteId;
        set
        {
            if (SetProperty(ref _editingNoteId, value))
            {
                OnPropertyChanged(nameof(ComposerHeadingText));
            }
        }
    }

    public string ComposerHeadingText => string.IsNullOrEmpty(_editingNoteId) ? "New Note" : "Edit Note";

    public string ComposerTitle
    {
        get => _composerTitle;
        set => SetProperty(ref _composerTitle, value);
    }

    public string ComposerMarkdown
    {
        get => _composerMarkdown;
        set => SetProperty(ref _composerMarkdown, value);
    }

    public string CurrentNote
    {
        get => _currentNote;
        set
        {
            if (SetProperty(ref _currentNote, value))
            {
                var cards = NoteCardCollection.Deserialize(value);
                if (NoteCardCollection.SyncCards(NotesCollection, cards, WireCard))
                {
                    OnPropertyChanged(nameof(HasNoNotes));
                    OnPropertyChanged(nameof(HasNotes));
                    OnPropertyChanged(nameof(NotesCountText));
                }
                AutoSaveStatus = "Saving...";
                _noteDebounceTimer.Stop();
                _noteDebounceTimer.Start();
            }
        }
    }

    public string AutoSaveStatus
    {
        get => _autoSaveStatus;
        private set => SetProperty(ref _autoSaveStatus, value);
    }

    private void InitializeNotes()
    {
        _noteDebounceTimer.Tick += NoteDebounceTimer_Tick;

        _transientSaveTimer.Tick += (s, e) =>
        {
            _transientSaveTimer.Stop();
            AutoSaveStatus = string.Empty;
        };

        ExportNotesCommand = new RelayCommand(ExportCurrentNotes);
        ToggleQuickNotesCommand = new RelayCommand(ToggleQuickNotes);
        ToggleNotesDrawerCommand = new RelayCommand(() =>
        {
            IsNotesDrawerOpen = !IsNotesDrawerOpen;
            if (IsNotesDrawerOpen && IsSoundscapeMenuOpen)
            {
                IsSoundscapeMenuOpen = false;
            }
        });

        NewNoteCommand = new RelayCommand(() =>
        {
            if (IsSoundscapeMenuOpen)
            {
                IsSoundscapeMenuOpen = false;
            }
            EditingNoteId = null;
            ComposerTitle = string.Empty;
            ComposerMarkdown = string.Empty;
            IsComposerOpen = true;
        });

        EditNoteCommand = new RelayCommand<NoteCard>(card =>
        {
            if (card == null) return;
            EditingNoteId = card.Id;
            ComposerTitle = card.Title;
            ComposerMarkdown = card.MarkdownContent;
            IsComposerOpen = true;
        });

        DeleteNoteCommand = new RelayCommand<NoteCard>(card =>
        {
            if (card == null) return;
            NotesCollection.Remove(card);
            OnPropertyChanged(nameof(HasNoNotes));
            OnPropertyChanged(nameof(HasNotes));
            OnPropertyChanged(nameof(NotesCountText));
            SaveActiveNotes();
        });

        SaveNoteCardCommand = new RelayCommand(() =>
        {
            string title = ComposerTitle?.Trim() ?? string.Empty;
            string content = ComposerMarkdown ?? string.Empty;

            // Auto-detect title from first heading or first non-empty line if title input is left empty
            if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(content))
            {
                var lines = content.Replace("\r", "").Split('\n');
                foreach (var line in lines)
                {
                    string tr = line.Trim();
                    if (tr.StartsWith("# ") || tr.StartsWith("## ") || tr.StartsWith("### "))
                    {
                        title = tr.TrimStart('#').Trim();
                        break;
                    }
                    else if (!string.IsNullOrWhiteSpace(tr) && string.IsNullOrEmpty(title))
                    {
                        string clean = tr.TrimStart('-', '*', '•', '>', ' ', '[', ']', '☐', '☑');
                        if (clean.Length > 40) clean = clean[..40] + "...";
                        title = clean.Trim();
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = "Untitled Note";
            }

            if (!string.IsNullOrEmpty(EditingNoteId))
            {
                var existing = NotesCollection.FirstOrDefault(n => n.Id == EditingNoteId);
                if (existing != null)
                {
                    existing.Title = title;
                    existing.MarkdownContent = ComposerMarkdown;
                    existing.UpdatedAtUtc = DateTime.UtcNow;
                    existing.RebuildParsedBlocks();
                }
            }
            else
            {
                var newCard = new NoteCard(title, ComposerMarkdown);
                WireCard(newCard);
                NotesCollection.Insert(0, newCard);
            }

            IsComposerOpen = false;
            OnPropertyChanged(nameof(HasNoNotes));
            OnPropertyChanged(nameof(HasNotes));
            OnPropertyChanged(nameof(NotesCountText));
            SaveActiveNotes();
        });

        CancelNoteComposerCommand = new RelayCommand(() =>
        {
            IsComposerOpen = false;
        });

        ToggleCardExpandCommand = new RelayCommand<NoteCard>(card =>
        {
            if (card != null)
            {
                card.IsExpanded = !card.IsExpanded;
            }
        });

        ToggleSeeMoreCommand = new RelayCommand<NoteCard>(card =>
        {
            if (card != null)
            {
                card.IsFullExpanded = !card.IsFullExpanded;
            }
        });
    }

    private void WireCard(NoteCard card)
    {
        card.OnCardChanged = () =>
        {
            AutoSaveStatus = "Saving...";
            _noteDebounceTimer.Stop();
            _noteDebounceTimer.Start();
        };
    }

    private void NoteDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _noteDebounceTimer.Stop();
        SaveNoteAsync();
    }

    public void SaveActiveNotes()
    {
        if (NotesCollection.Count > 0)
        {
            _currentNote = NoteCardCollection.Serialize(NotesCollection.ToList());
        }
        else
        {
            _currentNote = string.Empty;
        }
        OnPropertyChanged(nameof(CurrentNote));

        NotesVaultStore.QueueSave(NotesCollection);

        // If _quickNotesOverlay is open or exists, sync it immediately (skip if overlay originated change):
        if (!_isUpdatingFromOverlay)
        {
            _quickNotesOverlay?.LoadNotes(_currentNote);
        }

        if (_activeZenShield != null)
        {
            _activeZenShield.NotesText = _currentNote;
        }

        AutoSaveStatus = "Saved";
        _transientSaveTimer.Stop();
        _transientSaveTimer.Start();

        if (_activeSessionId.HasValue && _pipeClient != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var payload = JsonSerializer.Serialize(new SaveMeditationNoteRequest
                    {
                        SessionId = _activeSessionId.Value,
                        Notes = _currentNote
                    });
                    var req = new Request
                    {
                        Command = CommandNames.SaveMeditationNote,
                        Args = new Dictionary<string, string> { ["data"] = payload }
                    };
                    await _pipeClient.SendRequestAsync(req);
                }
                catch { }
            });
        }
    }

    private void SaveNoteAsync()
    {
        SaveActiveNotes();
    }

    public void ExportCurrentNotes()
    {
        if (NotesCollection.Count == 0 && string.IsNullOrWhiteSpace(_currentNote))
        {
            DarkDialogWindow.Show("No reflections have been written for this session yet.", "Universal Notes", isConfirmation: false);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Meditation Notes",
            FileName = $"meditation-notes-{DateTime.Now:yyyy-MM-dd}.md",
            Filter = "Markdown files (*.md)|*.md|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".md"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                string content = NotesCollection.Count > 0
                    ? NoteCardCollection.GenerateSessionBundleMarkdown(
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        $"{_targetDurationSeconds / 60}m",
                        NotesCollection.ToList())
                    : $"# Meditation Reflection\n\n**Date**: {DateTime.Now:MMMM d, yyyy - h:mm tt}\n**Duration**: {_targetDurationSeconds / 60} minutes\n\n---\n\n{_currentNote}";

                File.WriteAllText(dialog.FileName, content);
                AutoSaveStatus = "Exported to " + Path.GetFileName(dialog.FileName);
            }
            catch (Exception ex)
            {
                DarkDialogWindow.Show($"Failed to export note: {ex.Message}", "Export Failed");
            }
        }
    }

    public void ToggleQuickNotes()
    {
        if (_quickNotesOverlay != null && _quickNotesOverlay.IsVisible)
        {
            _quickNotesOverlay.Hide();
            return;
        }

        if (_quickNotesOverlay == null)
        {
            _quickNotesOverlay = new ZenQuickNotesOverlay();
            _quickNotesOverlay.OnNotesChanged = updatedNotes =>
            {
                _isUpdatingFromOverlay = true;
                try
                {
                    CurrentNote = updatedNotes;
                    if (_activeZenShield != null)
                    {
                        _activeZenShield.NotesText = updatedNotes;
                    }
                }
                finally
                {
                    _isUpdatingFromOverlay = false;
                }
            };
            _quickNotesOverlay.Closed += (s, e) => _quickNotesOverlay = null;
        }

        _quickNotesOverlay.LoadNotes(CurrentNote);
        _quickNotesOverlay.Show();
        _quickNotesOverlay.Activate();
    }
}
