using System;
using System.Windows.Input;
using Blocker.Core;

namespace Blocker.App;

public partial class MeditationViewModel
{
    private SoundscapeType _selectedSoundscape = SoundscapeType.Rain;
    private bool _isSoundscapeMenuOpen = false;
    private double _soundscapeVolume = 60.0;

    public ICommand ToggleSoundscapeCommand { get; private set; } = null!;
    public ICommand SelectSoundscapeCommand { get; private set; } = null!;
    public ICommand ToggleSoundscapeMenuCommand { get; private set; } = null!;

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

    private void InitializeAudio()
    {
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
        ToggleSoundscapeMenuCommand = new RelayCommand(() => IsSoundscapeMenuOpen = !IsSoundscapeMenuOpen);
    }
}
