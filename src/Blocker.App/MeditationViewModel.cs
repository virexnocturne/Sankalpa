using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Blocker.Core;
using Microsoft.Win32;
using Application = System.Windows.Application;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Blocker.App;

public class MeditationViewModel : ViewModelBase, IDisposable
{
    private readonly IPipeClient? _pipeClient;
    private readonly AmbientSoundEngine _soundEngine;
    private readonly DispatcherTimer _sessionTimer;
    private readonly DispatcherTimer _noteDebounceTimer;

    private long _targetDurationSeconds = 1200; // 20m default
    private long _remainingSeconds = 1200;
    private bool _isActive;
    private LockMode _lockMode = LockMode.Locked;
    private SoundscapeType _selectedSoundscape = SoundscapeType.Rain;
    private string _currentNote = string.Empty;
    private string _autoSaveStatus = "All changes saved";
    private int? _activeSessionId;
    private ZenShieldWindow? _activeZenShield;

    // Schedule properties
    private bool _isScheduleEnabled;
    private string _scheduleTime = "15:00";
    private List<DayOfWeek> _scheduleDays = new() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

    public ObservableCollection<string> AllowedApps { get; } = new();
    public ObservableCollection<NoteCard> NotesCollection { get; } = new();

    public ICommand StartMeditationCommand { get; }
    public ICommand StopMeditationCommand { get; }
    public ICommand OpenEditDialogCommand { get; }
    public ICommand OpenUniversalNotesCommand { get; }
    public ICommand ToggleSoundscapeCommand { get; }
    public ICommand SelectSoundscapeCommand { get; }
    public ICommand ExportNotesCommand { get; }
    public ICommand ToggleQuickNotesCommand { get; }
    public ICommand ToggleNotesDrawerCommand { get; }
    public ICommand ToggleSoundscapeMenuCommand { get; }
    public ICommand NewNoteCommand { get; }
    public ICommand EditNoteCommand { get; }
    public ICommand DeleteNoteCommand { get; }
    public ICommand SaveNoteCardCommand { get; }
    public ICommand CancelNoteComposerCommand { get; }
    public ICommand ToggleCardExpandCommand { get; }
    public ICommand ToggleSeeMoreCommand { get; }

    public MeditationViewModel(IPipeClient? pipeClient = null)
    {
        _pipeClient = pipeClient;
        _soundEngine = new AmbientSoundEngine();

        _sessionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _sessionTimer.Tick += SessionTimer_Tick;

        _noteDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _noteDebounceTimer.Tick += NoteDebounceTimer_Tick;

        // Initialize Commands
        StartMeditationCommand = new RelayCommand(StartMeditation);
        StopMeditationCommand = new RelayCommand(StopMeditation, () => CanStop);
        OpenEditDialogCommand = new RelayCommand(OpenEditDialog);
        OpenUniversalNotesCommand = new RelayCommand(OpenUniversalNotes);
        ToggleSoundscapeCommand = new RelayCommand(ToggleSoundscape);
        SelectSoundscapeCommand = new RelayCommand<string>(soundName =>
        {
            if (Enum.TryParse<SoundscapeType>(soundName, true, out var parsed))
            {
                SelectedSoundscape = parsed;
                _soundEngine.SetSoundscape(parsed);
                OnPropertyChanged(nameof(IsSoundscapePlaying));
            }
        });
        ExportNotesCommand = new RelayCommand(ExportCurrentNotes);
        ToggleQuickNotesCommand = new RelayCommand(ToggleQuickNotes);

        ToggleNotesDrawerCommand = new RelayCommand(() => IsNotesDrawerOpen = !IsNotesDrawerOpen);
        ToggleSoundscapeMenuCommand = new RelayCommand(() => IsSoundscapeMenuOpen = !IsSoundscapeMenuOpen);

        NewNoteCommand = new RelayCommand(() =>
        {
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
            SaveNoteAsync();
        });

        SaveNoteCardCommand = new RelayCommand(() =>
        {
            string title = string.IsNullOrWhiteSpace(ComposerTitle) ? "Untitled Note" : ComposerTitle.Trim();
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
            SaveNoteAsync();
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

        UpdateTimerDisplay();
    }

    #region Properties

    public static string FormatTimer(long totalSeconds) => UiLogic.FormatMeditationTimer(totalSeconds);

    public string CurrentTimerDisplay => FormatTimer(_isActive ? _remainingSeconds : _targetDurationSeconds);

    public long TargetDurationSeconds
    {
        get => _targetDurationSeconds;
        set
        {
            if (SetProperty(ref _targetDurationSeconds, value))
            {
                if (!_isActive)
                {
                    RemainingSeconds = value;
                    OnPropertyChanged(nameof(CurrentTimerDisplay));
                }
            }
        }
    }

    public long RemainingSeconds
    {
        get => _remainingSeconds;
        set
        {
            if (SetProperty(ref _remainingSeconds, value))
            {
                OnPropertyChanged(nameof(CurrentTimerDisplay));
            }
        }
    }

    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(IsCompleteLocked));
                OnPropertyChanged(nameof(IsChallengeLocked));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(CurrentTimerDisplay));
                (StopMeditationCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsIdle => !_isActive;

    public LockMode LockMode
    {
        get => _lockMode;
        set
        {
            if (SetProperty(ref _lockMode, value))
            {
                OnPropertyChanged(nameof(IsCompleteLocked));
                OnPropertyChanged(nameof(IsChallengeLocked));
                OnPropertyChanged(nameof(CanStop));
                (StopMeditationCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCompleteLocked => _isActive && _lockMode == LockMode.Locked;

    public bool IsChallengeLocked => _isActive && (_lockMode == LockMode.TypingChallenge || _lockMode == LockMode.Password);

    public bool CanStop => _isActive && !IsCompleteLocked;

    public SoundscapeType SelectedSoundscape
    {
        get => _selectedSoundscape;
        set
        {
            if (SetProperty(ref _selectedSoundscape, value))
            {
                OnPropertyChanged(nameof(SelectedSoundscapeName));
                OnPropertyChanged(nameof(IsRainSelected));
                OnPropertyChanged(nameof(IsLibrarySelected));
                OnPropertyChanged(nameof(IsForestSelected));
                OnPropertyChanged(nameof(IsBowlsSelected));
                OnPropertyChanged(nameof(IsMuteSelected));
                OnPropertyChanged(nameof(IsSoundscapePlaying));
            }
        }
    }

    public string SelectedSoundscapeName => _selectedSoundscape switch
    {
        SoundscapeType.Rain => "Rain",
        SoundscapeType.Library => "Library",
        SoundscapeType.Forest => "Forest",
        SoundscapeType.SingingBowls => "Singing Bowls",
        _ => "Mute"
    };

    public bool IsRainSelected => _selectedSoundscape == SoundscapeType.Rain;
    public bool IsLibrarySelected => _selectedSoundscape == SoundscapeType.Library;
    public bool IsForestSelected => _selectedSoundscape == SoundscapeType.Forest;
    public bool IsBowlsSelected => _selectedSoundscape == SoundscapeType.SingingBowls;
    public bool IsMuteSelected => _selectedSoundscape == SoundscapeType.None;

    public bool IsSoundscapePlaying => _soundEngine.IsPlaying;
    public string SelectedSoundscapeDisplayName => SelectedSoundscapeName;

    public bool HasNoNotes => NotesCollection.Count == 0;
    public bool HasNotes => NotesCollection.Count > 0;
    public string NotesCountText => $"{NotesCollection.Count} note{(NotesCollection.Count == 1 ? "" : "s")}";

    private bool _isNotesDrawerOpen = false;
    public bool IsNotesDrawerOpen
    {
        get => _isNotesDrawerOpen;
        set => SetProperty(ref _isNotesDrawerOpen, value);
    }

    private bool _isSoundscapeMenuOpen = false;
    public bool IsSoundscapeMenuOpen
    {
        get => _isSoundscapeMenuOpen;
        set => SetProperty(ref _isSoundscapeMenuOpen, value);
    }

    private double _soundscapeVolume = 60.0;
    public double SoundscapeVolume
    {
        get => _soundscapeVolume;
        set
        {
            if (SetProperty(ref _soundscapeVolume, Math.Clamp(value, 0.0, 100.0)))
            {
                _soundEngine.Volume = _soundscapeVolume / 100.0;
                OnPropertyChanged(nameof(SoundscapeVolumeText));
            }
        }
    }

    public string SoundscapeVolumeText => $"{(int)_soundscapeVolume}%";

    private bool _isComposerOpen = false;
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

    private string? _editingNoteId;
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

    private string _composerTitle = string.Empty;
    public string ComposerTitle
    {
        get => _composerTitle;
        set => SetProperty(ref _composerTitle, value);
    }

    private string _composerMarkdown = string.Empty;
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
                if (NotesCollection.Count == 0 && !string.IsNullOrWhiteSpace(value))
                {
                    var cards = NoteCardCollection.Deserialize(value);
                    foreach (var card in cards)
                    {
                        WireCard(card);
                        NotesCollection.Add(card);
                    }
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

    public bool IsScheduleEnabled
    {
        get => _isScheduleEnabled;
        set => SetProperty(ref _isScheduleEnabled, value);
    }

    public string ScheduleTime
    {
        get => _scheduleTime;
        set => SetProperty(ref _scheduleTime, value);
    }

    public List<DayOfWeek> ScheduleDays
    {
        get => _scheduleDays;
        set => SetProperty(ref _scheduleDays, value);
    }

    #endregion

    #region Session Lifecycle

    public void StartMeditation()
    {
        if (_isActive) return;

        IsActive = true;
        RemainingSeconds = _targetDurationSeconds;
        UpdateTimerDisplay();

        // 1. Play ambient soundscape
        if (_selectedSoundscape != SoundscapeType.None)
        {
            try
            {
                _soundEngine.SetSoundscape(_selectedSoundscape);
            }
            catch
            {
                // Audio failure shouldn't prevent lockdown
            }
        }

        // 2. Launch Zen Shield Window
        try
        {
            _activeZenShield = new ZenShieldWindow(
                (int)_targetDurationSeconds,
                LockMode == LockMode.Locked,
                LockMode.ToString(),
                AllowedApps.ToList(),
                "Meditation & Lockdown",
                this)
            {
                NotesText = CurrentNote
            };

            _activeZenShield.OnToggleNotesRequested = ToggleQuickNotes;
            _activeZenShield.OnNotesChanged = text =>
            {
                CurrentNote = text;
            };

            _activeZenShield.OnSessionEnded = () =>
            {
                StopMeditationInternal();
            };

            _activeZenShield.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MeditationViewModel] Error opening ZenShield: {ex.Message}");
        }

        // 3. Start local countdown
        _sessionTimer.Start();

        // 4. Send IPC request if service connected
        _ = Task.Run(async () =>
        {
            if (_pipeClient != null)
            {
                try
                {
                    var payload = JsonSerializer.Serialize(new StartMeditationRequest
                    {
                        TargetDurationSeconds = _targetDurationSeconds,
                        AllowedApps = AllowedApps.ToList(),
                        LockMode = LockMode.ToString(),
                        Soundscape = SelectedSoundscapeName
                    });
                    var req = new Request
                    {
                        Command = CommandNames.StartMeditation,
                        Args = new Dictionary<string, string> { ["data"] = payload }
                    };
                    var res = await _pipeClient.SendRequestAsync(req);
                    if (res.Connected && res.Response != null && res.Response.Ok && int.TryParse(res.Response.Data, out int id))
                    {
                        _activeSessionId = id;
                    }
                }
                catch
                {
                    // Fallback locally
                }
            }
        });
    }

    public void StopMeditation()
    {
        if (!_isActive) return;

        if (IsCompleteLocked)
        {
            DarkDialogWindow.Show(
                "Meditation lockdown is in Complete Lock mode. The session cannot be terminated early until the countdown timer expires.",
                "Complete Lock Active",
                isConfirmation: false,
                primaryText: "OK");
            return;
        }

        if (LockMode == LockMode.TypingChallenge)
        {
            var challenge = "I choose to return to my desktop with intention and focus.";
            var dlg = new TypingChallengeDialog(challenge)
            {
                Owner = Application.Current?.MainWindow
            };
            if (dlg.ShowDialog() != true)
            {
                return;
            }
        }
        else if (LockMode == LockMode.Password)
        {
            var dlg = new PasswordUnlockDialog("Meditation Session")
            {
                Owner = Application.Current?.MainWindow
            };
            if (dlg.ShowDialog() != true)
            {
                return;
            }
        }

        StopMeditationInternal();
    }

    private void StopMeditationInternal()
    {
        _quickNotesOverlay?.Close();
        _quickNotesOverlay = null;
        _sessionTimer.Stop();
        IsActive = false;
        RemainingSeconds = _targetDurationSeconds;
        UpdateTimerDisplay();

        _soundEngine.Stop(fadeOut: true);
        OnPropertyChanged(nameof(IsSoundscapePlaying));

        if (_activeZenShield != null && _activeZenShield.IsLoaded)
        {
            try
            {
                _activeZenShield.Close();
            }
            catch { }
            _activeZenShield = null;
        }

        // Auto-save any pending note before finalizing
        _noteDebounceTimer.Stop();
        SaveNoteAsync();

        // Notify Service over IPC
        _ = Task.Run(async () =>
        {
            if (_pipeClient != null)
            {
                try
                {
                    await _pipeClient.SendRequestAsync(new Request { Command = CommandNames.StopMeditation });
                }
                catch { }
            }
        });
    }

    private void SessionTimer_Tick(object? sender, EventArgs e)
    {
        if (RemainingSeconds > 0)
        {
            RemainingSeconds--;
            UpdateTimerDisplay();
        }
        else
        {
            // Completed!
            StopMeditationInternal();
        }
    }

    private void UpdateTimerDisplay()
    {
        OnPropertyChanged(nameof(CurrentTimerDisplay));
    }

    #endregion

    #region Audio & Notes

    public void ToggleSoundscape()
    {
        if (_soundEngine.IsPlaying)
        {
            _soundEngine.Pause();
        }
        else
        {
            if (_selectedSoundscape == SoundscapeType.None)
            {
                SelectedSoundscape = SoundscapeType.Rain;
            }
            _soundEngine.SetSoundscape(_selectedSoundscape);
        }
        OnPropertyChanged(nameof(IsSoundscapePlaying));
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

    private void SaveNoteAsync()
    {
        AutoSaveStatus = "Saved";
        if (NotesCollection.Count > 0)
        {
            _currentNote = NoteCardCollection.Serialize(NotesCollection.ToList());
            OnPropertyChanged(nameof(CurrentNote));
        }

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

    #endregion

    #region Dialogs

    public void OpenEditDialog()
    {
        var dlg = new MeditationEditDialog(
            (int)(TargetDurationSeconds / 60),
            LockMode,
            AllowedApps,
            IsScheduleEnabled,
            ScheduleTime,
            ScheduleDays,
            SelectedSoundscape)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dlg.ShowDialog() == true)
        {
            TargetDurationSeconds = dlg.DurationMinutes * 60;
            LockMode = dlg.LockProtection;

            AllowedApps.Clear();
            if (!dlg.CompleteLockout)
            {
                foreach (var app in dlg.AllowedApps)
                {
                    AllowedApps.Add(app);
                }
            }

            IsScheduleEnabled = dlg.ScheduleEnabled;
            ScheduleTime = dlg.ScheduleTime;
            ScheduleDays = dlg.ScheduleDays;
            SelectedSoundscape = dlg.SelectedSoundscape;

            // Sync schedule to Service
            if (IsScheduleEnabled && _pipeClient != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var payload = JsonSerializer.Serialize(new MeditationScheduleDto
                        {
                            Enabled = IsScheduleEnabled,
                            TimeOfDay = ScheduleTime,
                            DaysOfWeek = ScheduleDays.Select(d => d.ToString()).ToList(),
                            DurationSeconds = TargetDurationSeconds,
                            AllowedApps = AllowedApps.ToList(),
                            LockMode = LockMode.ToString(),
                            Soundscape = SelectedSoundscapeName
                        });
                        var req = new Request
                        {
                            Command = CommandNames.SaveMeditationSchedule,
                            Args = new Dictionary<string, string> { ["data"] = payload }
                        };
                        await _pipeClient.SendRequestAsync(req);
                    }
                    catch { }
                });
            }
        }
    }

    public void OpenUniversalNotes()
    {
        var dlg = new UniversalNotesDialog(null, _pipeClient)
        {
            Owner = Application.Current?.MainWindow
        };
        dlg.ShowDialog();
    }

    private ZenQuickNotesOverlay? _quickNotesOverlay;

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
                CurrentNote = updatedNotes;
                if (_activeZenShield != null)
                {
                    _activeZenShield.NotesText = updatedNotes;
                }
            };
            _quickNotesOverlay.Closed += (s, e) => _quickNotesOverlay = null;
        }

        _quickNotesOverlay.LoadNotes(CurrentNote);
        _quickNotesOverlay.Show();
        _quickNotesOverlay.Activate();
    }

    #endregion

    public void Dispose()
    {
        _sessionTimer.Stop();
        _noteDebounceTimer.Stop();
        _soundEngine.Dispose();
    }
}
