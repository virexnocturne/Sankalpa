using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace Blocker.Core;

public class AppPickerItem
{
    public string DisplayName { get; set; } = string.Empty;
    public string ExeName { get; set; } = string.Empty;
    public string? Path { get; set; }
    public string Category { get; set; } = string.Empty;
    public bool IsSystemProcess { get; set; }

    public override string ToString() => DisplayName;
}

public class AppPickerRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public bool IsHeader { get; set; }
    public string? HeaderTitle { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ExeName { get; set; } = string.Empty;
    public string? Path { get; set; }
    public string Category { get; set; } = string.Empty;
    public bool IsSystemProcess { get; set; }
    public bool IsAlreadyBlocked { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    public bool IsEnabled => !IsHeader && !IsAlreadyBlocked;
    public bool IsSelectable => !IsHeader && !IsAlreadyBlocked;
    public string? StatusText => IsAlreadyBlocked ? "already blocked" : null;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public static class AppPickerLogic
{
    public const string CatPopular = "Popular distractions";
    public const string CatRunning = "Running apps";
    public const string CatInstalled = "Installed apps";
    public const string CatSystem = "System & background";

    public static readonly List<(string DisplayName, string ExeName)> PopularDistractions = new()
    {
        ("Discord", "Discord.exe"),
        ("Steam", "steam.exe"),
        ("Epic Games Launcher", "EpicGamesLauncher.exe"),
        ("Spotify", "Spotify.exe"),
        ("Telegram", "Telegram.exe"),
        ("WhatsApp", "WhatsApp.exe"),
        ("Slack", "slack.exe"),
        ("Teams", "ms-teams.exe"),
        ("Riot Client", "RiotClientServices.exe"),
        ("Valorant", "VALORANT.exe"),
        ("Minecraft Launcher", "Minecraft.exe"),
        ("Roblox Player", "RobloxPlayerBeta.exe"),
        ("Notepad", "notepad.exe")
    };

    public static string NormalizeExeName(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string trimmed = input.Trim();
        string fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = trimmed;
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName : $"{fileName}.exe";
    }

    public static string GetDisplayName(string exeOrName)
    {
        if (string.IsNullOrWhiteSpace(exeOrName)) return string.Empty;
        string trimmed = exeOrName.Trim();
        string fileName = Path.GetFileName(trimmed);
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^4];
        }
        return fileName;
    }

    public static bool IsSystemProcess(string? exePath, string? procName, bool hasMainWindow)
    {
        if (!string.IsNullOrWhiteSpace(procName))
        {
            if (ProtectedProcesses.IsProtected(procName)) return true;
        }

        if (!string.IsNullOrWhiteSpace(exePath))
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(winDir) && exePath.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!hasMainWindow && string.IsNullOrWhiteSpace(exePath))
        {
            return true;
        }

        return false;
    }

    public static List<AppPickerItem> FilterAndDeduplicate(
        IEnumerable<AppPickerItem> rawItems,
        string? searchText,
        bool showAllProcesses)
    {
        var filtered = rawItems.Where(item => showAllProcesses || !item.IsSystemProcess);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            string query = searchText.Trim();
            filtered = filtered.Where(item =>
                item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.ExeName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(item.Path) && item.Path.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        var categoryPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [CatPopular] = 1,
            [CatRunning] = 2,
            [CatInstalled] = 3,
            [CatSystem] = 4
        };

        var deduped = filtered
            .OrderBy(i => categoryPriority.TryGetValue(i.Category, out int p) ? p : 99)
            .GroupBy(i => NormalizeExeName(i.ExeName), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(i => categoryPriority.TryGetValue(i.Category, out int p) ? p : 99)
            .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return deduped;
    }

    public static List<AppPickerRow> BuildFlatPickerList(
        IEnumerable<AppPickerItem> rawItems,
        IEnumerable<string>? alreadyBlockedExes,
        string? searchText,
        bool showAllProcesses)
    {
        var blockedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (alreadyBlockedExes != null)
        {
            foreach (var b in alreadyBlockedExes)
            {
                string norm = NormalizeExeName(b);
                if (!string.IsNullOrEmpty(norm)) blockedSet.Add(norm);
            }
        }

        var items = FilterAndDeduplicate(rawItems, searchText, showAllProcesses);

        var sectionOrder = new[]
        {
            CatPopular,
            CatRunning,
            CatInstalled,
            CatSystem
        };

        var result = new List<AppPickerRow>();

        foreach (var section in sectionOrder)
        {
            var sectionItems = items.Where(i => string.Equals(i.Category, section, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sectionItems.Count == 0) continue;

            // Section Header
            result.Add(new AppPickerRow
            {
                IsHeader = true,
                HeaderTitle = section
            });

            // Section Items
            foreach (var item in sectionItems)
            {
                string normExe = NormalizeExeName(item.ExeName);
                bool isBlocked = blockedSet.Contains(normExe);

                result.Add(new AppPickerRow
                {
                    IsHeader = false,
                    DisplayName = item.DisplayName,
                    ExeName = normExe,
                    Path = item.Path,
                    Category = item.Category,
                    IsSystemProcess = item.IsSystemProcess,
                    IsAlreadyBlocked = isBlocked,
                    IsSelected = isBlocked
                });
            }
        }

        return result;
    }

    public static int GetSelectedCount(IEnumerable<AppPickerRow>? rows)
    {
        if (rows == null) return 0;
        return rows.Count(r => !r.IsHeader && !r.IsAlreadyBlocked && r.IsSelected);
    }

    public static List<string> GetSelectedExeNames(IEnumerable<AppPickerRow>? rows)
    {
        if (rows == null) return new List<string>();

        return rows
            .Where(r => !r.IsHeader && !r.IsAlreadyBlocked && r.IsSelected)
            .Select(r => NormalizeExeName(r.ExeName))
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
