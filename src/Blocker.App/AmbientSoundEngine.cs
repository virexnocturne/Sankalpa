using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Blocker.Core;
using WpfApplication = System.Windows.Application;

namespace Blocker.App;

/// <summary>
/// Thread-safe ambient audio playback engine for meditation and focus soundscapes.
/// Supports both built-in presets and user-imported custom tracks with per-track trim volume
/// and seamless looping via MediaTimeline.
/// </summary>
public class AmbientSoundEngine : IDisposable, INotifyPropertyChanged
{
    private readonly Dispatcher _dispatcher;
    private MediaPlayer _mediaPlayer = null!;
    private MediaClock? _mediaClock;
    private bool _usingClock;
    private double _volume = 0.7;
    private double _trackTrimVolume = 1.0;
    private double _fadeTargetVolume = 0.7;
    private CancellationTokenSource? _fadeCts;
    private bool _disposed;
    private string _currentSoundscapeId = SoundscapeCatalog.RainId;
    private SoundscapeItem? _currentSoundscapeItem;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SoundscapeType CurrentSoundscape { get; private set; } = SoundscapeType.None;

    public string CurrentSoundscapeId => _currentSoundscapeId;

    public SoundscapeItem? CurrentSoundscapeItem => _currentSoundscapeItem;

    public bool IsPlaying { get; private set; }

    /// <summary>
    /// Master volume for soundscapes, clamped between 0.0 and 1.0. Defaults to 0.7.
    /// </summary>
    public double Volume
    {
        get => _volume;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_volume - clamped) < 0.001) return;

            _volume = clamped;
            _fadeTargetVolume = clamped * _trackTrimVolume;
            OnPropertyChanged();

            RunOnDispatcher(() =>
            {
                if (IsPlaying && _fadeCts == null)
                {
                    _mediaPlayer.Volume = Math.Clamp(_volume * _trackTrimVolume, 0.0, 1.0);
                }
            });
        }
    }

    public AmbientSoundEngine()
    {
        var appDispatcher = WpfApplication.Current?.Dispatcher;
        if (appDispatcher != null && !appDispatcher.CheckAccess())
        {
            _dispatcher = appDispatcher;
            appDispatcher.Invoke(InitializePlayer);
        }
        else
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            InitializePlayer();
        }
    }

    private void InitializePlayer()
    {
        _mediaPlayer = new MediaPlayer();
        _mediaPlayer.MediaEnded += OnMediaEnded;
        _mediaPlayer.MediaFailed += OnMediaFailed;
    }

    /// <summary>
    /// Selects and starts playing the specified soundscape by legacy enum.
    /// Passing <see cref="SoundscapeType.None"/> smoothly stops playback.
    /// </summary>
    public void SetSoundscape(SoundscapeType type)
    {
        if (type == SoundscapeType.Custom)
        {
            var uiSettings = UiSettingsStore.Load();
            SetSoundscape(uiSettings.MeditationSoundscape);
            return;
        }

        string id = SoundscapeCatalog.FromLegacyEnum(type);
        SetSoundscape(id);
    }

    /// <summary>
    /// Selects and starts playing the specified soundscape by ID (built-in ID or "custom:<guid>").
    /// </summary>
    public void SetSoundscape(string? soundscapeId)
    {
        RunOnDispatcher(() =>
        {
            string normalized = SoundscapeCatalog.NormalizeId(soundscapeId);

            if (string.Equals(normalized, SoundscapeCatalog.MuteId, StringComparison.OrdinalIgnoreCase))
            {
                _currentSoundscapeId = SoundscapeCatalog.MuteId;
                CurrentSoundscape = SoundscapeType.None;
                _currentSoundscapeItem = null;
                OnPropertyChanged(nameof(CurrentSoundscape));
                OnPropertyChanged(nameof(CurrentSoundscapeId));
                OnPropertyChanged(nameof(CurrentSoundscapeItem));
                Stop(fadeOut: true);
                return;
            }

            if (string.Equals(_currentSoundscapeId, normalized, StringComparison.OrdinalIgnoreCase) && IsPlaying)
            {
                return;
            }

            var uiSettings = UiSettingsStore.Load();
            var track = SoundscapeCatalog.FindTrack(normalized, uiSettings);

            if (track.Id == SoundscapeCatalog.MuteId)
            {
                _currentSoundscapeId = SoundscapeCatalog.MuteId;
                CurrentSoundscape = SoundscapeType.None;
                _currentSoundscapeItem = null;
                OnPropertyChanged(nameof(CurrentSoundscape));
                OnPropertyChanged(nameof(CurrentSoundscapeId));
                OnPropertyChanged(nameof(CurrentSoundscapeItem));
                Stop(fadeOut: true);
                return;
            }

            string filePath;
            if (track.IsBuiltIn)
            {
                string fileName = track.FilePath ?? "rain.wav";
                filePath = ResolveAudioFilePath(fileName);
            }
            else
            {
                filePath = track.FilePath ?? string.Empty;
            }

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                System.Diagnostics.Trace.WriteLine($"[AmbientSoundEngine] Ambient audio file not found: {filePath}. Falling back to default.");
                CancelFade();
                StopPlayer();
                _currentSoundscapeId = SoundscapeCatalog.MuteId;
                CurrentSoundscape = SoundscapeType.None;
                _currentSoundscapeItem = null;
                IsPlaying = false;
                OnPropertyChanged(nameof(CurrentSoundscape));
                OnPropertyChanged(nameof(CurrentSoundscapeId));
                OnPropertyChanged(nameof(CurrentSoundscapeItem));
                OnPropertyChanged(nameof(IsPlaying));
                return;
            }

            try
            {
                CancelFade();
                StopPlayer();

                _currentSoundscapeId = track.Id;
                CurrentSoundscape = SoundscapeCatalog.ToLegacyEnum(track.Id);
                _currentSoundscapeItem = track;
                _trackTrimVolume = Math.Clamp(track.VolumeTrim, 0.0, 1.0);

                OnPropertyChanged(nameof(CurrentSoundscape));
                OnPropertyChanged(nameof(CurrentSoundscapeId));
                OnPropertyChanged(nameof(CurrentSoundscapeItem));

                // Attempt seamless looping using MediaTimeline with RepeatBehavior.Forever
                bool clockStarted = false;
                try
                {
                    var timeline = new MediaTimeline(new Uri(filePath, UriKind.Absolute))
                    {
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    var clock = timeline.CreateClock();
                    _mediaClock = clock;
                    _mediaPlayer.Volume = 0.0;
                    _mediaPlayer.Clock = clock;
                    clock.Controller.Seek(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
                    clock.Controller.Resume();
                    _usingClock = true;
                    clockStarted = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[AmbientSoundEngine] MediaTimeline creation failed ({ex.Message}), falling back to direct MediaPlayer playback.");
                    _mediaClock = null;
                    _mediaPlayer.Clock = null;
                    _usingClock = false;
                    clockStarted = false;
                }

                if (!clockStarted)
                {
                    _mediaPlayer.Volume = 0.0;
                    _mediaPlayer.Open(new Uri(filePath, UriKind.Absolute));
                    _mediaPlayer.Position = TimeSpan.Zero;
                    _mediaPlayer.Play();
                }

                IsPlaying = true;
                OnPropertyChanged(nameof(IsPlaying));

                double targetVol = Math.Clamp(Volume * _trackTrimVolume, 0.0, 1.0);
                StartFade(targetVol, TimeSpan.FromSeconds(1.0));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[AmbientSoundEngine] Error playing soundscape '{filePath}': {ex.Message}");
                CancelFade();
                StopPlayer();
                _currentSoundscapeId = SoundscapeCatalog.MuteId;
                CurrentSoundscape = SoundscapeType.None;
                _currentSoundscapeItem = null;
                IsPlaying = false;
                OnPropertyChanged(nameof(CurrentSoundscape));
                OnPropertyChanged(nameof(CurrentSoundscapeId));
                OnPropertyChanged(nameof(CurrentSoundscapeItem));
                OnPropertyChanged(nameof(IsPlaying));
            }
        });
    }

    /// <summary>
    /// Resumes playback of the current soundscape with a smooth 1-second fade-in.
    /// </summary>
    public void Play()
    {
        RunOnDispatcher(() =>
        {
            if (string.Equals(_currentSoundscapeId, SoundscapeCatalog.MuteId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (IsPlaying)
            {
                return;
            }

            try
            {
                CancelFade();
                _mediaPlayer.Volume = 0.0;

                if (_usingClock && _mediaClock?.Controller != null)
                {
                    _mediaClock.Controller.Resume();
                }
                else
                {
                    _mediaPlayer.Play();
                }

                IsPlaying = true;
                OnPropertyChanged(nameof(IsPlaying));

                double targetVol = Math.Clamp(Volume * _trackTrimVolume, 0.0, 1.0);
                StartFade(targetVol, TimeSpan.FromSeconds(1.0));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[AmbientSoundEngine] Error in Play(): {ex.Message}");
                IsPlaying = false;
                OnPropertyChanged(nameof(IsPlaying));
            }
        });
    }

    /// <summary>
    /// Pauses playback immediately.
    /// </summary>
    public void Pause()
    {
        RunOnDispatcher(() =>
        {
            if (!IsPlaying)
            {
                return;
            }

            try
            {
                CancelFade();
                if (_usingClock && _mediaClock?.Controller != null)
                {
                    _mediaClock.Controller.Pause();
                }
                else
                {
                    _mediaPlayer.Pause();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[AmbientSoundEngine] Error in Pause(): {ex.Message}");
            }
            finally
            {
                IsPlaying = false;
                OnPropertyChanged(nameof(IsPlaying));
            }
        });
    }

    /// <summary>
    /// Stops playback, optionally fading out over 1 second, and resets track position to zero.
    /// </summary>
    public void Stop(bool fadeOut = true)
    {
        RunOnDispatcher(() =>
        {
            try
            {
                if (!IsPlaying)
                {
                    CancelFade();
                    StopPlayer();
                    return;
                }

                if (fadeOut)
                {
                    IsPlaying = false;
                    OnPropertyChanged(nameof(IsPlaying));

                    StartFade(0.0, TimeSpan.FromSeconds(1.0), onCompleted: () =>
                    {
                        RunOnDispatcher(() =>
                        {
                            StopPlayer();
                        });
                    });
                }
                else
                {
                    CancelFade();
                    StopPlayer();
                    IsPlaying = false;
                    OnPropertyChanged(nameof(IsPlaying));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[AmbientSoundEngine] Error in Stop(): {ex.Message}");
                IsPlaying = false;
                OnPropertyChanged(nameof(IsPlaying));
            }
        });
    }

    private void StopPlayer()
    {
        try
        {
            if (_usingClock && _mediaClock?.Controller != null)
            {
                _mediaClock.Controller.Stop();
                _mediaPlayer.Clock = null;
                _mediaClock = null;
                _usingClock = false;
            }
            else
            {
                _mediaPlayer.Stop();
                _mediaPlayer.Position = TimeSpan.Zero;
            }
            _mediaPlayer.Volume = Math.Clamp(Volume * _trackTrimVolume, 0.0, 1.0);
        }
        catch
        {
            // Best-effort stop
        }
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        RunOnDispatcher(() =>
        {
            // In fallback mode (!usingClock), loop manually on MediaEnded
            if (!_usingClock && IsPlaying && !string.Equals(_currentSoundscapeId, SoundscapeCatalog.MuteId, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _mediaPlayer.Position = TimeSpan.Zero;
                    _mediaPlayer.Play();
                }
                catch { }
            }
        });
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        RunOnDispatcher(() =>
        {
            System.Diagnostics.Trace.WriteLine($"[AmbientSoundEngine] MediaFailed: {e.ErrorException?.Message}");
            IsPlaying = false;
            OnPropertyChanged(nameof(IsPlaying));
        });
    }

    private void StartFade(double targetVol, TimeSpan duration, Action? onCompleted = null)
    {
        CancelFade();
        _fadeCts = new CancellationTokenSource();
        var ct = _fadeCts.Token;
        _fadeTargetVolume = targetVol;

        double startVol = _mediaPlayer.Volume;
        var startTime = DateTime.UtcNow;
        double durationMs = duration.TotalMilliseconds;

        Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) return;

                    double elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    double fraction = Math.Clamp(elapsedMs / durationMs, 0.0, 1.0);
                    double currentTarget = _fadeTargetVolume;
                    double currentVol = startVol + (currentTarget - startVol) * fraction;

                    RunOnDispatcher(() =>
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            _mediaPlayer.Volume = Math.Clamp(currentVol, 0.0, 1.0);
                        }
                    });

                    if (fraction >= 1.0)
                    {
                        break;
                    }
                }

                if (!ct.IsCancellationRequested)
                {
                    RunOnDispatcher(() =>
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            _mediaPlayer.Volume = Math.Clamp(_fadeTargetVolume, 0.0, 1.0);
                            _fadeCts?.Dispose();
                            _fadeCts = null;
                            onCompleted?.Invoke();
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Fade cancelled intentionally
            }
        });
    }

    private void CancelFade()
    {
        if (_fadeCts != null)
        {
            try
            {
                _fadeCts.Cancel();
                _fadeCts.Dispose();
            }
            catch
            {
                // Ignored
            }
            finally
            {
                _fadeCts = null;
            }
        }
    }

    private void RunOnDispatcher(Action action)
    {
        if (_disposed) return;

        var dispatcher = _dispatcher ?? WpfApplication.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.Invoke(action);
            }
        }
        else
        {
            action();
        }
    }

    private static string ResolveAudioFilePath(string fileName)
    {
        // 1. AppContext.BaseDirectory / Assets / Audio / fileName
        string p1 = Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", fileName);
        if (File.Exists(p1)) return p1;

        // 2. AppDomain.CurrentDomain.BaseDirectory / Assets / Audio / fileName
        string p2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Audio", fileName);
        if (File.Exists(p2)) return p2;

        // 3. Current working directory fallback
        string p3 = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Audio", fileName);
        if (File.Exists(p3)) return p3;

        // 4. Source tree fallback for test / development runner
        string p4 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "Audio", fileName));
        if (File.Exists(p4)) return p4;

        return p1;
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            CancelFade();
            RunOnDispatcher(() =>
            {
                try
                {
                    _mediaPlayer.MediaEnded -= OnMediaEnded;
                    _mediaPlayer.MediaFailed -= OnMediaFailed;
                    StopPlayer();
                    _mediaPlayer.Close();
                }
                catch
                {
                    // Ignored on disposal
                }
            });
        }
    }
}
