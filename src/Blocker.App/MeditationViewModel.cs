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
    private readonly DispatcherTimer _transientSaveTimer;

    private long _targetDurationSeconds = 1200; // 20m default
    private long _remainingSeconds = 1200;
    private bool _isActive;
    private LockMode _lockMode = LockMode.Locked;
    private SoundscapeType _selectedSoundscape = SoundscapeType.Rain;
    private string? _passwordHash;
    private string _currentNote = string.Empty;
    private string _autoSaveStatus = string.Empty;
    private int? _activeSessionId;
    private ZenShieldWindow? _activeZenShield;

    public ZenShieldWindow? ActiveZenShield => _activeZenShield;
    public IPipeClient? PipeClient => _pipeClient;
    public Func<IEnumerable<string>>? GetBlockedAppsProvider { get; set; }

    public string? PasswordHash
    {
        get => _passwordHash;
        set => SetProperty(ref _passwordHash, value);
    }

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

        _transientSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _transientSaveTimer.Tick += (s, e) =>
        {
            _transientSaveTimer.Stop();
            AutoSaveStatus = string.Empty;
        };

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

        // Load settings from UiSettingsStore (Bug 10)
        try
        {
            var uiSettings = UiSettingsStore.Load();
            int durMins = uiSettings.MeditationDurationMinutes > 0 ? uiSettings.MeditationDurationMinutes : 20;
            _targetDurationSeconds = durMins * 60;
            _remainingSeconds = _targetDurationSeconds;

            if (Enum.TryParse<LockMode>(uiSettings.MeditationLockMode, true, out var parsedLock))
            {
                _lockMode = parsedLock;
            }
            if (Enum.TryParse<SoundscapeType>(uiSettings.MeditationSoundscape, true, out var parsedSound))
            {
                _selectedSoundscape = parsedSound;
            }
            if (!string.IsNullOrEmpty(uiSettings.MeditationPasswordHash))
            {
                _passwordHash = uiSettings.MeditationPasswordHash;
            }
        }
        catch { }

        UpdateTimerDisplay();

        if (_pipeClient != null)
        {
            _ = Task.Run(ConnectToServiceAsync);
        }
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
                OnPropertyChanged(nameof(SelectedSoundscapeDisplayName));
                OnPropertyChanged(nameof(IsRainSelected));
                OnPropertyChanged(nameof(IsLibrarySelected));
                OnPropertyChanged(nameof(IsForestSelected));
                OnPropertyChanged(nameof(IsBowlsSelected));
                OnPropertyChanged(nameof(IsMuteSelected));
                OnPropertyChanged(nameof(IsSoundscapePlaying));
            }
        }
    }

    public string SelectedSoundscapeName
    {
        get => _selectedSoundscape switch
        {
            SoundscapeType.Rain => "Rain",
            SoundscapeType.Library => "Library",
            SoundscapeType.Forest => "Forest",
            SoundscapeType.SingingBowls => "Singing Bowls",
            _ => "Mute"
        };
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string cleaned = value.Replace(" ", "");
            if (Enum.TryParse<SoundscapeType>(cleaned, true, out var parsed))
            {
                SelectedSoundscape = parsed;
            }
        }
    }

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
                var cards = NoteCardCollection.Deserialize(value);
                if (!AreCardListsEquivalent(NotesCollection, cards))
                {
                    NotesCollection.Clear();
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

    private static bool AreCardListsEquivalent(IList<NoteCard> a, IList<NoteCard> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id ||
                a[i].Title != b[i].Title ||
                a[i].MarkdownContent != b[i].MarkdownContent)
            {
                return false;
            }
        }
        return true;
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

        if (!UiSettingsStore.Instance.SuppressMeditationStartWarning)
        {
            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                bool confirmed = false;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    confirmed = ShowMeditationStartWarning();
                });
                if (!confirmed) return;
            }
            else
            {
                if (!ShowMeditationStartWarning()) return;
            }
        }

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
                this,
                _activeSessionId)
            {
                NotesText = CurrentNote,
                Tag = PasswordHash,
                SessionId = _activeSessionId ?? 0
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

            _activeZenShield.OnVerifyUnlockRequested = () =>
            {
                if (LockMode == LockMode.Password)
                {
                    var dlg = new PasswordUnlockDialog("Meditation Session")
                    {
                        Owner = _activeZenShield,
                        ValidatePassword = VerifyPassword
                    };
                    dlg.Closing += (s, ev) =>
                    {
                        if (dlg.DialogResult == true)
                        {
                            if (!VerifyPassword(dlg.ResultPassword))
                            {
                                ev.Cancel = true;
                                dlg.SetError("Incorrect password. Access denied.");
                            }
                        }
                    };
                    return dlg.ShowDialog() == true && VerifyPassword(dlg.ResultPassword);
                }
                else if (LockMode == LockMode.TypingChallenge)
                {
                    var challenge = "I choose to return to my desktop with intention and focus.";
                    var dlg = new TypingChallengeDialog(challenge)
                    {
                        Owner = _activeZenShield
                    };
                    return dlg.ShowDialog() == true;
                }
                return true;
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
        var allowedAppsSnapshot = AllowedApps.ToList();
        string soundscapeName = SelectedSoundscapeName;
        _ = Task.Run(async () =>
        {
            if (_pipeClient != null)
            {
                try
                {
                    var payload = JsonSerializer.Serialize(new StartMeditationRequest
                    {
                        TargetDurationSeconds = _targetDurationSeconds,
                        AllowedApps = allowedAppsSnapshot,
                        LockMode = LockMode.ToString(),
                        Soundscape = soundscapeName
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
                        if (_activeZenShield != null)
                        {
                            _activeZenShield.SessionId = id;
                        }
                    }
                }
                catch
                {
                    // Fallback locally
                }
            }
        });
    }

    private bool ShowMeditationStartWarning()
    {
        try
        {
            int durationMinutes = (int)Math.Max(1, _targetDurationSeconds / 60);
            string lockRigor = MeditationStartWarningDialog.FormatLockRigor(_lockMode);
            var owner = Application.Current?.MainWindow;
            var dlg = new MeditationStartWarningDialog(durationMinutes, lockRigor, AllowedApps, owner);
            if (dlg.ShowDialog() != true)
            {
                return false;
            }

            if (dlg.DoNotShowAgain)
            {
                UiSettingsStore.Instance.SuppressMeditationStartWarning = true;
                UiSettingsStore.Instance.Save();
            }

            return true;
        }
        catch
        {
            return true;
        }
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
                Owner = Application.Current?.MainWindow,
                ValidatePassword = VerifyPassword
            };
            dlg.Closing += (s, ev) =>
            {
                if (dlg.DialogResult == true)
                {
                    if (!VerifyPassword(dlg.ResultPassword))
                    {
                        ev.Cancel = true;
                        dlg.SetError("Incorrect password. Access denied.");
                    }
                }
            };
            if (dlg.ShowDialog() != true || !VerifyPassword(dlg.ResultPassword))
            {
                return;
            }
        }

        StopMeditationInternal();
    }

    public bool VerifyPassword(string input)
    {
        if (string.IsNullOrEmpty(_passwordHash)) return true;
        if (string.IsNullOrEmpty(input)) return false;

        string inputHash = ComputeSha256(input);
        if (string.Equals(inputHash, _passwordHash, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_passwordHash.Contains(':'))
        {
            var parts = _passwordHash.Split(':');
            if (parts.Length == 2 && PasswordSecurity.VerifyPassword(input, parts[0], parts[1]))
            {
                return true;
            }
        }

        return false;
    }

    public static string ComputeSha256(string rawData)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void StopMeditationInternal()
    {
        IsNotesDrawerOpen = false;
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
        try
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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[MeditationViewModel] ToggleSoundscape failed: {ex.Message}");
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

        // If _quickNotesOverlay is open or exists, sync it immediately:
        _quickNotesOverlay?.LoadNotes(_currentNote);

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

    #endregion

    #region Dialogs

    public void OpenEditDialog()
    {
        var blockedApps = GetBlockedAppsProvider?.Invoke();
        var dlg = new MeditationEditDialog(
            (int)(TargetDurationSeconds / 60),
            LockMode,
            AllowedApps,
            IsScheduleEnabled,
            ScheduleTime,
            ScheduleDays,
            SelectedSoundscape,
            passwordHash: PasswordHash,
            blockedApps: blockedApps)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dlg.ShowDialog() == true)
        {
            TargetDurationSeconds = dlg.DurationMinutes * 60;
            LockMode = dlg.LockProtection;
            PasswordHash = dlg.PasswordHash;

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

            // Persist settings to UiSettingsStore (Bug 10)
            try
            {
                var uiSettings = UiSettingsStore.Load();
                uiSettings.MeditationDurationMinutes = dlg.DurationMinutes;
                uiSettings.MeditationLockMode = dlg.LockProtection.ToString();
                uiSettings.MeditationSoundscape = dlg.SelectedSoundscape.ToString();
                if (dlg.LockProtection == LockMode.Password && !string.IsNullOrEmpty(dlg.PasswordHash))
                {
                    uiSettings.MeditationPasswordHash = dlg.PasswordHash;
                }
                UiSettingsStore.Save(uiSettings);
            }
            catch { }

            // Sync schedule to Service unconditionally
            if (_pipeClient != null)
            {
                var schedAppsSnapshot = AllowedApps.ToList();
                var schedDaysSnapshot = ScheduleDays.Select(d => d.ToString()).ToList();
                string schedSoundscape = SelectedSoundscapeName;
                bool isEnabled = dlg.ScheduleEnabled;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var payload = JsonSerializer.Serialize(new MeditationScheduleDto
                        {
                            Id = 1,
                            Enabled = isEnabled,
                            TimeOfDay = ScheduleTime,
                            DaysOfWeek = schedDaysSnapshot,
                            DurationSeconds = TargetDurationSeconds,
                            AllowedApps = schedAppsSnapshot,
                            LockMode = LockMode.ToString(),
                            Soundscape = schedSoundscape
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

    public async Task ConnectToServiceAsync()
    {
        if (_pipeClient == null) return;

        try
        {
            var res = await _pipeClient.SendRequestAsync(new Request { Command = CommandNames.GetMeditationSchedules });
            if (res.Connected && res.Response?.Ok == true && !string.IsNullOrWhiteSpace(res.Response.Data))
            {
                var schedules = JsonSerializer.Deserialize<List<MeditationScheduleDto>>(res.Response.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var schedule = schedules?.FirstOrDefault();
                if (schedule != null)
                {
                    IsScheduleEnabled = schedule.Enabled;
                    if (!string.IsNullOrWhiteSpace(schedule.TimeOfDay))
                    {
                        ScheduleTime = schedule.TimeOfDay;
                    }
                    if (schedule.DaysOfWeek != null && schedule.DaysOfWeek.Count > 0)
                    {
                        var days = new List<DayOfWeek>();
                        foreach (var d in schedule.DaysOfWeek)
                        {
                            if (Enum.TryParse<DayOfWeek>(d, true, out var day))
                            {
                                days.Add(day);
                            }
                        }
                        if (days.Count > 0)
                        {
                            ScheduleDays = days;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(schedule.Soundscape))
                    {
                        SelectedSoundscapeName = schedule.Soundscape;
                    }
                    if (schedule.DurationSeconds > 0)
                    {
                        TargetDurationSeconds = schedule.DurationSeconds;
                    }
                }
            }

            await CheckSessionStatusAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MeditationViewModel] ConnectToServiceAsync error: {ex.Message}");
        }
    }

    public async Task CheckSessionStatusAsync()
    {
        if (_pipeClient == null) return;

        try
        {
            var medStatus = await GetActiveMeditationAsync();
            if (medStatus != null && (_activeZenShield == null || !_activeZenShield.IsLoaded))
            {
                bool isPendingOrActive = string.Equals(medStatus.Status, "Pending", StringComparison.OrdinalIgnoreCase) || medStatus.Active;
                if (isPendingOrActive && medStatus.RemainingSeconds > 0)
                {
                    LockMode lockMode = LockMode.Locked;
                    if (Enum.TryParse<LockMode>(medStatus.LockMode, true, out var parsedMode))
                    {
                        lockMode = parsedMode;
                    }

                    string? pwdHash = PasswordHash;
                    if (string.IsNullOrEmpty(pwdHash))
                    {
                        try { pwdHash = UiSettingsStore.Load().MeditationPasswordHash; } catch { }
                    }

                    _activeSessionId = medStatus.SessionId;
                    LaunchZenShieldFromSession(
                        medStatus.RemainingSeconds,
                        lockMode,
                        medStatus.AllowedApps ?? new List<string>(),
                        pwdHash,
                        medStatus.Soundscape,
                        medStatus.SessionId);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MeditationViewModel] CheckSessionStatusAsync error: {ex.Message}");
        }
    }

    public async Task<MeditationStatusDto?> GetActiveMeditationAsync()
    {
        if (_pipeClient == null) return null;
        try
        {
            var res = await _pipeClient.SendRequestAsync(new Request { Command = CommandNames.GetMeditationStatus });
            if (!res.Connected || res.Response?.Ok != true || string.IsNullOrWhiteSpace(res.Response.Data))
            {
                res = await _pipeClient.SendRequestAsync(new Request { Command = "GetActiveMeditation" });
            }
            if (res.Connected && res.Response?.Ok == true && !string.IsNullOrWhiteSpace(res.Response.Data))
            {
                return JsonSerializer.Deserialize<MeditationStatusDto>(res.Response.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        }
        catch { }
        return null;
    }

    public void LaunchZenShieldFromSession(long remainingSeconds, LockMode lockMode, List<string> allowedApps, string? passwordHash = null, string? soundscape = null, int? sessionId = null)
    {
        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => LaunchZenShieldFromSession(remainingSeconds, lockMode, allowedApps, passwordHash, soundscape, sessionId));
            return;
        }

        if (_activeZenShield != null) return;

        if (sessionId.HasValue)
        {
            _activeSessionId = sessionId.Value;
        }

        IsActive = true;
        RemainingSeconds = remainingSeconds;
        TargetDurationSeconds = remainingSeconds;
        LockMode = lockMode;
        PasswordHash = passwordHash;

        AllowedApps.Clear();
        foreach (var app in allowedApps)
        {
            AllowedApps.Add(app);
        }

        if (!string.IsNullOrEmpty(soundscape))
        {
            SelectedSoundscapeName = soundscape;
        }

        if (SelectedSoundscape != SoundscapeType.None)
        {
            try { _soundEngine.SetSoundscape(SelectedSoundscape); } catch { }
        }

        try
        {
            _activeZenShield = new ZenShieldWindow(
                (int)remainingSeconds,
                lockMode == LockMode.Locked,
                lockMode.ToString(),
                allowedApps,
                "Meditation & Lockdown",
                this,
                _activeSessionId)
            {
                NotesText = CurrentNote,
                Tag = passwordHash,
                SessionId = _activeSessionId ?? 0
            };

            _activeZenShield.OnToggleNotesRequested = ToggleQuickNotes;
            _activeZenShield.OnNotesChanged = text => CurrentNote = text;
            _activeZenShield.OnSessionEnded = () => StopMeditationInternal();
            _activeZenShield.OnVerifyUnlockRequested = () =>
            {
                if (LockMode == LockMode.Password)
                {
                    var dlg = new PasswordUnlockDialog("Meditation Session")
                    {
                        Owner = _activeZenShield,
                        ValidatePassword = VerifyPassword
                    };
                    dlg.Closing += (s, ev) =>
                    {
                        if (dlg.DialogResult == true)
                        {
                            if (!VerifyPassword(dlg.ResultPassword))
                            {
                                ev.Cancel = true;
                                dlg.SetError("Incorrect password. Access denied.");
                            }
                        }
                    };
                    return dlg.ShowDialog() == true && VerifyPassword(dlg.ResultPassword);
                }
                else if (LockMode == LockMode.TypingChallenge)
                {
                    var challenge = "I choose to return to my desktop with intention and focus.";
                    var dlg = new TypingChallengeDialog(challenge)
                    {
                        Owner = _activeZenShield
                    };
                    return dlg.ShowDialog() == true;
                }
                return true;
            };

            _activeZenShield.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MeditationViewModel] Error launching ZenShield from session: {ex.Message}");
        }

        _sessionTimer.Start();
    }

    #endregion

    public void Dispose()
    {
        _sessionTimer.Stop();
        _noteDebounceTimer.Stop();
        _transientSaveTimer.Stop();
        _soundEngine.Dispose();
    }
}
