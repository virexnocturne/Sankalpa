using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Blocker.Core;

public enum MarkdownBlockType
{
    Header,
    Paragraph,
    TaskItem
}

public class MarkdownBlockItem : INotifyPropertyChanged
{
    private bool _isChecked;
    public event PropertyChangedEventHandler? PropertyChanged;

    public MarkdownBlockType BlockType { get; set; }
    public string Text { get; set; } = string.Empty;
    public int TaskIndex { get; set; } = -1;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                OnCheckToggled?.Invoke(TaskIndex, value);
            }
        }
    }

    [JsonIgnore]
    public Action<int, bool>? OnCheckToggled { get; set; }
}

public class NoteCard : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _title = string.Empty;
    private string _markdownContent = string.Empty;
    private DateTime _createdAtUtc = DateTime.UtcNow;
    private DateTime _updatedAtUtc = DateTime.UtcNow;
    private bool _isExpanded = true;
    private bool _isFullExpanded = false;
    private readonly ObservableCollection<MarkdownBlockItem> _blocks = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string MarkdownContent
    {
        get => _markdownContent;
        set
        {
            if (SetProperty(ref _markdownContent, value))
            {
                RebuildParsedBlocks();
                OnPropertyChanged(nameof(IsLongContent));
                OnPropertyChanged(nameof(ShowSeeMore));
                OnPropertyChanged(nameof(VisibleBlocks));
            }
        }
    }

    public DateTime CreatedAtUtc
    {
        get => _createdAtUtc;
        set
        {
            if (SetProperty(ref _createdAtUtc, value))
            {
                OnPropertyChanged(nameof(RelativeTime));
            }
        }
    }

    public DateTime UpdatedAtUtc
    {
        get => _updatedAtUtc;
        set
        {
            if (SetProperty(ref _updatedAtUtc, value))
            {
                OnPropertyChanged(nameof(RelativeTime));
            }
        }
    }

    [JsonIgnore]
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    [JsonIgnore]
    public bool IsFullExpanded
    {
        get => _isFullExpanded;
        set
        {
            if (SetProperty(ref _isFullExpanded, value))
            {
                OnPropertyChanged(nameof(VisibleBlocks));
                OnPropertyChanged(nameof(SeeMoreText));
            }
        }
    }

    [JsonIgnore]
    public string RelativeTime => FormatRelativeTime(UpdatedAtUtc);

    [JsonIgnore]
    public ObservableCollection<MarkdownBlockItem> Blocks => _blocks;

    [JsonIgnore]
    public IEnumerable<MarkdownBlockItem> VisibleBlocks
    {
        get
        {
            if (!IsFullExpanded && _blocks.Count > 4)
            {
                return _blocks.Take(4);
            }
            return _blocks;
        }
    }

    [JsonIgnore]
    public bool IsLongContent => _blocks.Count > 4 || (!string.IsNullOrEmpty(MarkdownContent) && MarkdownContent.Length > 220);

    [JsonIgnore]
    public bool ShowSeeMore => IsLongContent;

    [JsonIgnore]
    public string SeeMoreText => IsFullExpanded ? "see less" : "see more...";

    [JsonIgnore]
    public Action? OnCardChanged { get; set; }

    public NoteCard()
    {
        RebuildParsedBlocks();
    }

    public NoteCard(string title, string content)
    {
        _title = title;
        _markdownContent = content;
        _createdAtUtc = DateTime.UtcNow;
        _updatedAtUtc = DateTime.UtcNow;
        RebuildParsedBlocks();
    }

    public void RebuildParsedBlocks()
    {
        _blocks.Clear();
        if (string.IsNullOrWhiteSpace(MarkdownContent))
        {
            OnPropertyChanged(nameof(Blocks));
            OnPropertyChanged(nameof(VisibleBlocks));
            OnPropertyChanged(nameof(IsLongContent));
            OnPropertyChanged(nameof(ShowSeeMore));
            return;
        }

        var lines = MarkdownContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var taskRegex = new Regex(@"^\s*[-*]\s*\[([ xX])\]\s*(.*)$");
        int taskCount = 0;

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            var taskMatch = taskRegex.Match(trimmed);
            if (taskMatch.Success)
            {
                int currentIdx = taskCount++;
                bool isChecked = taskMatch.Groups[1].Value.Trim().Equals("x", StringComparison.OrdinalIgnoreCase);
                string text = taskMatch.Groups[2].Value;
                var item = new MarkdownBlockItem
                {
                    BlockType = MarkdownBlockType.TaskItem,
                    Text = text,
                    TaskIndex = currentIdx,
                    IsChecked = isChecked,
                    OnCheckToggled = (idx, check) =>
                    {
                        ToggleTaskCheckbox(idx, check);
                        OnCardChanged?.Invoke();
                    }
                };
                _blocks.Add(item);
            }
            else if (trimmed.StartsWith("#"))
            {
                string headerText = trimmed.TrimStart('#').Trim();
                _blocks.Add(new MarkdownBlockItem
                {
                    BlockType = MarkdownBlockType.Header,
                    Text = headerText
                });
            }
            else
            {
                _blocks.Add(new MarkdownBlockItem
                {
                    BlockType = MarkdownBlockType.Paragraph,
                    Text = trimmed
                });
            }
        }

        OnPropertyChanged(nameof(Blocks));
        OnPropertyChanged(nameof(VisibleBlocks));
        OnPropertyChanged(nameof(IsLongContent));
        OnPropertyChanged(nameof(ShowSeeMore));
    }

    public static string FormatRelativeTime(DateTime utcTime)
    {
        var diff = DateTime.UtcNow - utcTime;
        if (diff.TotalSeconds < 60) return "Just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
        return utcTime.ToLocalTime().ToString("MMM d");
    }

    /// <summary>
    /// Toggles the n-th markdown checkbox (- [ ] or - [x]) in the markdown content.
    /// </summary>
    public bool ToggleTaskCheckbox(int taskIndex, bool isChecked)
    {
        if (string.IsNullOrEmpty(MarkdownContent) || taskIndex < 0)
        {
            return false;
        }

        var regex = new Regex(@"^(\s*[-*]\s*\[)([ xX])(\].*)$", RegexOptions.Multiline);
        var matches = regex.Matches(MarkdownContent);

        if (taskIndex >= matches.Count)
        {
            return false;
        }

        var targetMatch = matches[taskIndex];
        string newChar = isChecked ? "x" : " ";
        string before = MarkdownContent.Substring(0, targetMatch.Groups[2].Index);
        string after = MarkdownContent.Substring(targetMatch.Groups[2].Index + 1);

        _markdownContent = before + newChar + after;
        _updatedAtUtc = DateTime.UtcNow;
        OnPropertyChanged(nameof(MarkdownContent));
        OnPropertyChanged(nameof(UpdatedAtUtc));
        OnPropertyChanged(nameof(RelativeTime));
        return true;
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
