using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using WpfApplication = System.Windows.Application;

namespace Blocker.App;

public enum SoundscapeType
{
    None,
    Rain,
    Library,
    Forest,
    SingingBowls
}

/// <summary>
/// Thread-safe ambient audio playback engine for meditation and focus soundscapes.
/// </summary>
public class AmbientSoundEngine : IDisposable, INotifyPropertyChanged
{
    private readonly Dispatcher _dispatcher;
    private MediaPlayer _mediaPlayer = null!;
    private double _volume = 0.7;
    private double _fadeTargetVolume = 0.7;
    private CancellationTokenSource? _fadeCts;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SoundscapeType CurrentSoundscape { get; private set; } = SoundscapeType.None;

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
            _fadeTargetVolume = clamped;
            OnPropertyChanged();

            RunOnDispatcher(() =>
            {
                if (IsPlaying && _fadeCts == null)
                {
                    _mediaPlayer.Volume = _volume;
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
    /// Selects and starts playing the specified soundscape with a smooth 1-second fade-in.
    /// Passing <see cref="SoundscapeType.None"/> smoothly stops playback.
    /// </summary>
    public void SetSoundscape(SoundscapeType type)
    {
        RunOnDispatcher(() =>
        {
            if (type == SoundscapeType.None)
            {
                CurrentSoundscape = SoundscapeType.None;
                OnPropertyChanged(nameof(CurrentSoundscape));
                Stop(fadeOut: true);
                return;
            }

            if (type == CurrentSoundscape && IsPlaying)
            {
                return;
            }

            string fileName = type switch
            {
                SoundscapeType.Rain => "rain.wav",
                SoundscapeType.Library => "library.wav",
                SoundscapeType.Forest => "forest.wav",
                SoundscapeType.SingingBowls => "bowls.wav",
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };

            string filePath = ResolveAudioFilePath(fileName);
            if (!File.Exists(filePath))
            {
                System.Diagnostics.Trace.WriteLine($"[AmbientSoundEngine] Ambient audio file not found: {filePath}");
                CancelFade();
                CurrentSoundscape = SoundscapeType.None;
                IsPlaying = false;
                OnPropertyChanged(nameof(CurrentSoundscape));
                OnPropertyChanged(nameof(IsPlaying));
                return;
            }

            try
            {
                CancelFade();
                CurrentSoundscape = type;
                OnPropertyChanged(nameof(CurrentSoundscape));

                _mediaPlayer.Volume = 0.0;
                _mediaPlayer.Open(new Uri(filePath, UriKind.Absolute));
                _mediaPlayer.Position = TimeSpan.Zero;
                _mediaPlayer.Play();
                IsPlaying = true;
                OnPropertyChanged(nameof(IsPlaying));

                StartFade(Volume, TimeSpan.FromSeconds(1.0));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[AmbientSoundEngine] Error playing soundscape '{filePath}': {ex.Message}");
                CancelFade();
                CurrentSoundscape = SoundscapeType.None;
                IsPlaying = false;
                OnPropertyChanged(nameof(CurrentSoundscape));
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
            if (CurrentSoundscape == SoundscapeType.None)
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
                _mediaPlayer.Play();
                IsPlaying = true;
                OnPropertyChanged(nameof(IsPlaying));

                StartFade(Volume, TimeSpan.FromSeconds(1.0));
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
                _mediaPlayer.Pause();
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
                    _mediaPlayer.Stop();
                    _mediaPlayer.Position = TimeSpan.Zero;
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
                            try
                            {
                                _mediaPlayer.Stop();
                                _mediaPlayer.Position = TimeSpan.Zero;
                                _mediaPlayer.Volume = Volume;
                            }
                            catch { }
                        });
                    });
                }
                else
                {
                    CancelFade();
                    _mediaPlayer.Stop();
                    _mediaPlayer.Position = TimeSpan.Zero;
                    _mediaPlayer.Volume = Volume;
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

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        RunOnDispatcher(() =>
        {
            if (IsPlaying && CurrentSoundscape != SoundscapeType.None)
            {
                _mediaPlayer.Position = TimeSpan.Zero;
                _mediaPlayer.Play();
            }
        });
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        RunOnDispatcher(() =>
        {
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
                    _mediaPlayer.Stop();
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
