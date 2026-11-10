using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Blocker.Core;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace Blocker.App;

public class SoundscapeItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _displayName = string.Empty;

    public string Id { get; set; } = string.Empty;

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName != value)
            {
                _displayName = value;
                OnPropertyChanged();
            }
        }
    }

    public string IconGlyph { get; set; } = "music";
    public bool IsBuiltIn { get; set; }
    public double VolumeTrim { get; set; } = 1.0;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public partial class MeditationViewModel
{
    private string _selectedSoundscapeId = SoundscapeCatalog.RainId;
    private SoundscapeType _selectedSoundscape = SoundscapeType.Rain;
    private bool _isSoundscapeMenuOpen = false;
    private double _soundscapeVolume = 60.0;

    public ObservableCollection<SoundscapeItemViewModel> SoundscapeTracks { get; } = new();
    public ObservableCollection<SoundscapeItemViewModel> BuiltInTracks { get; } = new();
    public ObservableCollection<SoundscapeItemViewModel> CustomTracks { get; } = new();

    public bool HasCustomTracks => CustomTracks.Count > 0;
    public int CustomTracksCount => CustomTracks.Count;

    public ICommand ToggleSoundscapeCommand { get; private set; } = null!;
    public ICommand SelectSoundscapeCommand { get; private set; } = null!;
    public ICommand ToggleSoundscapeMenuCommand { get; private set; } = null!;
    public ICommand ImportAudioCommand { get; private set; } = null!;
    public ICommand ImportPlaylistCommand { get; private set; } = null!;
    public ICommand OpenAudioFolderCommand { get; private set; } = null!;
    public ICommand DeleteTrackCommand { get; private set; } = null!;
    public ICommand RefreshSoundscapeCatalogCommand { get; private set; } = null!;

    public string SelectedSoundscapeId
    {
        get => _selectedSoundscapeId;
        set
        {
            string normalized = SoundscapeCatalog.NormalizeId(value);
            if (_selectedSoundscapeId != normalized)
            {
                _selectedSoundscapeId = normalized;
                _selectedSoundscape = SoundscapeCatalog.ToLegacyEnum(normalized);

                OnPropertyChanged(nameof(SelectedSoundscapeId));
                OnPropertyChanged(nameof(SelectedSoundscape));
                OnPropertyChanged(nameof(SelectedSoundscapeName));
                OnPropertyChanged(nameof(SelectedSoundscapeDisplayName));
                OnPropertyChanged(nameof(IsRainSelected));
                OnPropertyChanged(nameof(IsLibrarySelected));
                OnPropertyChanged(nameof(IsForestSelected));
                OnPropertyChanged(nameof(IsBowlsSelected));
                OnPropertyChanged(nameof(IsMuteSelected));
                OnPropertyChanged(nameof(IsSoundscapePlaying));

                UpdateTracksSelection();
            }
        }
    }

    public SoundscapeType SelectedSoundscape
    {
        get => SoundscapeCatalog.ToLegacyEnum(_selectedSoundscapeId);
        set
        {
            if (value == SoundscapeType.Custom)
            {
                if (!_selectedSoundscapeId.StartsWith(SoundscapeCatalog.CustomPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var uiSettings = UiSettingsStore.Load();
                    if (!string.IsNullOrEmpty(uiSettings.MeditationSoundscape) &&
                        uiSettings.MeditationSoundscape.StartsWith(SoundscapeCatalog.CustomPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectedSoundscapeId = uiSettings.MeditationSoundscape;
                    }
                    else if (uiSettings.CustomSoundtracks.Count > 0)
                    {
                        SelectedSoundscapeId = uiSettings.CustomSoundtracks[0].Id;
                    }
                }
            }
            else
            {
                SelectedSoundscapeId = SoundscapeCatalog.FromLegacyEnum(value);
            }
        }
    }

    public string SelectedSoundscapeName
    {
        get
        {
            var uiSettings = UiSettingsStore.Load();
            var track = SoundscapeCatalog.FindTrack(_selectedSoundscapeId, uiSettings);
            return track.DisplayName;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string normalized = SoundscapeCatalog.NormalizeId(value);
            SelectedSoundscapeId = normalized;
        }
    }

    /// <summary>
    /// Compact display name for UI labels (e.g. ZenMiniHudWindow audio label).
    /// Truncates long custom soundtrack titles to fit cleanly without layout clipping.
    /// </summary>
    public string SelectedSoundscapeDisplayName
    {
        get
        {
            string name = SelectedSoundscapeName;
            if (string.IsNullOrWhiteSpace(name)) return "Mute";
            const int maxLen = 16;
            if (name.Length > maxLen)
            {
                return name[..(maxLen - 3)] + "...";
            }
            return name;
        }
    }

    public bool IsRainSelected => _selectedSoundscapeId == SoundscapeCatalog.RainId;
    public bool IsLibrarySelected => _selectedSoundscapeId == SoundscapeCatalog.LibraryId;
    public bool IsForestSelected => _selectedSoundscapeId == SoundscapeCatalog.ForestId;
    public bool IsBowlsSelected => _selectedSoundscapeId == SoundscapeCatalog.SingingBowlsId;
    public bool IsMuteSelected => _selectedSoundscapeId == SoundscapeCatalog.MuteId;

    public bool IsSoundscapePlaying => _soundEngine.IsPlaying;

    public bool IsSoundscapeMenuOpen
    {
        get => _isSoundscapeMenuOpen;
        set => SetProperty(ref _isSoundscapeMenuOpen, value);
    }

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

    public void RefreshSoundscapeCatalog()
    {
        try
        {
            var uiSettings = UiSettingsStore.Load();
            var allTracks = SoundscapeCatalog.GetAllTracks(uiSettings);

            SoundscapeTracks.Clear();
            BuiltInTracks.Clear();
            CustomTracks.Clear();

            foreach (var t in allTracks)
            {
                var item = new SoundscapeItemViewModel
                {
                    Id = t.Id,
                    DisplayName = t.DisplayName,
                    IconGlyph = t.IconGlyph,
                    IsBuiltIn = t.IsBuiltIn,
                    VolumeTrim = t.VolumeTrim,
                    IsSelected = string.Equals(t.Id, _selectedSoundscapeId, StringComparison.OrdinalIgnoreCase)
                };

                SoundscapeTracks.Add(item);
                if (t.IsBuiltIn)
                {
                    BuiltInTracks.Add(item);
                }
                else
                {
                    CustomTracks.Add(item);
                }
            }

            OnPropertyChanged(nameof(HasCustomTracks));
            OnPropertyChanged(nameof(CustomTracksCount));
            OnPropertyChanged(nameof(SelectedSoundscapeName));
            OnPropertyChanged(nameof(SelectedSoundscapeDisplayName));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[MeditationViewModel] RefreshSoundscapeCatalog error: {ex.Message}");
        }
    }

    private void UpdateTracksSelection()
    {
        foreach (var track in SoundscapeTracks)
        {
            track.IsSelected = string.Equals(track.Id, _selectedSoundscapeId, StringComparison.OrdinalIgnoreCase);
        }
    }

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
                if (string.Equals(_selectedSoundscapeId, SoundscapeCatalog.MuteId, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedSoundscapeId = SoundscapeCatalog.RainId;
                }
                _soundEngine.SetSoundscape(_selectedSoundscapeId);
                _soundEngine.Play();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[MeditationViewModel] ToggleSoundscape failed: {ex.Message}");
        }
        OnPropertyChanged(nameof(IsSoundscapePlaying));
    }

    public void SelectTrack(string soundId)
    {
        string normalized = SoundscapeCatalog.NormalizeId(soundId);
        SelectedSoundscapeId = normalized;
        _soundEngine.SetSoundscape(normalized);

        try
        {
            var uiSettings = UiSettingsStore.Load();
            uiSettings.MeditationSoundscape = normalized;
            UiSettingsStore.Save(uiSettings);
        }
        catch { }

        OnPropertyChanged(nameof(IsSoundscapePlaying));
    }

    private static async Task<(bool Decodable, double? Duration, string? Error)> ProbeAudioAsync(string filePath, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<(bool Decodable, double? Duration, string? Error)>();
        var player = new MediaPlayer();

        void Cleanup()
        {
            try
            {
                player.MediaOpened -= OnOpened;
                player.MediaFailed -= OnFailed;
                player.Close();
            }
            catch { }
        }

        void OnOpened(object? sender, EventArgs e)
        {
            double? dur = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : null;
            Cleanup();
            tcs.TrySetResult((true, dur, null));
        }

        void OnFailed(object? sender, ExceptionEventArgs e)
        {
            string msg = e.ErrorException?.Message ?? "Decoder failed";
            Cleanup();
            tcs.TrySetResult((false, null, msg));
        }

        player.MediaOpened += OnOpened;
        player.MediaFailed += OnFailed;

        try
        {
            player.Open(new Uri(filePath, UriKind.Absolute));
        }
        catch (Exception ex)
        {
            Cleanup();
            return (false, null, ex.Message);
        }

        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() =>
        {
            Cleanup();
            tcs.TrySetResult((false, null, "Decoder probe timed out."));
        });

        return await tcs.Task;
    }

    public async Task ImportAudioActionAsync()
    {
        if (IsActive)
        {
            MessageBox.Show(
                "Audio management is disabled while a meditation session is active.",
                "Sankalpa",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Audio Tracks",
            Filter = "Audio and Playlists (*.mp3;*.wav;*.m4a;*.wma;*.aac;*.flac;*.m3u;*.m3u8)|*.mp3;*.wav;*.m4a;*.wma;*.aac;*.flac;*.m3u;*.m3u8|Audio Files (*.mp3;*.wav;*.m4a;*.wma;*.aac;*.flac)|*.mp3;*.wav;*.m4a;*.wma;*.aac;*.flac|Playlists (*.m3u;*.m3u8)|*.m3u;*.m3u8|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (ofd.ShowDialog() != true) return;

        var filesToImport = new List<string>();
        var playlistWarnings = new List<string>();

        foreach (var file in ofd.FileNames)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".m3u" || ext == ".m3u8")
            {
                if (AudioImportService.TryParsePlaylist(file, out var playlistFiles, out var warnings, out var err))
                {
                    filesToImport.AddRange(playlistFiles);
                    playlistWarnings.AddRange(warnings);
                }
                else if (!string.IsNullOrEmpty(err))
                {
                    playlistWarnings.Add($"{Path.GetFileName(file)}: {err}");
                }
            }
            else
            {
                filesToImport.Add(file);
            }
        }

        if (filesToImport.Count == 0)
        {
            string msg = playlistWarnings.Count > 0
                ? string.Join("\n", playlistWarnings)
                : "No valid audio files selected.";
            MessageBox.Show(msg, "Import Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var uiSettings = UiSettingsStore.Load();
        var result = await AudioImportService.ImportAudioFilesAsync(filesToImport, uiSettings, null,
            async path => await ProbeAudioAsync(path, TimeSpan.FromSeconds(4)));

        RefreshSoundscapeCatalog();

        if (result.ImportedTracks.Count > 0)
        {
            var latest = result.ImportedTracks[^1];
            SelectTrack(latest.Id);
        }

        if (result.FailureCount > 0 || result.DuplicateCount > 0 || playlistWarnings.Count > 0)
        {
            string summary = result.GetSummaryMessage();
            if (playlistWarnings.Count > 0)
            {
                summary += "\n\nPlaylist details:\n" + string.Join("\n", playlistWarnings.Take(5));
            }
            MessageBox.Show(summary, "Audio Import", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public async Task ImportPlaylistActionAsync()
    {
        if (IsActive)
        {
            MessageBox.Show(
                "Audio management is disabled while a meditation session is active.",
                "Sankalpa",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Playlist (.m3u, .m3u8)",
            Filter = "Playlists (*.m3u;*.m3u8)|*.m3u;*.m3u8|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (ofd.ShowDialog() != true) return;

        if (!AudioImportService.TryParsePlaylist(ofd.FileName, out var resolvedFiles, out var warnings, out var err))
        {
            MessageBox.Show(err ?? "Failed to parse playlist.", "Import Playlist", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var uiSettings = UiSettingsStore.Load();
        var result = await AudioImportService.ImportAudioFilesAsync(resolvedFiles, uiSettings, null,
            async path => await ProbeAudioAsync(path, TimeSpan.FromSeconds(4)));

        RefreshSoundscapeCatalog();

        if (result.ImportedTracks.Count > 0)
        {
            var latest = result.ImportedTracks[^1];
            SelectTrack(latest.Id);
        }

        string summary = result.GetSummaryMessage();
        if (warnings.Count > 0)
        {
            summary += "\n\nWarnings:\n" + string.Join("\n", warnings.Take(5));
        }

        MessageBox.Show(summary, "Playlist Import", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void OpenAudioFolderAction()
    {
        if (IsActive)
        {
            MessageBox.Show(
                "Opening the audio folder is disabled while a meditation session is active.",
                "Sankalpa",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            string dir = SoundscapeCatalog.GetCustomAudioDirectory();
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open audio folder: {ex.Message}", "Sankalpa", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void DeleteTrackAction(string? trackId)
    {
        if (IsActive)
        {
            MessageBox.Show(
                "Audio management is disabled while a meditation session is active.",
                "Sankalpa",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(trackId)) return;

        var uiSettings = UiSettingsStore.Load();
        var meta = uiSettings.CustomSoundtracks.FirstOrDefault(t => string.Equals(t.Id, trackId, StringComparison.OrdinalIgnoreCase));
        if (meta == null) return;

        var confirm = MessageBox.Show(
            $"Are you sure you want to remove '{meta.DisplayName}'?",
            "Remove Custom Audio",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        bool wasSelected = string.Equals(SelectedSoundscapeId, trackId, StringComparison.OrdinalIgnoreCase);

        AudioImportService.DeleteCustomTrack(trackId, uiSettings);

        if (wasSelected)
        {
            SelectTrack(SoundscapeCatalog.RainId);
        }

        RefreshSoundscapeCatalog();
    }

    private void OnSoundscapeCatalogChanged()
    {
        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.InvokeAsync(RefreshSoundscapeCatalog);
        }
        else
        {
            RefreshSoundscapeCatalog();
        }
    }

    public void DisposeAudio()
    {
        SoundscapeCatalog.CatalogChanged -= OnSoundscapeCatalogChanged;
    }

    private void InitializeAudio()
    {
        try
        {
            var uiSettings = UiSettingsStore.Load();
            if (!string.IsNullOrWhiteSpace(uiSettings.MeditationSoundscape))
            {
                _selectedSoundscapeId = SoundscapeCatalog.NormalizeId(uiSettings.MeditationSoundscape);
                _selectedSoundscape = SoundscapeCatalog.ToLegacyEnum(_selectedSoundscapeId);
            }
        }
        catch { }

        RefreshSoundscapeCatalog();
        SoundscapeCatalog.CatalogChanged += OnSoundscapeCatalogChanged;

        ToggleSoundscapeCommand = new RelayCommand(ToggleSoundscape);
        SelectSoundscapeCommand = new RelayCommand<string>(soundName =>
        {
            if (!string.IsNullOrWhiteSpace(soundName))
            {
                SelectTrack(soundName);
            }
        });
        ToggleSoundscapeMenuCommand = new RelayCommand(() => IsSoundscapeMenuOpen = !IsSoundscapeMenuOpen);
        ImportAudioCommand = new RelayCommand(async () => await ImportAudioActionAsync());
        ImportPlaylistCommand = new RelayCommand(async () => await ImportPlaylistActionAsync());
        OpenAudioFolderCommand = new RelayCommand(OpenAudioFolderAction);
        DeleteTrackCommand = new RelayCommand<string>(DeleteTrackAction);
        RefreshSoundscapeCatalogCommand = new RelayCommand(RefreshSoundscapeCatalog);
    }
}
