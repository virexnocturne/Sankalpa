using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Blocker.Core;
using CheckBox = System.Windows.Controls.CheckBox;

namespace Blocker.App;

public partial class AppPickerDialog : Window
{
    private readonly List<string> _alreadyBlockedExes = new();
    private List<AppPickerItem> _rawItems = new();
    private readonly HashSet<string> _userSelectedExes = new(StringComparer.OrdinalIgnoreCase);
    private List<AppPickerRow> _currentRows = new();

    public List<string> SelectedApps { get; private set; } = new();

    public AppPickerDialog(
        IEnumerable<string>? alreadyBlockedExes = null,
        List<AppPickerItem>? customItems = null)
    {
        InitializeComponent();
        WindowHelper.EnableDarkMode(this);

        if (alreadyBlockedExes != null)
        {
            _alreadyBlockedExes.AddRange(alreadyBlockedExes);
        }

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

    public void SetSearchQuery(string query)
    {
        SearchTextBox.Text = query;
    }

    private void RebuildList()
    {
        string query = SearchTextBox.Text;
        bool showAll = ShowAllProcessesCheckBox.IsChecked == true;

        var rows = AppPickerLogic.BuildFlatPickerList(_rawItems, _alreadyBlockedExes, query, showAll);

        foreach (var row in rows)
        {
            if (!row.IsHeader && !row.IsAlreadyBlocked)
            {
                if (_userSelectedExes.Contains(row.ExeName))
                {
                    row.IsSelected = true;
                }

                row.PropertyChanged += OnRowPropertyChanged;
            }
        }

        _currentRows = rows;
        AppsListBox.ItemsSource = _currentRows;

        EmptyNoticeTextBlock.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            if (row.IsEnabled)
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
            .Where(x => !string.IsNullOrEmpty(x))
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
