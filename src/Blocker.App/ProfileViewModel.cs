using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Blocker.Core;

namespace Blocker.App;

public class ProfileViewModel : ViewModelBase
{
    private int _id;
    private string _name = string.Empty;
    private bool _isReadOnly;
    private string? _readOnlyReason;
    private bool _isDirty;
    private bool _enabled;
    private LockMode _lockMode = LockMode.None;
    private bool _isActive;
    private string _activeReason = "None";
    private DateTime? _nextChangeLocal;
    private bool _lockedNow;
    private bool _blockAllWebsites;
    private bool _systemWide;
    private bool _isSessionRunning;
    private string _sessionRemainingFormatted = "00:00";
    private bool _isSessionLocked;
    private AllowanceConfig _allowance = new();
    private bool _isEditingName;
    private string _editingName = string.Empty;

    public bool IsEditingName
    {
        get => _isEditingName;
        set => SetProperty(ref _isEditingName, value);
    }

    public string EditingName
    {
        get => _editingName;
        set => SetProperty(ref _editingName, value);
    }

    public void BeginEditName()
    {
        EditingName = Name;
        IsEditingName = true;
    }

    public void CancelEditName()
    {
        EditingName = Name;
        IsEditingName = false;
    }

    public Func<ProfileViewModel, string, Task<bool>>? RequestRenameAsync { get; set; }
    public Action<ProfileViewModel, int>? RequestOpenTab { get; set; }

    public RelayCommand BeginEditNameCommand { get; }
    public RelayCommand SaveNameCommand { get; }
    public RelayCommand CancelEditNameCommand { get; }
    public RelayCommand OpenAppsCommand { get; }
    public RelayCommand OpenSitesCommand { get; }
    public RelayCommand OpenAppsTabCommand => OpenAppsCommand;
    public RelayCommand OpenSitesTabCommand => OpenSitesCommand;
    public RelayCommand OpenWebsitesTabCommand => OpenSitesCommand;

    public int AppsCount => BlockedProcesses.Count;
    public string AppsBadgeText => UiLogic.FormatAppsBadge(AppsCount);
    public string SitesBadgeText => UiLogic.FormatSitesBadge(SitesCount, BlockAllWebsites);
    public string ExceptionsBadgeText => UiLogic.FormatExceptionsBadge(WebRules.Count(r => r.Allow));
    public bool HasExceptions => WebRules.Any(r => r.Allow);

    public AllowanceConfig Allowance
    {
        get => _allowance;
        set => SetProperty(ref _allowance, value);
    }

    public bool IsSessionRunning
    {
        get => _isSessionRunning;
        set
        {
            if (SetProperty(ref _isSessionRunning, value))
            {
                OnPropertyChanged(nameof(IsLockedFromToggling));
                OnPropertyChanged(nameof(CanToggle));
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(IsToggleChecked));
                OnPropertyChanged(nameof(OnOffText));
                OnPropertyChanged(nameof(LockColText));
            }
        }
    }

    public string SessionRemainingFormatted
    {
        get => _sessionRemainingFormatted;
        set => SetProperty(ref _sessionRemainingFormatted, value);
    }

    public bool IsSessionLocked
    {
        get => _isSessionLocked;
        set
        {
            if (SetProperty(ref _isSessionLocked, value))
            {
                OnPropertyChanged(nameof(IsLockedFromToggling));
                OnPropertyChanged(nameof(CanToggle));
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(IsToggleChecked));
                OnPropertyChanged(nameof(OnOffText));
                OnPropertyChanged(nameof(LockColText));
            }
        }
    }

    public bool HasSchedule => Schedule.Count > 0;
    public bool HasWebsites => SitesCount > 0 || BlockAllWebsites;
    public bool HasApps => BlockedProcesses.Count > 0;

    public ObservableCollection<string> BlockedProcesses { get; }
    public ObservableCollection<string> BlockedDomains { get; }
    public ObservableCollection<WebRule> WebRules { get; }
    public ObservableCollection<ScheduleRule> Schedule { get; }

    public ProfileViewModel(Profile? profile = null)
    {
        if (profile != null)
        {
            _id = profile.Id;
            _name = profile.Name;
            _enabled = profile.Enabled;
            _lockMode = profile.LockMode;
            _blockAllWebsites = profile.BlockAllWebsites;
            _systemWide = profile.SystemWide;
            BlockedProcesses = new ObservableCollection<string>(profile.BlockedProcesses);
            BlockedDomains = new ObservableCollection<string>(profile.BlockedDomains);
            WebRules = new ObservableCollection<WebRule>(profile.WebRules ?? new());
            Schedule = new ObservableCollection<ScheduleRule>(profile.Schedule ?? new());
            _allowance = profile.Allowance ?? new();
            _delayedUnlockMinutes = profile.DelayedUnlockMinutes > 0 ? profile.DelayedUnlockMinutes : 15;
        }
        else
        {
            BlockedProcesses = new ObservableCollection<string>();
            BlockedDomains = new ObservableCollection<string>();
            WebRules = new ObservableCollection<WebRule>();
            Schedule = new ObservableCollection<ScheduleRule>();
        }

        BlockedProcesses.CollectionChanged += (s, e) =>
        {
            IsDirty = true;
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(AppsCount));
            OnPropertyChanged(nameof(AppsBadgeText));
            OnPropertyChanged(nameof(HasApps));
        };
        BlockedDomains.CollectionChanged += (s, e) =>
        {
            IsDirty = true;
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(SitesCount));
            OnPropertyChanged(nameof(SitesBadgeText));
            OnPropertyChanged(nameof(HasWebsites));
        };
        WebRules.CollectionChanged += (s, e) =>
        {
            IsDirty = true;
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(SitesCount));
            OnPropertyChanged(nameof(SitesBadgeText));
            OnPropertyChanged(nameof(ExceptionsBadgeText));
            OnPropertyChanged(nameof(HasExceptions));
            OnPropertyChanged(nameof(HasWebsites));
        };
        Schedule.CollectionChanged += (s, e) =>
        {
            IsDirty = true;
            OnPropertyChanged(nameof(ScheduleSummary));
            OnPropertyChanged(nameof(ScheduleColText));
            OnPropertyChanged(nameof(StateChipText));
            OnPropertyChanged(nameof(HasSchedule));
            OnPropertyChanged(nameof(IsLockedFromToggling));
            OnPropertyChanged(nameof(CanToggle));
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(IsToggleChecked));
            OnPropertyChanged(nameof(OnOffText));
        };

        BeginEditNameCommand = new RelayCommand(() => BeginEditName());
        CancelEditNameCommand = new RelayCommand(() => CancelEditName());
        SaveNameCommand = new RelayCommand(async () =>
        {
            if (RequestRenameAsync != null)
            {
                await RequestRenameAsync(this, EditingName);
            }
            else
            {
                string trimmed = EditingName?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    Name = trimmed;
                    EditingName = trimmed;
                }
                else
                {
                    EditingName = Name;
                }
                IsEditingName = false;
            }
        });

        OpenAppsCommand = new RelayCommand(() => RequestOpenTab?.Invoke(this, 0));
        OpenSitesCommand = new RelayCommand(() => RequestOpenTab?.Invoke(this, 1));
    }

    public Thickness CardStripeThickness => (IsActive || Enabled) ? new Thickness(4, 0, 0, 0) : new Thickness(0);
    public string ScheduleColText => Schedule.Count == 0 ? "Blocked at all times" : ScheduleSummary;
    public string LockColText => (IsSessionRunning && IsSessionLocked)
        ? "Complete Lock 🔒"
        : (Schedule.Count > 0 && (LockedNow || IsActive))
            ? "Schedule 🔒"
            : LockMode switch
            {
                LockMode.Locked => "Schedule 🔒",
                LockMode.Password => "Password 🔒",
                LockMode.TypingChallenge => "Challenge 🔒",
                LockMode.DelayedUnlock => $"Delay ({DelayedUnlockMinutes}m) ⏳",
                _ => "Unlocked 🔓"
            };

    private int _delayedUnlockMinutes = 15;
    public int DelayedUnlockMinutes
    {
        get => _delayedUnlockMinutes;
        set
        {
            if (SetProperty(ref _delayedUnlockMinutes, value))
            {
                OnPropertyChanged(nameof(LockColText));
            }
        }
    }

    public bool BlockAllWebsites
    {
        get => _blockAllWebsites;
        set
        {
            if (SetProperty(ref _blockAllWebsites, value))
            {
                IsDirty = true;
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(SitesBadgeText));
                OnPropertyChanged(nameof(HasWebsites));
            }
        }
    }

    public int SitesCount => WebRules.Count > 0 ? WebRules.Count(r => !r.Allow) : BlockedDomains.Count;

    public string Summary
    {
        get
        {
            int apps = BlockedProcesses.Count;
            int sites = SitesCount;
            int exceptions = WebRules.Count(r => r.Allow);
            return UiLogic.FormatCardSummary(apps, sites, exceptions, BlockAllWebsites);
        }
    }

    private string _timeFormat = "24h";
    public string TimeFormat
    {
        get => _timeFormat;
        set
        {
            if (SetProperty(ref _timeFormat, value))
            {
                OnPropertyChanged(nameof(StateChipText));
                OnPropertyChanged(nameof(ScheduleSummary));
                OnPropertyChanged(nameof(ScheduleColText));
            }
        }
    }

    private string _cardDensity = "Comfortable";
    public string CardDensity
    {
        get => _cardDensity;
        set
        {
            if (SetProperty(ref _cardDensity, value))
            {
                OnPropertyChanged(nameof(CardPadding));
                OnPropertyChanged(nameof(CardMargin));
            }
        }
    }

    public Thickness CardPadding => string.Equals(CardDensity, "Compact", StringComparison.OrdinalIgnoreCase)
        ? new Thickness(16, 10, 16, 10)
        : new Thickness(18, 14, 18, 14);

    public Thickness CardMargin => string.Equals(CardDensity, "Compact", StringComparison.OrdinalIgnoreCase)
        ? new Thickness(0, 0, 0, 6)
        : new Thickness(0, 0, 0, 10);

    public string ScheduleSummary => ScheduleLogic.FormatSummary(Schedule.ToList(), TimeFormat);

    public string LockLabel => LockColText;

    public string StateChipText
    {
        get
        {
            if (IsActive)
            {
                if (NextChangeLocal.HasValue)
                {
                    return $"Active now until {UiLogic.FormatTime(NextChangeLocal.Value, TimeFormat)}";
                }
                if (ActiveReason == "AlwaysOn")
                {
                    return "Active now (always)";
                }
                if (ActiveReason == "Manual")
                {
                    return "Active now (manual)";
                }
                return "Active now";
            }

            if (Enabled)
            {
                if (NextChangeLocal.HasValue)
                {
                    return $"Next: {UiLogic.FormatTimeWithDay(NextChangeLocal.Value, TimeFormat)}";
                }
                return "On";
            }

            return "Off";
        }
    }

    public string StateChipBackground => IsActive ? "#166534" : (Enabled ? "#1E293B" : "#262626");
    public string StateChipForeground => IsActive ? "#4ADE80" : (Enabled ? "#93C5FD" : "#9CA3AF");

    public bool IsChallengeLock => LockMode == LockMode.DelayedUnlock
                                || LockMode == LockMode.Password
                                || LockMode == LockMode.TypingChallenge;

    public bool IsLockedFromToggling => (IsSessionRunning && IsSessionLocked)
        || (IsActive && Schedule.Count > 0 && LockedNow)
        || (!IsChallengeLock && IsActive && LockMode == LockMode.Locked);

    public bool CanToggle => !IsLockedFromToggling;
    public bool CanDelete => !IsLockedFromToggling && !IsActive && !Enabled;
    public bool HasPasswordLock => LockMode == LockMode.Password;
    public bool HasTypingChallenge => LockMode == LockMode.TypingChallenge;
    public bool IsToggleChecked => IsLockedFromToggling || Enabled || IsActive;

    public string OnOffText => (IsLockedFromToggling || Enabled || IsActive) ? "ON" : "OFF";

    public int Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                IsDirty = true;
                if (!IsEditingName)
                {
                    _editingName = value;
                    OnPropertyChanged(nameof(EditingName));
                }
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                IsDirty = true;
                OnPropertyChanged(nameof(OnOffText));
                OnPropertyChanged(nameof(IsToggleChecked));
                OnPropertyChanged(nameof(StateChipText));
                OnPropertyChanged(nameof(StateChipBackground));
                OnPropertyChanged(nameof(StateChipForeground));
                OnPropertyChanged(nameof(CardStripeThickness));
                OnPropertyChanged(nameof(CanDelete));
            }
        }
    }

    public LockMode LockMode
    {
        get => _lockMode;
        set
        {
            if (SetProperty(ref _lockMode, value))
            {
                IsDirty = true;
                OnPropertyChanged(nameof(LockLabel));
                OnPropertyChanged(nameof(LockColText));
                OnPropertyChanged(nameof(HasPasswordLock));
                OnPropertyChanged(nameof(HasTypingChallenge));
                OnPropertyChanged(nameof(IsChallengeLock));
                OnPropertyChanged(nameof(IsLockedFromToggling));
                OnPropertyChanged(nameof(CanToggle));
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(IsToggleChecked));
                OnPropertyChanged(nameof(OnOffText));
            }
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(StateChipText));
                OnPropertyChanged(nameof(StateChipBackground));
                OnPropertyChanged(nameof(StateChipForeground));
                OnPropertyChanged(nameof(CardStripeThickness));
                OnPropertyChanged(nameof(IsLockedFromToggling));
                OnPropertyChanged(nameof(CanToggle));
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(IsToggleChecked));
                OnPropertyChanged(nameof(OnOffText));
                OnPropertyChanged(nameof(LockColText));
                OnPropertyChanged(nameof(LockLabel));
            }
        }
    }

    public string ActiveReason
    {
        get => _activeReason;
        set => SetProperty(ref _activeReason, value);
    }

    public DateTime? NextChangeLocal
    {
        get => _nextChangeLocal;
        set
        {
            if (SetProperty(ref _nextChangeLocal, value))
            {
                OnPropertyChanged(nameof(StateChipText));
            }
        }
    }

    public bool LockedNow
    {
        get => _lockedNow;
        set
        {
            if (SetProperty(ref _lockedNow, value))
            {
                OnPropertyChanged(nameof(IsReadOnly));
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(IsLockedFromToggling));
                OnPropertyChanged(nameof(CanToggle));
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(IsToggleChecked));
                OnPropertyChanged(nameof(OnOffText));
                OnPropertyChanged(nameof(LockColText));
                OnPropertyChanged(nameof(LockLabel));
            }
        }
    }

    public bool IsReadOnly
    {
        get => _isReadOnly || _lockedNow;
        set
        {
            if (SetProperty(ref _isReadOnly, value))
            {
                OnPropertyChanged(nameof(CanEdit));
            }
        }
    }

    public bool CanEdit => true;

    public string? ReadOnlyReason
    {
        get => _readOnlyReason;
        set => SetProperty(ref _readOnlyReason, value);
    }

    public bool SystemWide
    {
        get => _systemWide;
        set
        {
            if (SetProperty(ref _systemWide, value))
            {
                IsDirty = true;
            }
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        set => SetProperty(ref _isDirty, value);
    }

    public void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsChallengeLock));
        OnPropertyChanged(nameof(IsLockedFromToggling));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(IsToggleChecked));
        OnPropertyChanged(nameof(OnOffText));
        OnPropertyChanged(nameof(StateChipText));
        OnPropertyChanged(nameof(StateChipBackground));
        OnPropertyChanged(nameof(StateChipForeground));
        OnPropertyChanged(nameof(CardStripeThickness));
        OnPropertyChanged(nameof(LockLabel));
        OnPropertyChanged(nameof(LockColText));
        OnPropertyChanged(nameof(HasPasswordLock));
        OnPropertyChanged(nameof(HasTypingChallenge));
        OnPropertyChanged(nameof(ScheduleColText));
        OnPropertyChanged(nameof(ScheduleSummary));
    }

    public Profile ToModel()
    {
        return new Profile
        {
            Id = Id,
            Name = Name.Trim(),
            BlockedProcesses = BlockedProcesses.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            BlockedDomains = BlockedDomains.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            WebRules = WebRules.ToList(),
            BlockAllWebsites = BlockAllWebsites,
            SystemWide = SystemWide,
            Enabled = Enabled,
            Schedule = Schedule.ToList(),
            Allowance = Allowance ?? new(),
            LockMode = LockMode
        };
    }
}
