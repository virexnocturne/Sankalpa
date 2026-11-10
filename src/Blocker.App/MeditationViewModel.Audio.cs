using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Blocker.Core;

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

    public ICommand ToggleSoundscapeCommand { get; private set; } = null!;
    public ICommand SelectSoundscapeCommand { get; private set; } = null!;
    public ICommand ToggleSoundscapeMenuCommand { get; private set; } = null!;

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
            foreach (var t in allTracks)
            {
                SoundscapeTracks.Add(new SoundscapeItemViewModel
                {
                    Id = t.Id,
                    DisplayName = t.DisplayName,
                    IconGlyph = t.IconGlyph,
                    IsBuiltIn = t.IsBuiltIn,
                    VolumeTrim = t.VolumeTrim,
                    IsSelected = string.Equals(t.Id, _selectedSoundscapeId, StringComparison.OrdinalIgnoreCase)
                });
            }
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

        ToggleSoundscapeCommand = new RelayCommand(ToggleSoundscape);
        SelectSoundscapeCommand = new RelayCommand<string>(soundName =>
        {
            if (!string.IsNullOrWhiteSpace(soundName))
            {
                SelectTrack(soundName);
            }
        });
        ToggleSoundscapeMenuCommand = new RelayCommand(() => IsSoundscapeMenuOpen = !IsSoundscapeMenuOpen);
    }
}
