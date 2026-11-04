using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Blocker.Core;
using Application = System.Windows.Application;

namespace Blocker.App;

public class ActiveBlockChipItem
{
    public string Name { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsManual { get; set; }
    public bool IsLocked { get; set; }
    public string BackgroundColor { get; set; } = "#14532D";
    public string ForegroundColor { get; set; } = "#86EFAC";
}

public class MainViewModel : ViewModelBase
{
    private readonly IPipeClient _pipeClient;
    private readonly DispatcherTimer _timer;
    private readonly NotificationThrottler _throttler = new();
    private long _lastEventId = 0;
    private bool _isPolling;
    private bool _isInitialConnecting = true;
    private bool _initialProfilesLoaded;

    // Service Status
    private bool _isServiceConnected;
    private bool _isServiceElevated = true;
    private bool _hostsBlockingOk = true;
    private HostsState _hostsState = HostsState.Idle;
    private int _hostsDomainCount;
    private string? _hostsLastError;
    private string? _serviceWarningMessage;

    // Session Status
    private bool _isSessionActive;
    private string? _activeSessionProfile;
    private bool _activeSessionLocked;
    private string _activeSessionRemainingFormatted = "00:00";
    private long _activeSessionRemainingSeconds;

    // Status / Messages
    private string? _statusMessage;
    private string? _errorMessage;
    private ExtensionStatusData? _extensionStatus;
    private StatusData? _currentStatusData;

    public ObservableCollection<string> WarningBanners { get; } = new();

    private readonly UiSettings _uiSettings;
    private bool _isSettingsViewActive;
    private string _theme = "black";
    private string _accentColor = "Emerald";
    private string _cardDensity = "Comfortable";
    private string _timeFormat = "24h";

    // Callbacks for opening dialogs and notifications
    public Action<ProfileViewModel?>? RequestOpenEditorDialog { get; set; }
    public Action<ProfileViewModel?, int>? RequestOpenEditorDialogWithTab { get; set; }
    public Action<ProfileViewModel>? RequestOpenStartSessionDialog { get; set; }
    public Action? RequestOpenExtensionDialog { get; set; }
    public Action? RequestOpenAppearanceDialog { get; set; }
    public Action<string, string>? OnAppBlocked { get; set; }
    public Action<string, string, string>? OnAppBlockedWithKind { get; set; }

    public ICommand ToggleDayNightCommand { get; }
    public ICommand OpenAppearanceDialogCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand BackToBlocksCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public ICommand DeleteBlockCommand { get; }
    public ICommand SwitchDesignThemeCommand { get; }
    public bool IsDarkMode => ThemeManager.IsDark;

    public bool IsSettingsViewActive
    {
        get => _isSettingsViewActive;
        set
        {
            if (SetProperty(ref _isSettingsViewActive, value))
            {
                OnPropertyChanged(nameof(IsBlocksViewActive));
            }
        }
    }

    public bool IsBlocksViewActive => !_isSettingsViewActive;

    public string Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                _uiSettings.Theme = value;
                UiSettingsStore.Save(_uiSettings);
                ThemeManager.SetTheme(value, _uiSettings);
                OnPropertyChanged(nameof(IsThemeBlack));
                OnPropertyChanged(nameof(IsThemeWhite));
                OnPropertyChanged(nameof(IsThemeGrey));
                OnPropertyChanged(nameof(IsDarkMode));
            }
        }
    }

    public bool IsThemeBlack
    {
        get => string.Equals(Theme, "black", StringComparison.OrdinalIgnoreCase);
        set { if (value) Theme = "black"; }
    }

    public bool IsThemeWhite
    {
        get => string.Equals(Theme, "white", StringComparison.OrdinalIgnoreCase);
        set { if (value) Theme = "white"; }
    }

    public bool IsThemeGrey
    {
        get => string.Equals(Theme, "grey", StringComparison.OrdinalIgnoreCase);
        set { if (value) Theme = "grey"; }
    }

    public string ThemeMode
    {
        get => IsDarkMode ? "Dark" : "Light";
        set => Theme = string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase) ? "white" : "black";
    }

    public bool IsThemeDark => IsDarkMode;
    public bool IsThemeLight => !IsDarkMode;

    public string AccentColor
    {
        get => _accentColor;
        set
        {
            if (SetProperty(ref _accentColor, value))
            {
                _uiSettings.AccentColor = value;
                UiSettingsStore.Save(_uiSettings);
                if (Enum.TryParse<AccentColorType>(value, true, out var accent))
                {
                    ThemeManager.SetAccentColor(accent, _uiSettings);
                }
                OnPropertyChanged(nameof(IsAccentEmerald));
                OnPropertyChanged(nameof(IsAccentIndigo));
                OnPropertyChanged(nameof(IsAccentViolet));
                OnPropertyChanged(nameof(IsAccentAmber));
                OnPropertyChanged(nameof(IsAccentRose));
                OnPropertyChanged(nameof(IsAccentSlate));
            }
        }
    }

    public string CardDensity
    {
        get => _cardDensity;
        set
        {
            if (SetProperty(ref _cardDensity, value))
            {
                _uiSettings.CardDensity = value;
                UiSettingsStore.Save(_uiSettings);
                foreach (var p in Profiles)
                {
                    p.CardDensity = value;
                }
                OnPropertyChanged(nameof(IsDensityComfortable));
                OnPropertyChanged(nameof(IsDensityCompact));
            }
        }
    }

    public string TimeFormat
    {
        get => _timeFormat;
        set
        {
            if (SetProperty(ref _timeFormat, value))
            {
                _uiSettings.TimeFormat = value;
                UiSettingsStore.Save(_uiSettings);
                foreach (var p in Profiles)
                {
                    p.TimeFormat = value;
                }
                OnPropertyChanged(nameof(IsTimeFormat12h));
                OnPropertyChanged(nameof(IsTimeFormat24h));
                if (_dnsLockedUntilUtc.HasValue && IsDnsLocked)
                {
                    bool is24 = string.Equals(value, "24h", StringComparison.OrdinalIgnoreCase);
                    DnsLockedUntilLocalFormatted = TimeFormatter.Format(_dnsLockedUntilUtc.Value.ToLocalTime(), is24);
                }
                if (_currentStatusData != null)
                {
                    UpdateProfilesReadOnlyState(_currentStatusData);
                }
            }
        }
    }


    public bool IsAccentEmerald
    {
        get => string.Equals(AccentColor, "Emerald", StringComparison.OrdinalIgnoreCase);
        set { if (value) AccentColor = "Emerald"; }
    }

    public bool IsAccentIndigo
    {
        get => string.Equals(AccentColor, "Indigo", StringComparison.OrdinalIgnoreCase);
        set { if (value) AccentColor = "Indigo"; }
    }

    public bool IsAccentViolet
    {
        get => string.Equals(AccentColor, "Violet", StringComparison.OrdinalIgnoreCase);
        set { if (value) AccentColor = "Violet"; }
    }

    public bool IsAccentAmber
    {
        get => string.Equals(AccentColor, "Amber", StringComparison.OrdinalIgnoreCase);
        set { if (value) AccentColor = "Amber"; }
    }

    public bool IsAccentRose
    {
        get => string.Equals(AccentColor, "Rose", StringComparison.OrdinalIgnoreCase);
        set { if (value) AccentColor = "Rose"; }
    }

    public bool IsAccentSlate
    {
        get => string.Equals(AccentColor, "Slate", StringComparison.OrdinalIgnoreCase);
        set { if (value) AccentColor = "Slate"; }
    }

    public bool IsDensityComfortable
    {
        get => !string.Equals(CardDensity, "Compact", StringComparison.OrdinalIgnoreCase);
        set { if (value) CardDensity = "Comfortable"; }
    }

    public bool IsDensityCompact
    {
        get => string.Equals(CardDensity, "Compact", StringComparison.OrdinalIgnoreCase);
        set { if (value) CardDensity = "Compact"; }
    }

    public bool IsTimeFormat12h
    {
        get => string.Equals(TimeFormat, "12h", StringComparison.OrdinalIgnoreCase);
        set { if (value) TimeFormat = "12h"; }
    }

    public bool IsTimeFormat24h
    {
        get => !string.Equals(TimeFormat, "12h", StringComparison.OrdinalIgnoreCase);
        set { if (value) TimeFormat = "24h"; }
    }

    // DNS Protection State
    private bool _isDnsEnabled;
    private string _selectedDnsPreset = "Cloudflare Family";
    private string _primaryDns = "1.1.1.3";
    private string _secondaryDns = "1.0.0.3";
    private bool _isDnsLocked;
    private string _dnsLockType = "Timer";
    private int _dnsLockDurationMinutes = 1440;
    private string? _dnsLockRemainingFormatted;
    private string? _dnsLockedUntilLocalFormatted;
    private string _dnsMessage = string.Empty;
    private string _dnsCustomDurationInput = "1";
    private int _dnsCustomDurationUnitIndex = 2; // Default: Days
    private int _dnsCustomDays = 0;
    private int _dnsCustomHours = 0;
    private int _dnsCustomMinutes = 15;
    private int _dnsCustomSeconds = 0;
    private DateTime? _dnsLockedUntilUtc;
    private int _pollCycleCount = 0;

    public Func<string, string?>? RequestTypingChallenge { get; set; }

    public IReadOnlyList<DnsPreset> DnsPresetList => DnsPresets.All;
    public List<int> DnsTimerDurationList { get; } = new() { 15, 30, 60, 120, 240, 480, 1440 };

    public bool IsDnsEnabled
    {
        get => _isDnsEnabled;
        set => SetProperty(ref _isDnsEnabled, value);
    }

    public string SelectedDnsPreset
    {
        get => _selectedDnsPreset;
        set
        {
            if (SetProperty(ref _selectedDnsPreset, value))
            {
                OnPropertyChanged(nameof(IsCustomDns));
                OnPropertyChanged(nameof(IsPresetCloudflare));
                OnPropertyChanged(nameof(IsPresetCleanBrowsing));
                OnPropertyChanged(nameof(IsPresetAdGuard));
                OnPropertyChanged(nameof(IsPresetQuad9));
                OnPropertyChanged(nameof(IsPresetCustom));
                var preset = DnsPresets.All.FirstOrDefault(p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase));
                if (preset != null && preset.Name != "Custom")
                {
                    PrimaryDns = preset.Primary;
                    SecondaryDns = preset.Secondary;
                }

                if (IsDnsEnabled && CanEditDns && value != "Custom")
                {
                    _ = SaveDnsConfigAsync(true);
                }
            }
        }
    }

    public bool IsPresetCloudflare
    {
        get => string.Equals(_selectedDnsPreset, "Cloudflare Family", StringComparison.OrdinalIgnoreCase);
        set { if (value) SelectedDnsPreset = "Cloudflare Family"; }
    }

    public bool IsPresetCleanBrowsing
    {
        get => string.Equals(_selectedDnsPreset, "CleanBrowsing", StringComparison.OrdinalIgnoreCase);
        set { if (value) SelectedDnsPreset = "CleanBrowsing"; }
    }

    public bool IsPresetAdGuard
    {
        get => string.Equals(_selectedDnsPreset, "AdGuard Family", StringComparison.OrdinalIgnoreCase);
        set { if (value) SelectedDnsPreset = "AdGuard Family"; }
    }

    public bool IsPresetQuad9
    {
        get => string.Equals(_selectedDnsPreset, "Quad9", StringComparison.OrdinalIgnoreCase);
        set { if (value) SelectedDnsPreset = "Quad9"; }
    }

    public bool IsPresetCustom
    {
        get => string.Equals(_selectedDnsPreset, "Custom", StringComparison.OrdinalIgnoreCase);
        set { if (value) SelectedDnsPreset = "Custom"; }
    }

    public bool IsCustomDns => string.Equals(_selectedDnsPreset, "Custom", StringComparison.OrdinalIgnoreCase);

    public string PrimaryDns
    {
        get => _primaryDns;
        set => SetProperty(ref _primaryDns, value);
    }

    public string SecondaryDns
    {
        get => _secondaryDns;
        set => SetProperty(ref _secondaryDns, value);
    }

    public bool IsDnsLocked
    {
        get => _isDnsLocked;
        set
        {
            if (SetProperty(ref _isDnsLocked, value))
            {
                OnPropertyChanged(nameof(CanEditDns));
            }
        }
    }

    public bool CanEditDns => !_isDnsLocked;

    public bool CanUnlockDns => IsDnsLocked && (!IsDnsLockTimer || (_dnsLockedUntilUtc.HasValue && DateTime.UtcNow >= _dnsLockedUntilUtc.Value));


    public string DnsLockType
    {
        get => _dnsLockType;
        set
        {
            if (SetProperty(ref _dnsLockType, value))
            {
                OnPropertyChanged(nameof(IsDnsLockChallenge));
                OnPropertyChanged(nameof(IsDnsLockTimer));
            }
        }
    }

    public bool IsDnsLockChallenge
    {
        get => string.Equals(_dnsLockType, "Challenge", StringComparison.OrdinalIgnoreCase);
        set { if (value) DnsLockType = "Challenge"; }
    }

    public bool IsDnsLockTimer
    {
        get => string.Equals(_dnsLockType, "Timer", StringComparison.OrdinalIgnoreCase);
        set { if (value) DnsLockType = "Timer"; }
    }

    public int DnsLockDurationMinutes
    {
        get => _dnsLockDurationMinutes;
        set
        {
            if (SetProperty(ref _dnsLockDurationMinutes, value))
            {
                OnPropertyChanged(nameof(IsDnsDuration15m));
                OnPropertyChanged(nameof(IsDnsDuration30m));
                OnPropertyChanged(nameof(IsDnsDuration1h));
                OnPropertyChanged(nameof(IsDnsDuration2h));
                OnPropertyChanged(nameof(IsDnsDuration4h));
                OnPropertyChanged(nameof(IsDnsDuration8h));
                OnPropertyChanged(nameof(IsDnsDuration24h));
            }
        }
    }

    public bool IsDnsDuration15m
    {
        get => DnsLockDurationMinutes == 15;
        set { if (value) DnsLockDurationMinutes = 15; }
    }
    public bool IsDnsDuration30m
    {
        get => DnsLockDurationMinutes == 30;
        set { if (value) DnsLockDurationMinutes = 30; }
    }
    public bool IsDnsDuration1h
    {
        get => DnsLockDurationMinutes == 60;
        set { if (value) DnsLockDurationMinutes = 60; }
    }
    public bool IsDnsDuration2h
    {
        get => DnsLockDurationMinutes == 120;
        set { if (value) DnsLockDurationMinutes = 120; }
    }
    public bool IsDnsDuration4h
    {
        get => DnsLockDurationMinutes == 240;
        set { if (value) DnsLockDurationMinutes = 240; }
    }
    public bool IsDnsDuration8h
    {
        get => DnsLockDurationMinutes == 480;
        set { if (value) DnsLockDurationMinutes = 480; }
    }
    public bool IsDnsDuration24h
    {
        get => DnsLockDurationMinutes == 1440;
        set { if (value) DnsLockDurationMinutes = 1440; }
    }

    public string? DnsLockRemainingFormatted
    {
        get => _dnsLockRemainingFormatted;
        set => SetProperty(ref _dnsLockRemainingFormatted, value);
    }

    public string? DnsLockedUntilLocalFormatted
    {
        get => _dnsLockedUntilLocalFormatted;
        set => SetProperty(ref _dnsLockedUntilLocalFormatted, value);
    }

    public string DnsCustomDurationInput
    {
        get => _dnsCustomDurationInput;
        set
        {
            if (SetProperty(ref _dnsCustomDurationInput, value))
            {
                RecalculateDnsLockDuration();
            }
        }
    }

    public int DnsCustomDurationUnitIndex
    {
        get => _dnsCustomDurationUnitIndex;
        set
        {
            if (SetProperty(ref _dnsCustomDurationUnitIndex, value))
            {
                RecalculateDnsLockDuration();
            }
        }
    }

    public int DnsCustomDays
    {
        get => _dnsCustomDays;
        set
        {
            if (SetProperty(ref _dnsCustomDays, value))
            {
                OnPropertyChanged(nameof(DnsCustomDurationSummary));
            }
        }
    }

    public int DnsCustomHours
    {
        get => _dnsCustomHours;
        set
        {
            if (SetProperty(ref _dnsCustomHours, value))
            {
                OnPropertyChanged(nameof(DnsCustomDurationSummary));
            }
        }
    }

    public int DnsCustomMinutes
    {
        get => _dnsCustomMinutes;
        set
        {
            if (SetProperty(ref _dnsCustomMinutes, value))
            {
                OnPropertyChanged(nameof(DnsCustomDurationSummary));
            }
        }
    }

    public int DnsCustomSeconds
    {
        get => _dnsCustomSeconds;
        set
        {
            if (SetProperty(ref _dnsCustomSeconds, value))
            {
                OnPropertyChanged(nameof(DnsCustomDurationSummary));
            }
        }
    }

    public string DnsCustomDurationSummary
    {
        get
        {
            int d = Math.Max(0, DnsCustomDays);
            int h = Math.Max(0, DnsCustomHours);
            int m = Math.Max(0, DnsCustomMinutes);
            int s = Math.Max(0, DnsCustomSeconds);

            if (d > 0)
            {
                return $"{d}d {h}h {m}m {s}s";
            }
            if (h > 0)
            {
                return $"{h}h {m}m {s}s";
            }
            return $"{m}m {s}s";
        }
    }

    public void ApplyDnsPresetDuration(int index)
    {
        string[] presetTags = { "1h", "4h", "1d", "3d", "1w", "1m", "3m", "1y" };
        if (index >= 0 && index < presetTags.Length)
        {
            ApplyDnsPresetDuration(presetTags[index]);
        }
        else if (index > 0)
        {
            TimeSpan ts = TimeSpan.FromMinutes(index);
            DnsCustomDays = (int)ts.TotalDays;
            DnsCustomHours = ts.Hours;
            DnsCustomMinutes = ts.Minutes;
            DnsCustomSeconds = ts.Seconds;
            DnsLockDurationMinutes = index;
        }
    }

    public void ApplyDnsPresetDuration(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        switch (tag.ToLowerInvariant())
        {
            case "1h":
                _dnsCustomDurationInput = "1";
                _dnsCustomDurationUnitIndex = 1;
                _dnsLockDurationMinutes = 60;
                DnsCustomDays = 0;
                DnsCustomHours = 1;
                DnsCustomMinutes = 0;
                DnsCustomSeconds = 0;
                break;
            case "4h":
                _dnsCustomDurationInput = "4";
                _dnsCustomDurationUnitIndex = 1;
                _dnsLockDurationMinutes = 240;
                DnsCustomDays = 0;
                DnsCustomHours = 4;
                DnsCustomMinutes = 0;
                DnsCustomSeconds = 0;
                break;
            case "1d":
                _dnsCustomDurationInput = "1";
                _dnsCustomDurationUnitIndex = 2;
                _dnsLockDurationMinutes = 1440;
                DnsCustomDays = 1;
                DnsCustomHours = 0;
                DnsCustomMinutes = 0;
                DnsCustomSeconds = 0;
                break;
            case "3d":
                _dnsCustomDurationInput = "3";
                _dnsCustomDurationUnitIndex = 2;
                _dnsLockDurationMinutes = 4320;
                DnsCustomDays = 3;
                DnsCustomHours = 0;
                DnsCustomMinutes = 0;
                DnsCustomSeconds = 0;
                break;
            case "1w":
                _dnsCustomDurationInput = "1";
                _dnsCustomDurationUnitIndex = 3;
                _dnsLockDurationMinutes = 10080;
                DnsCustomDays = 7;
                DnsCustomHours = 0;
                DnsCustomMinutes = 0;
                DnsCustomSeconds = 0;
                break;
            case "1m":
                _dnsCustomDurationInput = "1";
                _dnsCustomDurationUnitIndex = 4;
                _dnsLockDurationMinutes = 43200;
                DnsCustomDays = 30;
                DnsCustomHours = 0;
                DnsCustomMinutes = 0;
                DnsCustomSeconds = 0;
                break;
            case "3m":
                _dnsCustomDurationInput = "3";
                _dnsCustomDurationUnitIndex = 4;
                _dnsLockDurationMinutes = 129600;
                DnsCustomDays = 90;
                DnsCustomHours = 0;
                DnsCustomMinutes = 0;
                DnsCustomSeconds = 0;
                break;
            case "1y":
                _dnsCustomDurationInput = "1";
                _dnsCustomDurationUnitIndex = 5;
                _dnsLockDurationMinutes = 525600;
                DnsCustomDays = 365;
                DnsCustomHours = 0;
                DnsCustomMinutes = 0;
                DnsCustomSeconds = 0;
                break;
        }

        OnPropertyChanged(nameof(DnsCustomDurationInput));
        OnPropertyChanged(nameof(DnsCustomDurationUnitIndex));
        OnPropertyChanged(nameof(DnsLockDurationMinutes));
        OnPropertyChanged(nameof(IsDnsDuration1h));
        OnPropertyChanged(nameof(IsDnsDuration4h));
        OnPropertyChanged(nameof(IsDnsDuration24h));
    }

    private void RecalculateDnsLockDuration()
    {
        if (int.TryParse(_dnsCustomDurationInput, out int val) && val > 0)
        {
            int multiplier = _dnsCustomDurationUnitIndex switch
            {
                0 => 1,          // Minutes
                1 => 60,         // Hours
                2 => 1440,       // Days
                3 => 10080,      // Weeks
                4 => 43200,      // Months (30d)
                5 => 525600,     // Years (365d)
                _ => 1440
            };
            try
            {
                _dnsLockDurationMinutes = checked(val * multiplier);
            }
            catch (OverflowException)
            {
                _dnsLockDurationMinutes = 525600;
            }
            OnPropertyChanged(nameof(DnsLockDurationMinutes));
        }
    }

    public string DnsMessage
    {
        get => _dnsMessage;
        set => SetProperty(ref _dnsMessage, value);
    }

    public MainViewModel(IPipeClient? pipeClient = null)
    {
        _pipeClient = pipeClient ?? new PipeClient();
        Profiles = new ObservableCollection<ProfileViewModel>();
        Profiles.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(ProfileCount));
            if (e.NewItems != null)
            {
                foreach (ProfileViewModel p in e.NewItems.OfType<ProfileViewModel>())
                {
                    p.RequestRenameAsync ??= (prof, name) => CommitRenameProfileAsync(prof, name);
                    p.RequestOpenTab ??= (prof, tab) => RequestOpenEditorDialogWithTab?.Invoke(prof, tab);
                }
            }
        };
        ActiveChips = new ObservableCollection<ActiveBlockChipItem>();

        _uiSettings = UiSettingsStore.Load();
        _theme = _uiSettings.Theme;
        _accentColor = _uiSettings.AccentColor;
        _cardDensity = _uiSettings.CardDensity;
        _timeFormat = _uiSettings.TimeFormat;

        // Commands
        NewBlockCommand = new RelayCommand(() => RequestOpenEditorDialog?.Invoke(null));
        OpenExtensionDialogCommand = new RelayCommand(() => RequestOpenExtensionDialog?.Invoke());
        OpenSettingsCommand = new RelayCommand(() => IsSettingsViewActive = true);
        BackToBlocksCommand = new RelayCommand(() => IsSettingsViewActive = false);
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsViewActive = !IsSettingsViewActive);
        OpenAppearanceDialogCommand = new RelayCommand(() => IsSettingsViewActive = true);
        EditBlockCommand = new RelayCommand<ProfileViewModel>(p =>
        {
            if (p != null && p.CanEdit)
            {
                RequestOpenEditorDialog?.Invoke(p);
            }
        });
        OpenLockTabCommand = new RelayCommand<ProfileViewModel>(p =>
        {
            if (p != null)
            {
                RequestOpenEditorDialogWithTab?.Invoke(p, 3);
            }
        });
        OpenAppsTabCommand = new RelayCommand<ProfileViewModel>(p =>
        {
            if (p != null)
            {
                RequestOpenEditorDialogWithTab?.Invoke(p, 0);
            }
        });
        OpenSitesTabCommand = new RelayCommand<ProfileViewModel>(p =>
        {
            if (p != null)
            {
                RequestOpenEditorDialogWithTab?.Invoke(p, 1);
            }
        });
        OpenScheduleTabCommand = new RelayCommand<ProfileViewModel>(p =>
        {
            if (p != null)
            {
                RequestOpenEditorDialogWithTab?.Invoke(p, 2);
            }
        });
        RenameProfileCommand = new RelayCommand<object>(async param =>
        {
            ProfileViewModel? profile = null;
            string? newName = null;

            if (param is ProfileViewModel p)
            {
                profile = p;
                newName = p.EditingName;
            }
            else if (param is (ProfileViewModel pvm, string name))
            {
                profile = pvm;
                newName = name;
            }
            else if (param is Tuple<ProfileViewModel, string> t)
            {
                profile = t.Item1;
                newName = t.Item2;
            }

            if (profile != null)
            {
                await CommitRenameProfileAsync(profile, newName);
            }
        });
        StartBlockCommand = new RelayCommand<ProfileViewModel>(p =>
        {
            if (p != null && CanStartSession)
            {
                RequestOpenStartSessionDialog?.Invoke(p);
            }
        });

        StopSessionCommand = new RelayCommand(async () => await StopSessionAsync(), () => IsSessionActive);
        RetryConnectionCommand = new RelayCommand(async () => await RetryConnectionAsync());
        DeleteBlockCommand = new RelayCommand<ProfileViewModel>(async p => await DeleteBlockAsync(p));
        ToggleDayNightCommand = new RelayCommand(() =>
        {
            Theme = IsDarkMode ? "white" : "black";
        });
        SwitchDesignThemeCommand = new RelayCommand(() => { });
        ThemeManager.ThemeChanged += () =>
        {
            OnPropertyChanged(nameof(IsDarkMode));
            OnPropertyChanged(nameof(Theme));
            OnPropertyChanged(nameof(IsThemeBlack));
            OnPropertyChanged(nameof(IsThemeWhite));
            OnPropertyChanged(nameof(IsThemeGrey));
            OnPropertyChanged(nameof(IsThemeDark));
            OnPropertyChanged(nameof(IsThemeLight));
        };

        SaveDnsSettingsCommand = new RelayCommand(async () => await SaveDnsConfigAsync());
        LockDnsCommand = new RelayCommand(async () => await LockDnsAsync());
        UnlockDnsCommand = new RelayCommand(async () => await UnlockDnsAsync());
        ToggleDnsCommand = new RelayCommand(async () => await ToggleDnsProtectionAsync());
        SetDnsPresetDurationCommand = new RelayCommand<string>(ApplyDnsPresetDuration);

        // 1-second Polling Timer
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += async (s, e) => await PollStatusAsync();
    }

    public void StartPolling()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ServiceLifetimeManager.EnsureServiceRunningAsync(_pipeClient, maxWaitSeconds: 8);
            }
            finally
            {
                _isInitialConnecting = false;
                if (System.Windows.Application.Current?.Dispatcher != null)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        await PollStatusAsync();
                        _timer.Start();
                    });
                }
                else
                {
                    await PollStatusAsync();
                    _timer.Start();
                }
            }
        });
    }

    public void StopPolling()
    {
        _timer.Stop();
    }

    #region Properties

    public ObservableCollection<ProfileViewModel> Profiles { get; }
    public int ProfileCount => Profiles.Count;
    public ObservableCollection<ActiveBlockChipItem> ActiveChips { get; }

    public bool HasActiveBlocks => ActiveChips.Count > 0 || IsSessionActive;

    public bool IsServiceConnected
    {
        get => _isServiceConnected;
        set
        {
            if (SetProperty(ref _isServiceConnected, value))
            {
                OnPropertyChanged(nameof(CanStartSession));
                OnPropertyChanged(nameof(ShowWebsiteWarningBanner));
                OnPropertyChanged(nameof(HostsStatusDisplay));
                OnPropertyChanged(nameof(IsWebsitesBlocking));
            }
        }
    }

    public bool IsServiceElevated
    {
        get => _isServiceElevated;
        set
        {
            if (SetProperty(ref _isServiceElevated, value))
            {
                OnPropertyChanged(nameof(ShowWebsiteWarningBanner));
                OnPropertyChanged(nameof(WebsiteWarningBannerText));
                OnPropertyChanged(nameof(HostsStatusDisplay));
                OnPropertyChanged(nameof(IsWebsitesBlocking));
            }
        }
    }

    public HostsState HostsState
    {
        get => _hostsState;
        set
        {
            if (SetProperty(ref _hostsState, value))
            {
                OnPropertyChanged(nameof(ShowWebsiteWarningBanner));
                OnPropertyChanged(nameof(WebsiteWarningBannerText));
                OnPropertyChanged(nameof(HostsStatusDisplay));
                OnPropertyChanged(nameof(IsWebsitesBlocking));
            }
        }
    }

    public int HostsDomainCount
    {
        get => _hostsDomainCount;
        set
        {
            if (SetProperty(ref _hostsDomainCount, value))
            {
                OnPropertyChanged(nameof(HostsStatusDisplay));
            }
        }
    }

    public bool HostsBlockingOk
    {
        get => _hostsBlockingOk;
        set
        {
            if (SetProperty(ref _hostsBlockingOk, value))
            {
                OnPropertyChanged(nameof(ShowWebsiteWarningBanner));
                OnPropertyChanged(nameof(HostsStatusDisplay));
            }
        }
    }

    public string? HostsLastError
    {
        get => _hostsLastError;
        set
        {
            if (SetProperty(ref _hostsLastError, value))
            {
                OnPropertyChanged(nameof(WebsiteWarningBannerText));
            }
        }
    }

    public string? ServiceWarningMessage
    {
        get => _serviceWarningMessage;
        set => SetProperty(ref _serviceWarningMessage, value);
    }

    public bool IsSessionActive
    {
        get => _isSessionActive;
        set
        {
            if (SetProperty(ref _isSessionActive, value))
            {
                OnPropertyChanged(nameof(CanStartSession));
                OnPropertyChanged(nameof(HasActiveBlocks));
            OnPropertyChanged(nameof(HostsStatusDisplay));
            OnPropertyChanged(nameof(IsWebsitesBlocking));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string? ActiveSessionProfile
    {
        get => _activeSessionProfile;
        set => SetProperty(ref _activeSessionProfile, value);
    }

    public bool ActiveSessionLocked
    {
        get => _activeSessionLocked;
        set => SetProperty(ref _activeSessionLocked, value);
    }

    public string ActiveSessionRemainingFormatted
    {
        get => _activeSessionRemainingFormatted;
        set => SetProperty(ref _activeSessionRemainingFormatted, value);
    }

    public long ActiveSessionRemainingSeconds
    {
        get => _activeSessionRemainingSeconds;
        set => SetProperty(ref _activeSessionRemainingSeconds, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public bool CanStartSession => IsServiceConnected && !IsSessionActive;

    public bool ShowWebsiteWarningBanner => IsServiceConnected && (!IsServiceElevated || HostsState == HostsState.Error);

    public string WebsiteWarningBannerText
    {
        get
        {
            if (!IsServiceElevated)
            {
                return "Website blocking is OFF: the service is not running as Administrator.";
            }
            if (HostsState == HostsState.Error)
            {
                return $"Could not update the hosts file: {HostsLastError}. Retrying automatically.";
            }
            return string.Empty;
        }
    }

    public bool HasActiveWebsiteRules
    {
        get
        {
            if (ActiveChips.Count == 0 && !IsSessionActive) return false;
            var activeNames = new HashSet<string>(ActiveChips.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(ActiveSessionProfile)) activeNames.Add(ActiveSessionProfile);
            return Profiles.Any(p => activeNames.Contains(p.Name) && (p.SitesCount > 0 || p.BlockAllWebsites || (p.WebRules != null && p.WebRules.Count > 0)));
        }
    }

    public bool IsWebsitesBlocking => IsServiceConnected &&
        ((IsServiceElevated && HostsState == HostsState.Blocking) ||
         (IsExtensionConnected && HasActiveWebsiteRules));

    public string HostsStatusDisplay
    {
        get
        {
            if (!IsServiceConnected) return "Websites: off";
            if (!IsServiceElevated && !IsExtensionConnected) return "Websites: off";

            if (HostsState == HostsState.Blocking)
            {
                return $"Websites: blocking {HostsDomainCount} {(HostsDomainCount == 1 ? "site" : "sites")}";
            }

            if (IsExtensionConnected && HasActiveWebsiteRules)
            {
                int activeSites = Profiles
                    .Where(p => ActiveChips.Any(c => string.Equals(c.Name, p.Name, StringComparison.OrdinalIgnoreCase)) ||
                                string.Equals(ActiveSessionProfile, p.Name, StringComparison.OrdinalIgnoreCase))
                    .Sum(p => p.SitesCount);
                if (activeSites > 0)
                {
                    return $"Websites: active ({activeSites} {(activeSites == 1 ? "site" : "sites")})";
                }
                return "Websites: active";
            }

            if (HostsState == HostsState.Error)
            {
                return "Websites: error";
            }

            return "Websites: idle";
        }
    }

    public ExtensionStatusData? ExtensionStatus
    {
        get => _extensionStatus;
        set
        {
            if (SetProperty(ref _extensionStatus, value))
            {
                OnPropertyChanged(nameof(ExtensionStatusDisplay));
                OnPropertyChanged(nameof(IsExtensionConnected));
                OnPropertyChanged(nameof(HostsStatusDisplay));
                OnPropertyChanged(nameof(IsWebsitesBlocking));
            }
        }
    }

    public StatusData? CurrentStatusData
    {
        get => _currentStatusData;
        set => SetProperty(ref _currentStatusData, value);
    }

    public string ExtensionStatusDisplay =>
        UiLogic.FormatExtensionStatusSegment(_extensionStatus?.Browsers?.Where(b => b.Connected).Select(b => b.Name));

    public bool IsExtensionConnected => _extensionStatus?.Connected ?? false;

    #endregion

    #region Commands

    public RelayCommand NewBlockCommand { get; }
    public RelayCommand OpenNewBlockDialogCommand => NewBlockCommand;
    public RelayCommand OpenExtensionDialogCommand { get; }
    public RelayCommand<ProfileViewModel> EditBlockCommand { get; }
    public RelayCommand<ProfileViewModel> OpenLockTabCommand { get; }
    public RelayCommand<ProfileViewModel> OpenAppsTabCommand { get; }
    public RelayCommand<ProfileViewModel> OpenSitesTabCommand { get; }
    public RelayCommand<ProfileViewModel> OpenWebsitesTabCommand => OpenSitesTabCommand;
    public RelayCommand<ProfileViewModel> OpenScheduleTabCommand { get; }
    public RelayCommand<object> RenameProfileCommand { get; }
    public RelayCommand<ProfileViewModel> StartBlockCommand { get; }
    public RelayCommand StopSessionCommand { get; }
    public RelayCommand RetryConnectionCommand { get; }
    public RelayCommand SaveDnsSettingsCommand { get; }
    public RelayCommand LockDnsCommand { get; }
    public RelayCommand UnlockDnsCommand { get; }
    public RelayCommand ToggleDnsCommand { get; }
    public RelayCommand<string> SetDnsPresetDurationCommand { get; }

    #endregion

    #region Polling

    public async Task PollStatusAsync()
    {
        if (_isPolling) return;
        _isPolling = true;

        try
        {
            var req = new Request { Command = CommandNames.GetStatus };
            var result = await _pipeClient.SendRequestAsync(req);

            if (!result.Connected || result.Response == null || !result.Response.Ok)
            {
                if (!_isServiceConnected && !_isInitialConnecting)
                {
                    bool started = await ServiceLifetimeManager.EnsureServiceRunningAsync(_pipeClient, maxWaitSeconds: 3);
                    if (started)
                    {
                        result = await _pipeClient.SendRequestAsync(req);
                    }
                }

                if (!result.Connected || result.Response == null || !result.Response.Ok)
                {
                    IsServiceConnected = false;
                    ServiceWarningMessage = "Background service is not running. Blocks will not be enforced.";
                    return;
                }
            }

            IsServiceConnected = true;
            ServiceWarningMessage = null;
            ErrorMessage = null;
            if (StatusMessage == "Connecting to service...")
            {
                StatusMessage = null;
            }

            if (!string.IsNullOrWhiteSpace(result.Response.Data))
            {
                var status = JsonSerializer.Deserialize<StatusData>(result.Response.Data, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (status != null)
                {
                    CurrentStatusData = status;
                    IsServiceElevated = status.ServiceElevated;
                    HostsState = status.HostsState;
                    HostsDomainCount = status.HostsDomainCount;
                    HostsBlockingOk = status.HostsBlockingOk;
                    HostsLastError = status.HostsLastError;
                    ExtensionStatus = status.ExtensionStatus;

                    var newBanners = status.ExtensionStatus?.WarningBanners ?? new List<string>();
                    if (!WarningBanners.SequenceEqual(newBanners))
                    {
                        WarningBanners.Clear();
                        foreach (var banner in newBanners)
                        {
                            WarningBanners.Add(banner);
                        }
                    }

                    IsSessionActive = status.Active && status.RemainingSeconds > 0;
                    ActiveSessionProfile = status.Profile;
                    ActiveSessionLocked = status.Locked;
                    ActiveSessionRemainingSeconds = status.RemainingSeconds;
                    ActiveSessionRemainingFormatted = UiLogic.FormatCountdown(status.RemainingSeconds);

                    if (!_initialProfilesLoaded)
                    {
                        await LoadProfilesAsync();
                    }
                    else if (status.ProfileStatuses != null)
                    {
                        // Dynamically synchronize profiles created, renamed, or modified via browser extension or CLI
                        bool countMismatch = status.ProfileStatuses.Count != Profiles.Count;
                        bool contentMismatch = !countMismatch && status.ProfileStatuses.Any(ps =>
                            !Profiles.Any(p => p.Id == ps.Id && string.Equals(p.Name, ps.Name, StringComparison.OrdinalIgnoreCase)));
                        if (countMismatch || contentMismatch)
                        {
                            await LoadProfilesAsync();
                        }
                    }

                    UpdateProfilesReadOnlyState(status);

                    if (System.Windows.Application.Current is App app)
                    {
                        string trayText = IsSessionActive
                            ? $"Status: {ActiveSessionProfile} ({ActiveSessionRemainingFormatted})"
                            : (IsWebsitesBlocking ? "Status: Protection Active" : "Status: Guarding (Idle)");
                        app.UpdateTrayStatus(trayText);
                    }
                }
            }

            _pollCycleCount++;

            // If DNS is locked with a timer, update the local countdown smoothly every second
            if (_isDnsLocked && _dnsLockedUntilUtc.HasValue)
            {
                var remaining = _dnsLockedUntilUtc.Value - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    DnsLockRemainingFormatted = UiLogic.FormatCountdown((long)remaining.TotalSeconds);
                }
                else
                {
                    DnsLockRemainingFormatted = "Timer Expired (Unlocking...)";
                }
            }

            // Sync full DNS config over IPC when settings view is open, on initial load, or throttled to every 5s
            if (IsSettingsViewActive || !_hasLoadedDnsOnce || _pollCycleCount % 5 == 0)
            {
                await LoadDnsConfigAsync();
            }

            // Poll Block Events every second for instant toast notifications
            await PollEventsAsync();
        }
        catch (Exception ex)
        {
            IsServiceConnected = false;
            ServiceWarningMessage = $"Error communicating with service: {ex.Message}";
        }
        finally
        {
            _isPolling = false;
        }
    }

    private async Task PollEventsAsync()
    {
        try
        {
            var req = new Request
            {
                Command = CommandNames.GetEvents,
                Args = new Dictionary<string, string> { ["SinceId"] = _lastEventId.ToString() }
            };

            var res = await _pipeClient.SendRequestAsync(req);
            if (res.Connected && res.Response?.Ok == true && !string.IsNullOrWhiteSpace(res.Response.Data))
            {
                var events = JsonSerializer.Deserialize<List<BlockEvent>>(res.Response.Data, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (events != null)
                {
                    bool isInitialPoll = (_lastEventId == 0);
                    foreach (var ev in events)
                    {
                        if (ev.Id > _lastEventId)
                        {
                            _lastEventId = ev.Id;
                        }

                        // Never replay old historical events upon opening the UI
                        // Display notification if service did not dispatch it directly to user session
                        if (!isInitialPoll && _throttler.ShouldNotify(ev.Name, DateTime.UtcNow, ev.Kind))
                        {
                            if (!ev.ServiceDispatched)
                            {
                                OnAppBlockedWithKind?.Invoke(ev.Name, ev.ProfileName, ev.Kind);
                                OnAppBlocked?.Invoke(ev.Name, ev.ProfileName);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore event polling failures
        }
    }

    public async Task RetryConnectionAsync()
    {
        StatusMessage = "Connecting to service...";
        ErrorMessage = null;
        await ServiceLifetimeManager.EnsureServiceRunningAsync(_pipeClient, maxWaitSeconds: 6);
        await PollStatusAsync();
        if (IsServiceConnected)
        {
            StatusMessage = null;
            ErrorMessage = null;
            await LoadProfilesAsync();
        }
        else
        {
            StatusMessage = null;
            ErrorMessage = "Could not connect to Sankalpa Service. Verify service is running.";
        }
    }

    public async Task LoadProfilesAsync()
    {
        try
        {
            var req = new Request { Command = CommandNames.ListProfiles };
            var result = await _pipeClient.SendRequestAsync(req);

            if (result.Connected && result.Response != null && result.Response.Ok && !string.IsNullOrWhiteSpace(result.Response.Data))
            {
                var profiles = JsonSerializer.Deserialize<List<Profile>>(result.Response.Data, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (profiles != null)
                {
                    Action populate = () =>
                    {
                        Profiles.Clear();
                        foreach (var p in profiles)
                        {
                            var vm = new ProfileViewModel(p)
                            {
                                TimeFormat = TimeFormat,
                                CardDensity = CardDensity,
                                RequestRenameAsync = (prof, name) => CommitRenameProfileAsync(prof, name),
                                RequestOpenTab = (prof, tab) => RequestOpenEditorDialogWithTab?.Invoke(prof, tab)
                            };
                            Profiles.Add(vm);
                        }
                    };

                    if (System.Windows.Application.Current?.Dispatcher != null)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(populate);
                    }
                    else
                    {
                        populate();
                    }
                    _initialProfilesLoaded = true;

                    // Update read-only & lock state
                    var statusReq = await _pipeClient.SendRequestAsync(new Request { Command = CommandNames.GetStatus });
                    if (statusReq.Connected && statusReq.Response?.Ok == true && !string.IsNullOrWhiteSpace(statusReq.Response.Data))
                    {
                        var status = JsonSerializer.Deserialize<StatusData>(statusReq.Response.Data, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                        if (status != null)
                        {
                            UpdateProfilesReadOnlyState(status);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed loading profiles: {ex.Message}";
        }
    }

    private void UpdateProfilesReadOnlyState(StatusData status)
    {
        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            ActiveChips.Clear();

            // If manual session active
            if (status.RemainingSeconds > 0 && !string.IsNullOrEmpty(status.Profile))
            {
                string lockIcon = status.Locked ? " 🔒" : "";
                ActiveChips.Add(new ActiveBlockChipItem
                {
                    Name = status.Profile,
                    Text = $"{status.Profile} [Manual {status.RemainingFormatted}{lockIcon}]",
                    IsManual = true,
                    IsLocked = status.Locked,
                    BackgroundColor = "#1E293B",
                    ForegroundColor = "#93C5FD"
                });
            }

            foreach (var p in Profiles)
            {
                var item = status.ProfileStatuses.FirstOrDefault(ps => ps.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase) || ps.Id == p.Id);
                if (item != null)
                {
                    p.Enabled = item.Enabled;
                    p.IsActive = item.IsActive;
                    p.ActiveReason = item.ActiveReason;
                    p.NextChangeLocal = item.NextChangeLocal;
                    p.LockedNow = item.LockedNow;
                    p.LockMode = item.LockMode;

                    if (item.IsActive && item.ActiveReason != "Manual")
                    {
                        string chipText = item.NextChangeLocal.HasValue
                            ? $"{item.Name} until {UiLogic.FormatTime(item.NextChangeLocal.Value, TimeFormat)}"
                            : $"{item.Name} (always on)";

                        ActiveChips.Add(new ActiveBlockChipItem
                        {
                            Name = item.Name,
                            Text = chipText,
                            IsManual = false,
                            IsLocked = item.LockedNow,
                            BackgroundColor = "#14532D",
                            ForegroundColor = "#86EFAC"
                        });
                    }
                }

                bool isManualForThis = status.RemainingSeconds > 0 &&
                    (string.Equals(status.Profile, p.Name, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(status.Profile, p.Id.ToString(), StringComparison.OrdinalIgnoreCase));
                p.IsSessionRunning = isManualForThis;
                p.SessionRemainingFormatted = isManualForThis ? status.RemainingFormatted : "00:00";
                p.IsSessionLocked = isManualForThis && status.Locked;

                bool canEdit = UiLogic.CanEditProfile(p.ToModel(), status, out string? reason);
                p.IsReadOnly = !canEdit || p.LockedNow;
                p.ReadOnlyReason = reason ?? (p.LockedNow ? "Locked profile" : null);
            }

            OnPropertyChanged(nameof(HasActiveBlocks));
            OnPropertyChanged(nameof(HostsStatusDisplay));
            OnPropertyChanged(nameof(IsWebsitesBlocking));
        });
    }

    #endregion

    #region Profile Operations

    public async Task SaveProfileAsync(
        string name,
        List<string> apps,
        List<string> sites,
        List<ScheduleRule>? schedule = null,
        LockMode lockMode = LockMode.None,
        string? newPassword = null,
        string? currentPassword = null,
        string? oldName = null,
        string? unlockText = null,
        string? unlockPassword = null,
        List<WebRule>? webRules = null,
        bool blockAllWebsites = false,
        bool systemWide = false,
        AllowanceConfig? allowance = null,
        bool additiveOnly = false)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(oldName) && !oldName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var existingProf = Profiles.FirstOrDefault(p => string.Equals(p.Name, oldName, StringComparison.OrdinalIgnoreCase));
                int id = existingProf?.Id ?? 0;
                var renameArgs = new Dictionary<string, string>
                {
                    ["name"] = name,
                    ["old_name"] = oldName
                };
                if (id > 0)
                {
                    renameArgs["id"] = id.ToString();
                }

                var renameReq = new Request
                {
                    Command = CommandNames.RenameProfile,
                    Args = renameArgs
                };

                var renameRes = await _pipeClient.SendRequestAsync(renameReq);
                if (!renameRes.Connected || renameRes.Response?.Ok != true)
                {
                    var alreadyNamed = Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (alreadyNamed == null)
                    {
                        ErrorMessage = renameRes.Response?.Error ?? "Failed to rename block.";
                        StatusMessage = null;
                        return;
                    }
                }
            }

            var args = new Dictionary<string, string>
            {
                ["name"] = name,
                ["processes"] = string.Join(",", apps),
                ["domains"] = string.Join(",", sites),
                ["lock_mode"] = lockMode.ToString(),
                ["block_all"] = blockAllWebsites ? "true" : "false",
                ["system_wide"] = systemWide ? "true" : "false",
                ["enabled"] = "true"
            };

            if (additiveOnly)
            {
                args["additive_only"] = "true";
            }

            if (allowance != null)
            {
                args["allowance"] = JsonSerializer.Serialize(allowance);
            }

            if (webRules != null)
            {
                args["webrules"] = JsonSerializer.Serialize(webRules);
            }

            if (schedule != null)
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                args["schedule"] = JsonSerializer.Serialize(schedule, options);
            }
            if (!string.IsNullOrEmpty(newPassword)) args["password"] = newPassword;
            if (!string.IsNullOrEmpty(currentPassword)) args["current_password"] = currentPassword;
            if (!string.IsNullOrEmpty(unlockText)) args["unlock_text"] = unlockText;
            if (!string.IsNullOrEmpty(unlockPassword)) args["unlock_password"] = unlockPassword;

            var req = new Request
            {
                Command = CommandNames.SaveProfile,
                Args = args
            };

            var res = await _pipeClient.SendRequestAsync(req);
            if (res.Connected && res.Response?.Ok == true)
            {
                StatusMessage = $"Saved block '{name}'.";
                ErrorMessage = null;
                await LoadProfilesAsync();
            }
            else
            {
                ErrorMessage = res.Response?.Error ?? "Failed to save block.";
                StatusMessage = null;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error saving block: {ex.Message}";
        }
    }

    public async Task<bool> ToggleBlockAsync(ProfileViewModel profile, Window? owner = null)
    {
        try
        {
            if (profile.IsLockedFromToggling)
            {
                ErrorMessage = $"Block '{profile.Name}' is locked and cannot be turned off.";
                return false;
            }

            if (!profile.Enabled)
            {
                // Turning ON
                if (profile.Schedule.Count == 0)
                {
                    string settingsPath = UiSettingsStore.GetDefaultFilePath();
                    var uiSettings = UiSettingsStore.LoadFromFile(settingsPath);
                    if (!uiSettings.SuppressAlwaysActiveWarning)
                    {
                        var confirmDlg = DarkDialogWindow.CreateDialogWindow(
                            $"Block '{profile.Name}' has no schedule rules.\n\nTurning it On will immediately block all configured apps and websites at all times until turned Off.\n\nTurn On now?",
                            "Turn On Always-Active Block",
                            isConfirmation: true,
                            primaryText: "Turn On",
                            secondaryText: "Cancel",
                            showDontShowAgain: true);

                        if (owner != null) confirmDlg.Owner = owner;
                        if (confirmDlg.ShowDialog() != true)
                        {
                            return false;
                        }

                        if (confirmDlg.DontShowAgainChecked)
                        {
                            uiSettings.SuppressAlwaysActiveWarning = true;
                            UiSettingsStore.SaveToFile(uiSettings, settingsPath);
                        }
                    }
                }

                var req = new Request
                {
                    Command = CommandNames.EnableProfile,
                    Args = new() { ["name"] = profile.Name }
                };

                var res = await _pipeClient.SendRequestAsync(req);
                if (res.Connected && res.Response?.Ok == true)
                {
                    profile.Enabled = true;
                    StatusMessage = $"Block '{profile.Name}' turned ON.";
                    ErrorMessage = null;
                    await PollStatusAsync();
                    return true;
                }
                else
                {
                    ErrorMessage = res.Response?.Error ?? $"Failed to turn on block '{profile.Name}'.";
                    return false;
                }
            }
            else
            {
                // Turning OFF
                string? unlockText = null;
                string? unlockPassword = null;

                if (profile.LockedNow)
                {
                    if (profile.LockMode == LockMode.Locked)
                    {
                        string timeStr = profile.NextChangeLocal.HasValue
                            ? UiLogic.FormatTime(profile.NextChangeLocal.Value, TimeFormat)
                            : "the window ends";
                        DarkDialogWindow.Show(
                            $"Block '{profile.Name}' is locked until {timeStr}.\n\nIt cannot be turned off until the active window completes.",
                            "Block Locked");
                        return false;
                    }
                    else if (profile.LockMode == LockMode.TypingChallenge)
                    {
                        var chReq = new Request
                        {
                            Command = CommandNames.GetUnlockChallenge,
                            Args = new() { ["name"] = profile.Name }
                        };
                        var chRes = await _pipeClient.SendRequestAsync(chReq);
                        if (!chRes.Connected || chRes.Response?.Ok != true || string.IsNullOrEmpty(chRes.Response.Data))
                        {
                            ErrorMessage = chRes.Response?.Error ?? "Failed to request unlock challenge.";
                            return false;
                        }

                        var challengeDlg = new TypingChallengeDialog(chRes.Response.Data);
                        if (owner != null) challengeDlg.Owner = owner;
                        if (challengeDlg.ShowDialog() != true)
                        {
                            return false;
                        }
                        unlockText = challengeDlg.ResultText;
                    }
                    else if (profile.LockMode == LockMode.Password)
                    {
                        var pwdDlg = new PasswordUnlockDialog(profile.Name);
                        if (owner != null) pwdDlg.Owner = owner;
                        if (pwdDlg.ShowDialog() != true)
                        {
                            return false;
                        }
                        unlockPassword = pwdDlg.ResultPassword;
                    }
                }

                var disArgs = new Dictionary<string, string> { ["name"] = profile.Name };
                if (unlockText != null) disArgs["unlock_text"] = unlockText;
                if (unlockPassword != null) disArgs["unlock_password"] = unlockPassword;

                var req = new Request
                {
                    Command = CommandNames.DisableProfile,
                    Args = disArgs
                };

                var res = await _pipeClient.SendRequestAsync(req);
                if (res.Connected && res.Response?.Ok == true)
                {
                    profile.Enabled = false;
                    StatusMessage = $"Block '{profile.Name}' turned OFF.";
                    ErrorMessage = null;
                    await PollStatusAsync();
                    return true;
                }
                else
                {
                    ErrorMessage = res.Response?.Error ?? $"Failed to turn off block '{profile.Name}'.";
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error toggling block: {ex.Message}";
            return false;
        }
    }

    public async Task DeleteBlockAsync(ProfileViewModel? p)
    {
        if (p == null) return;

        // Active or turned on profiles cannot be deleted
        if (p.IsActive || p.Enabled || p.IsSessionRunning || p.IsLockedFromToggling)
        {
            DarkDialogWindow.Show(
                $"Block '{p.Name}' cannot be deleted while it is active or turned on.\n\nPlease turn off the block or stop the active session before deleting.",
                "Cannot Delete Block",
                isConfirmation: false,
                primaryText: "OK");
            return;
        }

        string? unlockPassword = null;
        string? unlockText = null;

        // Prompt for unlock credentials if locked with password or challenge
        if (p.LockMode == LockMode.Password || p.HasPasswordLock)
        {
            var pwdDlg = new PasswordUnlockDialog(p.Name);
            if (Application.Current?.MainWindow != null && Application.Current.MainWindow.IsLoaded)
            {
                pwdDlg.Owner = Application.Current.MainWindow;
            }
            if (pwdDlg.ShowDialog() != true)
            {
                return;
            }
            unlockPassword = pwdDlg.ResultPassword;
        }
        else if (p.LockMode == LockMode.TypingChallenge || p.HasTypingChallenge)
        {
            var chReq = new Request
            {
                Command = CommandNames.GetUnlockChallenge,
                Args = new() { ["name"] = p.Name }
            };
            var chRes = await _pipeClient.SendRequestAsync(chReq);
            if (!chRes.Connected || chRes.Response?.Ok != true || string.IsNullOrEmpty(chRes.Response.Data))
            {
                ErrorMessage = chRes.Response?.Error ?? "Failed to request unlock challenge.";
                return;
            }

            var challengeDlg = new TypingChallengeDialog(chRes.Response.Data);
            if (Application.Current?.MainWindow != null && Application.Current.MainWindow.IsLoaded)
            {
                challengeDlg.Owner = Application.Current.MainWindow;
            }
            if (challengeDlg.ShowDialog() != true)
            {
                return;
            }
            unlockText = challengeDlg.ResultText;
        }
        else if (p.LockMode == LockMode.Locked && (p.LockedNow || p.IsLockedFromToggling))
        {
            DarkDialogWindow.Show(
                $"Block '{p.Name}' is locked.\n\nIt cannot be deleted while locked.",
                "Block Locked",
                isConfirmation: false,
                primaryText: "OK");
            return;
        }

        bool confirm = DarkDialogWindow.Show(
            $"Are you sure you want to delete '{p.Name}'?\n\nThis will remove all associated app and website block rules.",
            "Delete Block",
            isConfirmation: true,
            primaryText: "Delete",
            secondaryText: "Cancel");
        if (!confirm) return;

        await DeleteProfileAsync(p.Name, unlockPassword, unlockText);
    }

    public async Task DeleteProfileAsync(string name, string? unlockPassword = null, string? unlockText = null)
    {
        try
        {
            var delArgs = new Dictionary<string, string> { ["name"] = name };
            if (!string.IsNullOrEmpty(unlockPassword)) delArgs["unlock_password"] = unlockPassword;
            if (!string.IsNullOrEmpty(unlockText)) delArgs["unlock_text"] = unlockText;

            var req = new Request
            {
                Command = CommandNames.DeleteProfile,
                Args = delArgs
            };

            var res = await _pipeClient.SendRequestAsync(req);
            if (res.Connected && res.Response?.Ok == true)
            {
                StatusMessage = $"Deleted block '{name}'.";
                ErrorMessage = null;
                await LoadProfilesAsync();
            }
            else
            {
                string err = res.Response?.Error ?? "Failed to delete block.";
                ErrorMessage = err;
                StatusMessage = null;
                DarkDialogWindow.Show(err, "Cannot Delete Block", isConfirmation: false, primaryText: "OK");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error deleting block: {ex.Message}";
        }
    }

    public async Task<bool> CommitRenameProfileAsync(ProfileViewModel profile, string? newName)
    {
        if (profile == null) return false;

        string trimmed = newName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            ErrorMessage = "Block name cannot be empty.";
            profile.EditingName = profile.Name;
            profile.IsEditingName = false;
            return false;
        }

        if (string.Equals(trimmed, profile.Name, StringComparison.Ordinal))
        {
            profile.EditingName = profile.Name;
            profile.IsEditingName = false;
            return true;
        }

        if (Profiles.Any(p => p != profile && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = $"A block named '{trimmed}' already exists.";
            profile.EditingName = profile.Name;
            profile.IsEditingName = false;
            return false;
        }

        bool success = await RenameProfileAsync(profile.Id, trimmed, profile.Name);
        if (success)
        {
            profile.Name = trimmed;
            profile.EditingName = trimmed;
            profile.IsEditingName = false;
            StatusMessage = $"Renamed block to '{trimmed}'.";
            ErrorMessage = null;
            return true;
        }
        else
        {
            profile.EditingName = profile.Name;
            profile.IsEditingName = false;
            return false;
        }
    }

    public async Task<bool> RenameProfileAsync(int id, string newName, string? oldName = null)
    {
        try
        {
            var args = new Dictionary<string, string>
            {
                ["name"] = newName
            };
            if (id > 0)
            {
                args["id"] = id.ToString();
            }
            if (!string.IsNullOrEmpty(oldName))
            {
                args["old_name"] = oldName;
            }

            var req = new Request
            {
                Command = CommandNames.RenameProfile,
                Args = args
            };

            var res = await _pipeClient.SendRequestAsync(req);
            if (res.Connected && res.Response?.Ok == true)
            {
                await LoadProfilesAsync();
                return true;
            }
            else
            {
                ErrorMessage = res.Response?.Error ?? "Failed to rename block.";
                return false;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error renaming block: {ex.Message}";
            return false;
        }
    }

    public async Task StartSessionAsync(string profileName, int minutes, bool locked)
    {
        try
        {
            var req = new Request
            {
                Command = CommandNames.StartSession,
                Args = new Dictionary<string, string>
                {
                    ["profile"] = profileName,
                    ["minutes"] = minutes.ToString(),
                    ["locked"] = locked.ToString()
                }
            };

            var res = await _pipeClient.SendRequestAsync(req);
            if (res.Connected && res.Response?.Ok == true)
            {
                StatusMessage = $"{DurationFormatter.FormatAdjective(TimeSpan.FromMinutes(minutes))} session started on {profileName}.";
                ErrorMessage = null;
                await PollStatusAsync();
            }
            else
            {
                ErrorMessage = res.Response?.Error ?? "Failed to start block session.";
                StatusMessage = null;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error starting block session: {ex.Message}";
        }
    }

    public async Task StopSessionAsync(Window? owner = null)
    {
        try
        {
            var req = new Request { Command = CommandNames.StopSession };
            var res = await _pipeClient.SendRequestAsync(req);

            if (res.Connected && res.Response != null)
            {
                if (res.Response.Ok)
                {
                    StatusMessage = "Session stopped.";
                    ErrorMessage = null;
                    await PollStatusAsync();
                }
                else
                {
                    DarkDialogWindow.Show(
                        res.Response.Error ?? "Cannot stop session while LOCKED. You must wait for the timer to expire or place an EMERGENCY_UNLOCK file.",
                        "Stop Refused");
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error stopping session: {ex.Message}";
        }
    }

    private bool _hasLoadedDnsOnce;

    public async Task LoadDnsConfigAsync()
    {
        if (!_isServiceConnected) return;
        try
        {
            var req = new Request { Command = CommandNames.GetDnsConfig };
            var res = await _pipeClient.SendRequestAsync(req);
            if (res.Connected && res.Response?.Ok == true && !string.IsNullOrWhiteSpace(res.Response.Data))
            {
                var config = JsonSerializer.Deserialize<DnsConfig>(res.Response.Data, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (config != null)
                {
                    _isDnsLocked = config.Locked;
                    _isDnsEnabled = config.Enabled;

                    if (!_hasLoadedDnsOnce || _isDnsLocked)
                    {
                        _selectedDnsPreset = config.PresetName ?? "Cloudflare Family";
                        _primaryDns = config.PrimaryDns ?? "1.1.1.3";
                        _secondaryDns = config.SecondaryDns ?? "1.0.0.3";
                        _dnsLockType = config.Locked ? (config.LockType ?? "Timer") : "Timer";
                        _dnsLockDurationMinutes = (int)Math.Max(15, config.LockDurationSeconds / 60);
                        _hasLoadedDnsOnce = true;

                        OnPropertyChanged(nameof(SelectedDnsPreset));
                        OnPropertyChanged(nameof(IsPresetCloudflare));
                        OnPropertyChanged(nameof(IsPresetCleanBrowsing));
                        OnPropertyChanged(nameof(IsPresetAdGuard));
                        OnPropertyChanged(nameof(IsPresetQuad9));
                        OnPropertyChanged(nameof(IsPresetCustom));
                        OnPropertyChanged(nameof(IsCustomDns));
                        OnPropertyChanged(nameof(PrimaryDns));
                        OnPropertyChanged(nameof(SecondaryDns));
                        OnPropertyChanged(nameof(DnsLockType));
                        OnPropertyChanged(nameof(IsDnsLockChallenge));
                        OnPropertyChanged(nameof(IsDnsLockTimer));
                        OnPropertyChanged(nameof(DnsLockDurationMinutes));
                        OnPropertyChanged(nameof(IsDnsDuration15m));
                        OnPropertyChanged(nameof(IsDnsDuration30m));
                        OnPropertyChanged(nameof(IsDnsDuration1h));
                        OnPropertyChanged(nameof(IsDnsDuration2h));
                        OnPropertyChanged(nameof(IsDnsDuration4h));
                        OnPropertyChanged(nameof(IsDnsDuration8h));
                        OnPropertyChanged(nameof(IsDnsDuration24h));
                    }

                    _dnsLockedUntilUtc = config.Locked && config.LockedUntilUtc.HasValue ? config.LockedUntilUtc : null;

                    if (config.Locked && config.LockedUntilUtc.HasValue)
                    {
                        var remaining = config.LockedUntilUtc.Value - DateTime.UtcNow;
                        if (remaining > TimeSpan.Zero)
                        {
                            DnsLockRemainingFormatted = UiLogic.FormatCountdown((long)remaining.TotalSeconds);
                            bool is24Hour = string.Equals(_uiSettings.TimeFormat, "24h", StringComparison.OrdinalIgnoreCase);
                            DnsLockedUntilLocalFormatted = TimeFormatter.Format(config.LockedUntilUtc.Value.ToLocalTime(), is24Hour);
                        }
                        else
                        {
                            DnsLockRemainingFormatted = "Timer Expired (Unlocking...)";
                            DnsLockedUntilLocalFormatted = null;
                        }
                    }
                    else if (config.Locked)
                    {
                        DnsLockRemainingFormatted = "Locked via Typing Challenge";
                        DnsLockedUntilLocalFormatted = null;
                    }
                    else
                    {
                        DnsLockRemainingFormatted = null;
                        DnsLockedUntilLocalFormatted = null;
                    }

                    OnPropertyChanged(nameof(IsDnsEnabled));
                    OnPropertyChanged(nameof(IsDnsLocked));
                    OnPropertyChanged(nameof(CanEditDns));
                    OnPropertyChanged(nameof(CanUnlockDns));
                    OnPropertyChanged(nameof(DnsLockRemainingFormatted));
                    OnPropertyChanged(nameof(DnsLockedUntilLocalFormatted));
                }
            }
        }
        catch
        {
            // Ignore polling errors
        }
    }

    public async Task ToggleDnsProtectionAsync()
    {
        if (!CanEditDns) return;
        await SaveDnsConfigAsync(!IsDnsEnabled);
    }

    public async Task SaveDnsConfigAsync(bool? enableOverride = null)
    {
        if (!_isServiceConnected)
        {
            DnsMessage = "Service is not connected.";
            return;
        }

        try
        {
            bool targetEnabled = enableOverride ?? IsDnsEnabled;
            var preset = DnsPresets.All.FirstOrDefault(p => string.Equals(p.Name, SelectedDnsPreset, StringComparison.OrdinalIgnoreCase));
            string ipv6Primary = preset?.PrimaryIpv6 ?? string.Empty;
            string ipv6Secondary = preset?.SecondaryIpv6 ?? string.Empty;

            var args = new Dictionary<string, string>
            {
                ["preset"] = SelectedDnsPreset,
                ["primary"] = PrimaryDns,
                ["secondary"] = SecondaryDns ?? string.Empty,
                ["primary_ipv6"] = ipv6Primary,
                ["secondary_ipv6"] = ipv6Secondary,
                ["enabled"] = targetEnabled.ToString()
            };

            var req = new Request { Command = CommandNames.SaveDnsConfig, Args = args };
            var res = await _pipeClient.SendRequestAsync(req);
            if (res.Connected && res.Response?.Ok == true)
            {
                IsDnsEnabled = targetEnabled;
                DnsMessage = targetEnabled ? "DNS settings applied successfully." : "DNS restored to automatic DHCP.";
                _hasLoadedDnsOnce = false;
                await LoadDnsConfigAsync();
            }
            else
            {
                DnsMessage = res.Response?.Error ?? "Failed to save DNS settings.";
            }
        }
        catch (Exception ex)
        {
            DnsMessage = $"Error: {ex.Message}";
        }
    }

    public async Task LockDnsAsync()
    {
        if (!_isServiceConnected)
        {
            DnsMessage = "Service is not connected.";
            return;
        }

        if (IsDnsLocked)
        {
            DnsMessage = "DNS settings are already locked.";
            return;
        }

        long customSeconds = (long)Math.Max(0, DnsCustomDays) * 86400L
                           + (long)Math.Max(0, DnsCustomHours) * 3600L
                           + (long)Math.Max(0, DnsCustomMinutes) * 60L
                           + (long)Math.Max(0, DnsCustomSeconds);
        long durationSeconds = customSeconds > 0
            ? customSeconds
            : checked((long)Math.Max(1, DnsLockDurationMinutes) * 60L);
        string formattedDuration = UiLogic.FormatCountdown(durationSeconds);
        DateTime targetTime = DateTime.Now.AddSeconds(durationSeconds);

        string prompt = IsDnsLockChallenge
            ? "Lock DNS Shield using a 64-character typing challenge?\n\nOnce locked, DNS cannot be disabled or modified until the challenge is typed."
            : $"Permanently lock DNS Protection for {formattedDuration} (until {targetTime:g})?\n\nWARNING: Once locked, DNS cannot be modified, disabled, or unlocked early until the timer expires.\n\nAre you sure you want to permanently lock DNS?";

        bool confirm = DarkDialogWindow.Show(
            prompt,
            "Confirm Permanent DNS Lock",
            isConfirmation: true,
            primaryText: "Lock Now",
            secondaryText: "Cancel");

        if (!confirm) return;

        try
        {
            var args = new Dictionary<string, string>
            {
                ["lock_type"] = DnsLockType,
                ["duration"] = durationSeconds.ToString()
            };

            var req = new Request { Command = CommandNames.LockDns, Args = args };
            var res = await _pipeClient.SendRequestAsync(req);
            if (res.Connected && res.Response?.Ok == true)
            {
                DnsMessage = IsDnsLockTimer
                    ? $"DNS protection permanently locked for {formattedDuration}."
                    : "DNS protection is now locked.";
                _hasLoadedDnsOnce = false;
                await LoadDnsConfigAsync();
            }
            else
            {
                DnsMessage = res.Response?.Error ?? "Failed to lock DNS settings.";
            }
        }
        catch (Exception ex)
        {
            DnsMessage = $"Error: {ex.Message}";
        }
    }

    public async Task UnlockDnsAsync()
    {
        if (!_isServiceConnected)
        {
            DnsMessage = "Service is not connected.";
            return;
        }

        if (!IsDnsLocked)
        {
            DnsMessage = "DNS is not locked.";
            return;
        }

        if (IsDnsLockTimer)
        {
            var req = new Request { Command = CommandNames.UnlockDns };
            var res = await _pipeClient.SendRequestAsync(req);
            if (res.Connected && res.Response?.Ok == true)
            {
                DnsMessage = "DNS unlocked successfully.";
                _hasLoadedDnsOnce = false;
                await LoadDnsConfigAsync();
            }
            else
            {
                string rem = DnsLockRemainingFormatted ?? "active";
                DnsMessage = $"DNS Protection is permanently locked.\n\nRemaining time: {rem}\n\nTimer lock cannot be stopped early.";
                DarkDialogWindow.Show(DnsMessage, "Permanent DNS Lock Active", isConfirmation: false, primaryText: "OK");
            }
            return;
        }

        try
        {
            var reqChallenge = new Request { Command = CommandNames.GetDnsChallenge };
            var resChallenge = await _pipeClient.SendRequestAsync(reqChallenge);
            if (!resChallenge.Connected || resChallenge.Response?.Ok != true || string.IsNullOrWhiteSpace(resChallenge.Response.Data))
            {
                DnsMessage = "Failed to generate typing challenge.";
                return;
            }

            string challengeText = resChallenge.Response.Data;
            string? typedText = RequestTypingChallenge?.Invoke(challengeText);
            if (string.IsNullOrEmpty(typedText))
            {
                DnsMessage = "DNS unlock cancelled.";
                return;
            }

            var reqUnlock = new Request
            {
                Command = CommandNames.UnlockDns,
                Args = new Dictionary<string, string> { ["challenge_text"] = typedText }
            };

            var resUnlock = await _pipeClient.SendRequestAsync(reqUnlock);
            if (resUnlock.Connected && resUnlock.Response?.Ok == true)
            {
                DnsMessage = "DNS unlocked successfully!";
                _hasLoadedDnsOnce = false;
                await LoadDnsConfigAsync();
            }
            else
            {
                DnsMessage = resUnlock.Response?.Error ?? "Incorrect challenge text. DNS remains locked.";
                DarkDialogWindow.Show(DnsMessage, "Unlock Failed", isConfirmation: false, primaryText: "OK");
            }
        }
        catch (Exception ex)
        {
            DnsMessage = $"Error: {ex.Message}";
        }
    }

    #endregion
}
