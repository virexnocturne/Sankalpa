using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Blocker.Core;
using CheckBox = System.Windows.Controls.CheckBox;

namespace Blocker.App;

public class BlockedByProfilePickerRow : AppPickerRow
{
    public new string? StatusText => "(Blocked by Block Profile)";
}

public partial class AppPickerDialog : Window
{
    private readonly List<string> _alreadyBlockedExes = new();
    private List<AppPickerItem> _rawItems = new();
    private readonly HashSet<string> _userSelectedExes = new(StringComparer.OrdinalIgnoreCase);
    private List<AppPickerRow> _currentRows = new();

    public bool IsMeditationMode { get; set; }
    public HashSet<string> BlockedProfileApps { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> SelectedApps { get; private set; } = new();

    public AppPickerDialog(
        IEnumerable<string>? alreadyBlockedExes = null,
        List<AppPickerItem>? customItems = null,
        bool isMeditationMode = false,
        IEnumerable<string>? blockedProfileApps = null)
    {
        InitializeComponent();
        WindowHelper.EnableDarkMode(this);

        IsMeditationMode = isMeditationMode;
        if (blockedProfileApps != null)
        {
            foreach (var b in blockedProfileApps)
            {
                string norm = AppPickerLogic.NormalizeExeName(b);
                if (!string.IsNullOrEmpty(norm)) BlockedProfileApps.Add(norm);
            }
        }

        if (alreadyBlockedExes != null)
        {
            _alreadyBlockedExes.AddRange(alreadyBlockedExes);
        }

        Loaded += (s, e) =>
        {
            if (CheckIsMeditationMode())
            {
                LoadBlockedProfileApps();
                RebuildList();
            }
        };

        if (customItems != null)
        {
            _rawItems = customItems;
            RebuildList();
        }
        else
        {
            LoadingOverlay.Visibility = Visibility.Visible;

            // Load genuine running processes immediately so dialog is populated with real apps while background scanning completes
            var initialRunning = InstalledAppScanner.ScanRunningProcessesOnly();
            _rawItems.AddRange(initialRunning);
            RebuildList();

            // Background thread scanning
            Task.Run(() =>
            {
                var scanned = InstalledAppScanner.ScanAll();
                Dispatcher.Invoke(() =>
                {
                    _rawItems = scanned;
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    RebuildList();
                });
            });
        }
    }

    public bool CheckIsMeditationMode()
    {
        if (IsMeditationMode) return true;
        if (Owner != null && Owner.GetType().Name.Contains("Meditation", StringComparison.OrdinalIgnoreCase)) return true;
        if (System.Windows.Application.Current != null)
        {
            foreach (Window win in System.Windows.Application.Current.Windows)
            {
                if (win.IsVisible && win.GetType().Name.Contains("Meditation", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void LoadBlockedProfileApps()
    {
        if (!CheckIsMeditationMode()) return;

        // 1. From Application.Current MainViewModel if available
        try
        {
            if (System.Windows.Application.Current != null)
            {
                foreach (Window win in System.Windows.Application.Current.Windows)
                {
                    if (win.DataContext is MainViewModel mvm)
                    {
                        foreach (var prof in mvm.Profiles)
                        {
                            if (prof.Enabled || prof.IsActive || prof.IsSessionRunning)
                            {
                                foreach (var app in prof.BlockedProcesses)
                                {
                                    string norm = AppPickerLogic.NormalizeExeName(app);
                                    if (!string.IsNullOrEmpty(norm))
                                    {
                                        BlockedProfileApps.Add(norm);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // 2. Fallback to IPC request to service if available
        if (BlockedProfileApps.Count == 0)
        {
            try
            {
                var pipeClient = new PipeClient(timeoutMs: 300);
                var task = pipeClient.SendRequestAsync(new Request { Command = CommandNames.ListProfiles });
                if (task.Wait(300) && task.Result.Connected && task.Result.Response?.Ok == true && !string.IsNullOrWhiteSpace(task.Result.Response.Data))
                {
                    var profiles = System.Text.Json.JsonSerializer.Deserialize<List<Profile>>(task.Result.Response.Data, new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (profiles != null)
                    {
                        foreach (var prof in profiles)
                        {
                            if (prof.Enabled)
                            {
                                foreach (var app in prof.BlockedProcesses)
                                {
                                    string norm = AppPickerLogic.NormalizeExeName(app);
                                    if (!string.IsNullOrEmpty(norm))
                                    {
                                        BlockedProfileApps.Add(norm);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }

    public void SetSearchQuery(string query)
    {
        SearchTextBox.Text = query;
    }

    private void RebuildList()
    {
        string query = SearchTextBox.Text;
        bool showAll = ShowAllProcessesCheckBox.IsChecked == true;
        bool isMeditation = CheckIsMeditationMode();
        if (isMeditation && BlockedProfileApps.Count == 0)
        {
            LoadBlockedProfileApps();
        }

        var rows = AppPickerLogic.BuildFlatPickerList(_rawItems, _alreadyBlockedExes, query, showAll);
        var finalRows = new List<AppPickerRow>(rows.Count);

        foreach (var row in rows)
        {
            if (isMeditation && !row.IsHeader && !string.IsNullOrEmpty(row.ExeName) &&
                BlockedProfileApps.Contains(AppPickerLogic.NormalizeExeName(row.ExeName)))
            {
                var blockedRow = new BlockedByProfilePickerRow
                {
                    IsHeader = false,
                    DisplayName = row.DisplayName,
                    ExeName = row.ExeName,
                    Path = row.Path,
                    Category = row.Category,
                    IsSystemProcess = row.IsSystemProcess,
                    IsAlreadyBlocked = true,
                    IsSelected = false
                };
                finalRows.Add(blockedRow);
                continue;
            }

            if (!row.IsHeader && !row.IsAlreadyBlocked)
            {
                if (_userSelectedExes.Contains(row.ExeName))
                {
                    row.IsSelected = true;
                }

                row.PropertyChanged += OnRowPropertyChanged;
            }

            finalRows.Add(row);
        }

        _currentRows = finalRows;
        AppsListBox.ItemsSource = _currentRows;

        EmptyNoticeTextBlock.Visibility = finalRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectedSummary();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppPickerRow.IsSelected) && sender is AppPickerRow row)
        {
            if (row.IsSelected)
            {
                _userSelectedExes.Add(row.ExeName);
            }
            else
            {
                _userSelectedExes.Remove(row.ExeName);
            }

            UpdateSelectedSummary();
        }
    }

    private void UpdateSelectedSummary()
    {
        int count = _userSelectedExes.Count;
        AddSelectedButton.Content = $"Add selected ({count})";
        AddSelectedButton.IsEnabled = count > 0;

        if (count == 0)
        {
            SelectedSummaryTextBlock.Text = "Select one or more applications to add.";
        }
        else
        {
            SelectedSummaryTextBlock.Text = $"{count} {(count == 1 ? "application" : "applications")} selected.";
        }
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        RebuildList();
    }

    private void ShowAllProcessesCheckBox_Click(object sender, RoutedEventArgs e)
    {
        RebuildList();
    }

    private void AppRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AppPickerRow row)
        {
            if (row.IsEnabled && !BlockedProfileApps.Contains(AppPickerLogic.NormalizeExeName(row.ExeName)))
            {
                if (e.OriginalSource is DependencyObject d && IsDescendantOf<CheckBox>(d))
                {
                    // CheckBox click will toggle IsSelected directly
                    return;
                }

                // Row surface clicked
                row.IsSelected = !row.IsSelected;
                e.Handled = true;
            }
        }
    }

    private static bool IsDescendantOf<T>(DependencyObject element) where T : DependencyObject
    {
        DependencyObject? current = element;
        while (current != null)
        {
            if (current is T) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void AppsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Don't retain ListBoxItem selection since row check state is used
        AppsListBox.SelectedItem = null;
    }

    private void AddSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedApps = _userSelectedExes
            .Select(AppPickerLogic.NormalizeExeName)
            .Where(x => !string.IsNullOrEmpty(x) && !BlockedProfileApps.Contains(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
