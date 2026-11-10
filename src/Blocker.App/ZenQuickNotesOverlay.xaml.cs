using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Blocker.Core;

// Disambiguate WPF vs WindowsForms/System.Drawing
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using Rectangle = System.Windows.Shapes.Rectangle;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Cursors = System.Windows.Input.Cursors;
using DataObject = System.Windows.DataObject;

namespace Blocker.App;

/// <summary>
/// A floating, draggable topmost Quick Notes flyout window that users can summon anywhere while working.
/// Supports Markdown formatting, interactive checklists, live preview, and auto-saving.
/// Optimized for near-zero keystroke lag via debounced thumbnails, incremental card rendering, and async background saving.
/// </summary>
public partial class ZenQuickNotesOverlay : Window
{
    private sealed record CardRenderCache(string Id, string Title, string Content, DateTime UpdatedAtUtc);

    private readonly List<NoteCard> _cards = new();
    private readonly Dictionary<string, Border> _renderedCardBorders = new();
    private NoteCard? _editingCard;
    private readonly DispatcherTimer _autoSaveNoticeTimer;
    private readonly DispatcherTimer _thumbnailDebounceTimer;
    private List<string> _currentThumbRefs = new();
    private bool _isPreviewTabActive;
    private bool _isInternalSave;

    public Action<string>? OnNotesChanged { get; set; }

    public ZenQuickNotesOverlay()
    {
        InitializeComponent();

        _thumbnailDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _thumbnailDebounceTimer.Tick += (s, e) =>
        {
            _thumbnailDebounceTimer.Stop();
            RebuildThumbnailsAsync();
        };

        if (ComposerContentInput != null)
        {
            ComposerContentInput.PreviewKeyDown += ComposerContentInput_PreviewKeyDown;
            ComposerContentInput.TextChanged += (s, e) =>
            {
                _thumbnailDebounceTimer.Stop();
                _thumbnailDebounceTimer.Start();
            };
            DataObject.AddPastingHandler(ComposerContentInput, OnComposerContentPasting);
        }

        Loaded += (s, e) => RecordOpenAndCheckHintBadge();
        IsVisibleChanged += (s, e) => { if (IsVisible) RecordOpenAndCheckHintBadge(); };

        _autoSaveNoticeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _autoSaveNoticeTimer.Tick += (s, e) =>
        {
            _autoSaveNoticeTimer.Stop();
            AutoSaveStatusText.Text = "Auto-saved";
            AutoSaveStatusText.Foreground = (Brush)FindResource("Success");
            AutoSaveIcon.Stroke = (Brush)FindResource("Success");
        };
    }

    /// <summary>
    /// Loads quick notes (JSON NoteCardCollection array or plain text) into the card view.
    /// Skips re-render if caller was the source of the change or if cards are identical.
    /// </summary>
    public void LoadNotes(string? notesData)
    {
        if (_isInternalSave)
        {
            return;
        }

        var deserialized = NoteCardCollection.Deserialize(notesData);
        if (deserialized.Count == 0)
        {
            deserialized = NotesVaultStore.LoadNotesFromVault();
        }

        if (NoteCardCollection.AreEquivalent(_cards, deserialized))
        {
            return;
        }

        _cards.Clear();
        _cards.AddRange(deserialized);

        // Switch to cards view and render
        ComposerPanel.Visibility = Visibility.Collapsed;
        CardsViewPanel.Visibility = Visibility.Visible;
        RenderCards();
    }

    /// <summary>
    /// Renders cards incrementally: reuses unchanged visual cards, updates only modified ones,
    /// and adds/removes elements with minimal visual tree mutation.
    /// </summary>
    private void RenderCards()
    {
        if (_cards.Count == 0)
        {
            EmptyStatePanel.Visibility = Visibility.Visible;
            CardsScrollViewer.Visibility = Visibility.Collapsed;
            CardsItemsPanel.Children.Clear();
            _renderedCardBorders.Clear();
            return;
        }

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        CardsScrollViewer.Visibility = Visibility.Visible;

        var activeIds = new HashSet<string>(_cards.Select(c => c.Id));

        // 1. Remove cards that no longer exist
        var toRemoveKeys = _renderedCardBorders.Keys.Where(id => !activeIds.Contains(id)).ToList();
        foreach (var id in toRemoveKeys)
        {
            if (_renderedCardBorders.TryGetValue(id, out var border))
            {
                CardsItemsPanel.Children.Remove(border);
                _renderedCardBorders.Remove(id);
            }
        }

        // 2. Add or update cards in-place
        for (int i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            if (_renderedCardBorders.TryGetValue(card.Id, out var existingBorder))
            {
                if (existingBorder.Tag is CardRenderCache cached &&
                    cached.Title == card.Title &&
                    cached.Content == card.MarkdownContent &&
                    cached.UpdatedAtUtc == card.UpdatedAtUtc)
                {
                    // Perfectly identical: align position if necessary
                    int currentIdx = CardsItemsPanel.Children.IndexOf(existingBorder);
                    if (currentIdx != i && currentIdx >= 0)
                    {
                        CardsItemsPanel.Children.RemoveAt(currentIdx);
                        CardsItemsPanel.Children.Insert(Math.Min(i, CardsItemsPanel.Children.Count), existingBorder);
                    }
                    continue;
                }

                // Card modified: replace element
                int pos = CardsItemsPanel.Children.IndexOf(existingBorder);
                var updatedBorder = CreateCardElement(card, i);
                _renderedCardBorders[card.Id] = updatedBorder;
                if (pos >= 0)
                {
                    CardsItemsPanel.Children[pos] = updatedBorder;
                }
                else
                {
                    CardsItemsPanel.Children.Insert(Math.Min(i, CardsItemsPanel.Children.Count), updatedBorder);
                }
            }
            else
            {
                // New card
                var newBorder = CreateCardElement(card, i);
                _renderedCardBorders[card.Id] = newBorder;
                CardsItemsPanel.Children.Insert(Math.Min(i, CardsItemsPanel.Children.Count), newBorder);
            }
        }
    }

    /// <summary>
    /// Creates a rendered UI card for a single NoteCard with interactive checkboxes and markdown formatting.
    /// </summary>
    private Border CreateCardElement(NoteCard card, int cardIndex)
    {
        var cardBorder = new Border
        {
            Background = (Brush)FindResource("SurfaceRaised"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 10),
            Tag = new CardRenderCache(card.Id, card.Title, card.MarkdownContent, card.UpdatedAtUtc)
        };

        var cardStack = new StackPanel();

        // 1. Header: Title, Timestamp, Edit and Delete buttons
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string titleDisplay = string.IsNullOrWhiteSpace(card.Title) ? $"Quick Note {cardIndex + 1}" : card.Title;
        var titleText = new TextBlock
        {
            Text = titleDisplay,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = (Brush)FindResource("TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleStack.Children.Add(titleText);

        var timestampText = new TextBlock
        {
            Text = card.UpdatedAtUtc.ToLocalTime().ToString("MMM d, h:mm tt"),
            FontSize = 12,
            Foreground = (Brush)FindResource("TextDisabled"),
            Margin = new Thickness(0, 2, 0, 0)
        };
        titleStack.Children.Add(timestampText);
        Grid.SetColumn(titleStack, 0);
        headerGrid.Children.Add(titleStack);

        var actionsStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var editButton = new Button
        {
            Content = "Edit",
            Style = (Style)FindResource("OverlayMiniButton"),
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = "Edit note content"
        };
        editButton.Click += (s, e) => EditNote(card);
        actionsStack.Children.Add(editButton);

        var deleteButton = new Button
        {
            Content = "×",
            Style = (Style)FindResource("OverlayMiniButton"),
            Padding = new Thickness(7, 2, 7, 2),
            ToolTip = "Delete note card"
        };
        deleteButton.Click += (s, e) => DeleteNote(card);
        actionsStack.Children.Add(deleteButton);

        Grid.SetColumn(actionsStack, 1);
        headerGrid.Children.Add(actionsStack);
        cardStack.Children.Add(headerGrid);

        // Divider
        var divider = new Rectangle
        {
            Height = 1,
            Fill = (Brush)FindResource("Border"),
            Margin = new Thickness(0, 8, 0, 8)
        };
        cardStack.Children.Add(divider);

        // 2. Body: Markdown & Interactive Checkboxes
        var bodyPanel = new StackPanel();
        NoteMarkdownRenderer.RenderMarkdownToPanel(bodyPanel, card.MarkdownContent, this, (taskIndex, isChecked) =>
        {
            card.ToggleTaskCheckbox(taskIndex, isChecked);
            cardBorder.Tag = new CardRenderCache(card.Id, card.Title, card.MarkdownContent, card.UpdatedAtUtc);
            SaveActiveNotes();
        });
        cardStack.Children.Add(bodyPanel);

        cardBorder.Child = cardStack;
        return cardBorder;
    }

    public void RenderMarkdownToPanel(StackPanel bodyPanel, string markdown, Action<int, bool>? onTaskToggled = null)
    {
        NoteMarkdownRenderer.RenderMarkdownToPanel(bodyPanel, markdown, this, onTaskToggled);
    }

    /// <summary>
    /// Serializes active cards and triggers debounced background saving.
    /// </summary>
    private void SaveActiveNotes()
    {
        string serialized = _cards.Count == 0 ? string.Empty : NoteCardCollection.Serialize(_cards);

        AutoSaveStatusText.Text = "Saving...";
        AutoSaveStatusText.Foreground = (Brush)FindResource("TextSecondary");
        AutoSaveIcon.Stroke = (Brush)FindResource("TextSecondary");

        _autoSaveNoticeTimer.Stop();
        _autoSaveNoticeTimer.Start();

        NotesVaultStore.QueueSave(_cards);

        _isInternalSave = true;
        try
        {
            OnNotesChanged?.Invoke(serialized);
        }
        finally
        {
            _isInternalSave = false;
        }
    }

    private void EditNote(NoteCard card)
    {
        _editingCard = card;
        ComposerHeadingText.Text = "Edit Quick Note";
        ComposerTitleInput.Text = card.Title;
        ComposerContentInput.Text = card.MarkdownContent;

        SwitchComposerToEditMode();
        CardsViewPanel.Visibility = Visibility.Collapsed;
        ComposerPanel.Visibility = Visibility.Visible;
        ComposerContentInput.Focus();
        RebuildThumbnailsAsync();
    }

    private void DeleteNote(NoteCard card)
    {
        _cards.Remove(card);
        RenderCards();
        SaveActiveNotes();
    }

    private void NewNoteButton_Click(object sender, RoutedEventArgs e)
    {
        _editingCard = null;
        ComposerHeadingText.Text = "New Quick Note";
        ComposerTitleInput.Text = string.Empty;
        ComposerContentInput.Text = string.Empty;

        SwitchComposerToEditMode();
        CardsViewPanel.Visibility = Visibility.Collapsed;
        ComposerPanel.Visibility = Visibility.Visible;
        ComposerTitleInput.Focus();
        RebuildThumbnailsAsync();
    }

    private void SaveComposer_Click(object? sender, RoutedEventArgs? e)
    {
        string title = ComposerTitleInput.Text.Trim();
        string content = ComposerContentInput.Text;

        // Auto-detect title from first heading or first line if title input is left empty (title fallback)
        if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(content))
        {
            var lines = content.Replace("\r", "").Split('\n');
            foreach (var line in lines)
            {
                string tr = line.Trim();
                if (tr.StartsWith("# ") || tr.StartsWith("## ") || tr.StartsWith("### "))
                {
                    title = tr.TrimStart('#').Trim();
                    break;
                }
                else if (!string.IsNullOrWhiteSpace(tr) && string.IsNullOrEmpty(title))
                {
                    string clean = tr.TrimStart('-', '*', '•', '>', ' ', '[', ']', '☐', '☑');
                    if (clean.Length > 40) clean = clean[..40] + "...";
                    title = clean;
                    break;
                }
            }
        }

        if (_editingCard != null)
        {
            _editingCard.Title = title;
            _editingCard.MarkdownContent = content;
            _editingCard.UpdatedAtUtc = DateTime.UtcNow;
        }
        else
        {
            var newCard = new NoteCard(title, content);
            _cards.Add(newCard);
        }

        _editingCard = null;
        ComposerPanel.Visibility = Visibility.Collapsed;
        CardsViewPanel.Visibility = Visibility.Visible;

        RenderCards();
        SaveActiveNotes();
    }

    private void CancelComposer_Click(object sender, RoutedEventArgs e)
    {
        _editingCard = null;
        ComposerPanel.Visibility = Visibility.Collapsed;
        CardsViewPanel.Visibility = Visibility.Visible;
    }

    // ==========================================
    // COMPOSER EDIT / PREVIEW TAB TOGGLE
    // ==========================================
    private void ComposerEditTab_Click(object sender, RoutedEventArgs e)
    {
        SwitchComposerToEditMode();
    }

    private void ComposerPreviewTab_Click(object sender, RoutedEventArgs e)
    {
        SwitchComposerToPreviewMode();
    }

    private void SwitchComposerToEditMode()
    {
        _isPreviewTabActive = false;
        if (ComposerEditArea != null) ComposerEditArea.Visibility = Visibility.Visible;
        ComposerContentInput.Visibility = Visibility.Visible;
        ComposerPreviewContainer.Visibility = Visibility.Collapsed;
        ComposerEditTabBtn.FontWeight = FontWeights.Bold;
        ComposerPreviewTabBtn.FontWeight = FontWeights.Normal;
        ComposerContentInput.Focus();
        RebuildThumbnailsAsync();
    }

    private void SwitchComposerToPreviewMode()
    {
        _isPreviewTabActive = true;
        if (ComposerEditArea != null) ComposerEditArea.Visibility = Visibility.Collapsed;
        ComposerContentInput.Visibility = Visibility.Collapsed;
        ComposerPreviewContainer.Visibility = Visibility.Visible;
        ComposerEditTabBtn.FontWeight = FontWeights.Normal;
        ComposerPreviewTabBtn.FontWeight = FontWeights.Bold;
        NoteMarkdownRenderer.RenderMarkdownToPanel(ComposerPreviewPanel, ComposerContentInput.Text, this, null);
    }

    private void ToggleComposerPreview()
    {
        if (_isPreviewTabActive)
        {
            SwitchComposerToEditMode();
        }
        else
        {
            SwitchComposerToPreviewMode();
        }
    }

    // ==========================================
    // RIGHT-CLICK & TOOLBAR PRESET ACTIONS
    // ==========================================
    private void InsertTask_Click(object sender, RoutedEventArgs e) => WrapOrInsert("\n- [ ] ");
    private void InsertBullet_Click(object sender, RoutedEventArgs e) => WrapOrInsert("\n- ");
    private void InsertNumbered_Click(object sender, RoutedEventArgs e) => WrapOrInsert("\n1. ");
    private void InsertH1_Click(object sender, RoutedEventArgs e) => WrapOrInsert("\n# ");
    private void InsertH2_Click(object sender, RoutedEventArgs e) => WrapOrInsert("\n## ");
    private void InsertH3_Click(object sender, RoutedEventArgs e) => WrapOrInsert("\n### ");
    private void InsertBold_Click(object sender, RoutedEventArgs e) => WrapOrInsert("**", "**");
    private void InsertItalic_Click(object sender, RoutedEventArgs e) => WrapOrInsert("*", "*");
    private void InsertStrikethrough_Click(object sender, RoutedEventArgs e) => WrapOrInsert("~~", "~~");
    private void InsertHighlight_Click(object sender, RoutedEventArgs e) => WrapOrInsert("==", "==");
    private void InsertCode_Click(object sender, RoutedEventArgs e) => WrapOrInsert("`", "`");
    private void InsertQuote_Click(object sender, RoutedEventArgs e) => WrapOrInsert("\n> ");

    private void WrapOrInsert(string prefix, string suffix = "")
    {
        if (ComposerContentInput.SelectionLength > 0)
        {
            string selected = ComposerContentInput.SelectedText;
            ComposerContentInput.SelectedText = $"{prefix}{selected}{suffix}";
        }
        else
        {
            InsertAtCursor($"{prefix}{suffix}");
            if (!string.IsNullOrEmpty(suffix))
            {
                ComposerContentInput.SelectionStart = Math.Max(0, ComposerContentInput.SelectionStart - suffix.Length);
            }
        }
        ComposerContentInput.Focus();
    }

    public void InsertAtCursor(string text)
    {
        int caret = ComposerContentInput.SelectionStart;
        ComposerContentInput.Text = ComposerContentInput.Text.Insert(caret, text);
        ComposerContentInput.SelectionStart = caret + text.Length;
        ComposerContentInput.Focus();
    }

    private void ComposerContentInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl + Enter: Save Note
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SaveComposer_Click(null, null);
            e.Handled = true;
            return;
        }

        // Ctrl + P: Toggle Live Preview
        if (e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            ToggleComposerPreview();
            e.Handled = true;
            return;
        }

        // Ctrl + B: Bold
        if (e.Key == Key.B && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            WrapOrInsert("**", "**");
            e.Handled = true;
            return;
        }

        // Ctrl + I: Italic
        if (e.Key == Key.I && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            WrapOrInsert("*", "*");
            e.Handled = true;
            return;
        }

        // Ctrl + Shift + T: Task
        if (e.Key == Key.T && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            WrapOrInsert("\n- [ ] ");
            e.Handled = true;
            return;
        }

        // Ctrl + V: Paste (handle image attachments)
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (NotesAttachmentManager.TryHandleImagePaste(out string? imageMarkdown) && !string.IsNullOrEmpty(imageMarkdown))
            {
                InsertAtCursor(imageMarkdown);
                e.Handled = true;
                return;
            }
        }

        // Smart typing for lists, checkboxes, headings
        if (sender is TextBox textBox)
        {
            NoteSmartTypingHelper.HandlePreviewKeyDown(textBox, e);
        }
    }

    private void OnComposerContentPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (NotesAttachmentManager.TryHandleImagePaste(out string? imageMarkdown) && !string.IsNullOrEmpty(imageMarkdown))
        {
            InsertAtCursor(imageMarkdown);
            e.CancelCommand();
            e.Handled = true;
        }
    }

    private void ComposerContentInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SaveComposer_Click(null, null);
            e.Handled = true;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // Ignore if mouse was immediately released
            }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            if (ComposerPanel.Visibility == Visibility.Visible)
            {
                CancelComposer_Click(this, new RoutedEventArgs());
            }
            else
            {
                Hide();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            Hide();
            e.Handled = true;
        }
    }

    // ==========================================
    // PERSISTENT NOTES VAULT & OPEN TRACKING
    // ==========================================
    private static int? _cachedQuickNotesOpenCount;
    private static bool _hasIncrementedThisSession;

    public static int QuickNotesOpenCount => GetQuickNotesOpenCount();

    private void RecordOpenAndCheckHintBadge()
    {
        int count = GetOrIncrementQuickNotesOpenCount();
        if (QuickNotesToggleHintBadge != null)
        {
            QuickNotesToggleHintBadge.Visibility = count >= 3 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public static int GetQuickNotesOpenCount()
    {
        if (_cachedQuickNotesOpenCount.HasValue)
        {
            return _cachedQuickNotesOpenCount.Value;
        }

        int count = ReadCountFromDisk();
        _cachedQuickNotesOpenCount = count;
        return count;
    }

    public static int GetOrIncrementQuickNotesOpenCount()
    {
        int current = GetQuickNotesOpenCount();
        if (current >= 3)
        {
            return current;
        }

        if (!_hasIncrementedThisSession)
        {
            _hasIncrementedThisSession = true;
            current++;
            _cachedQuickNotesOpenCount = current;
            Task.Run(() => WriteCountToDisk(current));
        }

        return current;
    }

    public static int IncrementAndGetQuickNotesOpenCount()
    {
        return GetOrIncrementQuickNotesOpenCount();
    }

    private static int ReadCountFromDisk()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string settingsPath = System.IO.Path.Combine(localAppData, "Blocker", "ui-settings.json");
            if (System.IO.File.Exists(settingsPath))
            {
                string json = System.IO.File.ReadAllText(settingsPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("QuickNotesOpenCount", out var prop) && prop.TryGetInt32(out int val))
                {
                    return val;
                }
            }

            string altPath = System.IO.Path.Combine(localAppData, "Sankalpa", "notes", "quick_notes_count.json");
            if (System.IO.File.Exists(altPath))
            {
                string txt = System.IO.File.ReadAllText(altPath).Trim();
                if (int.TryParse(txt, out int val)) return val;
            }
        }
        catch { }
        return 0;
    }

    private static void WriteCountToDisk(int count)
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string notesDir = System.IO.Path.Combine(localAppData, "Sankalpa", "notes");
            if (!System.IO.Directory.Exists(notesDir)) System.IO.Directory.CreateDirectory(notesDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(notesDir, "quick_notes_count.json"), count.ToString());

            string settingsPath = System.IO.Path.Combine(localAppData, "Blocker", "ui-settings.json");
            if (System.IO.File.Exists(settingsPath))
            {
                string json = System.IO.File.ReadAllText(settingsPath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
                dict["QuickNotesOpenCount"] = count;
                System.IO.File.WriteAllText(settingsPath, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { }
    }

    public static string GetNotesVaultPath() => NotesVaultStore.GetNotesVaultPath();

    public static List<NoteCard> LoadNotesFromVault() => NotesVaultStore.LoadNotesFromVault();

    public static void SaveNotesToVault(IEnumerable<NoteCard> cards) => NotesVaultStore.QueueSave(cards);

    // ==========================================
    // IN-EDIT-MODE SCREENSHOT PREVIEWS
    // ==========================================
    public static List<string> ExtractImageReferences(string? text)
    {
        return NotesAttachmentManager.ExtractImageReferences(text);
    }

    private void RebuildThumbnailsAsync()
    {
        if (EditModeImagePreviewStrip == null || EditModeImageThumbnailsPanel == null) return;

        string text = ComposerContentInput?.Text ?? string.Empty;
        var imageRefs = ExtractImageReferences(text);

        if (imageRefs.SequenceEqual(_currentThumbRefs))
        {
            return;
        }
        _currentThumbRefs = imageRefs;

        if (imageRefs.Count == 0)
        {
            EditModeImagePreviewStrip.Visibility = Visibility.Collapsed;
            EditModeImageThumbnailsPanel.Children.Clear();
            return;
        }

        EditModeImageThumbnailsPanel.Children.Clear();
        foreach (var imgRef in imageRefs)
        {
            var thumbBorder = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(1),
                Background = (Brush)FindResource("SurfaceRaised"),
                Padding = new Thickness(2),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = System.IO.Path.GetFileName(imgRef),
                Height = 60
            };

            var cached = NotesAttachmentManager.LoadBitmapSafely(imgRef, 120);
            if (cached != null)
            {
                var img = new System.Windows.Controls.Image
                {
                    Source = cached,
                    Height = 54,
                    MaxWidth = 100,
                    Stretch = Stretch.Uniform
                };
                thumbBorder.Child = img;
            }
            else
            {
                var fallback = new TextBlock
                {
                    Text = $"🖼 {System.IO.Path.GetFileName(imgRef)}",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4)
                };
                thumbBorder.Child = fallback;

                NotesAttachmentManager.LoadBitmapSafelyAsync(imgRef, 120).ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result != null)
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            var img = new System.Windows.Controls.Image
                            {
                                Source = t.Result,
                                Height = 54,
                                MaxWidth = 100,
                                Stretch = Stretch.Uniform
                            };
                            thumbBorder.Child = img;
                        });
                    }
                });
            }

            EditModeImageThumbnailsPanel.Children.Add(thumbBorder);
        }

        EditModeImagePreviewStrip.Visibility = Visibility.Visible;
    }
}
