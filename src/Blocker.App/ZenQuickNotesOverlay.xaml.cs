using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
using Orientation = System.Windows.Controls.Orientation;
using Rectangle = System.Windows.Shapes.Rectangle;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Cursors = System.Windows.Input.Cursors;

namespace Blocker.App;

/// <summary>
/// A floating, draggable topmost notes flyout window that users can summon while using whitelisted applications.
/// Supports inline markdown rendering, interactive checkable items (- [ ]), and auto-saving back to the active session.
/// </summary>
public partial class ZenQuickNotesOverlay : Window
{
    private readonly List<NoteCard> _cards = new();
    private NoteCard? _editingCard;
    private readonly DispatcherTimer _autoSaveNoticeTimer;

    public Action<string>? OnNotesChanged { get; set; }

    public ZenQuickNotesOverlay()
    {
        InitializeComponent();

        _autoSaveNoticeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _autoSaveNoticeTimer.Tick += (s, e) =>
        {
            _autoSaveNoticeTimer.Stop();
            AutoSaveStatusText.Text = "✓ Auto-saved";
            AutoSaveStatusText.Foreground = (Brush)FindResource("Success");
            AutoSaveIcon.Stroke = (Brush)FindResource("Success");
        };
    }

    /// <summary>
    /// Loads reflection notes (JSON NoteCardCollection array or plain text) into the card view.
    /// </summary>
    public void LoadNotes(string? notesData)
    {
        _cards.Clear();
        var deserialized = NoteCardCollection.Deserialize(notesData);
        _cards.AddRange(deserialized);

        // Switch to cards view and render
        ComposerPanel.Visibility = Visibility.Collapsed;
        CardsViewPanel.Visibility = Visibility.Visible;
        RenderCards();
    }

    /// <summary>
    /// Renders the cards in the collection or shows the empty state.
    /// </summary>
    private void RenderCards()
    {
        CardsItemsPanel.Children.Clear();

        if (_cards.Count == 0)
        {
            EmptyStatePanel.Visibility = Visibility.Visible;
            CardsScrollViewer.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        CardsScrollViewer.Visibility = Visibility.Visible;

        for (int i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            var cardBorder = CreateCardElement(card, i);
            CardsItemsPanel.Children.Add(cardBorder);
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
            Margin = new Thickness(0, 0, 0, 10)
        };

        var cardStack = new StackPanel();

        // 1. Header: Title, Timestamp, Edit and Delete buttons
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string titleDisplay = string.IsNullOrWhiteSpace(card.Title) ? $"Reflection {cardIndex + 1}" : card.Title;
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
        RenderMarkdownBody(bodyPanel, card);
        cardStack.Children.Add(bodyPanel);

        cardBorder.Child = cardStack;
        return cardBorder;
    }

    /// <summary>
    /// Parses and renders markdown text into WPF elements, including interactive - [ ] checkboxes.
    /// </summary>
    private void RenderMarkdownBody(StackPanel bodyPanel, NoteCard card)
    {
        if (string.IsNullOrWhiteSpace(card.MarkdownContent))
        {
            var emptyText = new TextBlock
            {
                Text = "(No content)",
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                Foreground = (Brush)FindResource("TextDisabled")
            };
            bodyPanel.Children.Add(emptyText);
            return;
        }

        string[] lines = card.MarkdownContent.Replace("\r", "").Split('\n');
        int taskCheckboxCount = 0;
        var checkboxRegex = new Regex(@"^(\s*[-*]\s*\[)([ xX])(\])(.*)$");

        foreach (var rawLine in lines)
        {
            var taskMatch = checkboxRegex.Match(rawLine);
            if (taskMatch.Success)
            {
                // Checkbox task item
                int currentTaskIndex = taskCheckboxCount++;
                bool isChecked = taskMatch.Groups[2].Value.Equals("x", StringComparison.OrdinalIgnoreCase);
                string taskDescription = taskMatch.Groups[4].Value.TrimStart();

                var cb = new CheckBox
                {
                    IsChecked = isChecked,
                    Margin = new Thickness(0, 3, 0, 3),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand
                };

                var taskLabel = new TextBlock
                {
                    FontSize = 13,
                    FontFamily = (FontFamily)FindResource("FontBody"),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = isChecked ? (Brush)FindResource("TextDisabled") : (Brush)FindResource("TextPrimary"),
                    TextDecorations = isChecked ? TextDecorations.Strikethrough : null
                };
                AddFormattedInlines(taskLabel.Inlines, taskDescription);
                cb.Content = taskLabel;

                cb.Click += (s, e) =>
                {
                    bool newChecked = cb.IsChecked == true;
                    card.ToggleTaskCheckbox(currentTaskIndex, newChecked);
                    taskLabel.Foreground = newChecked ? (Brush)FindResource("TextDisabled") : (Brush)FindResource("TextPrimary");
                    taskLabel.TextDecorations = newChecked ? TextDecorations.Strikethrough : null;
                    SaveActiveNotes();
                };

                bodyPanel.Children.Add(cb);
            }
            else if (rawLine.StartsWith("### "))
            {
                var h3 = new TextBlock
                {
                    Text = rawLine[4..].Trim(),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    Margin = new Thickness(0, 5, 0, 2)
                };
                bodyPanel.Children.Add(h3);
            }
            else if (rawLine.StartsWith("## "))
            {
                var h2 = new TextBlock
                {
                    Text = rawLine[3..].Trim(),
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Foreground = (Brush)FindResource("TextPrimary"),
                    Margin = new Thickness(0, 6, 0, 2)
                };
                bodyPanel.Children.Add(h2);
            }
            else if (rawLine.StartsWith("# "))
            {
                var h1 = new TextBlock
                {
                    Text = rawLine[2..].Trim(),
                    FontWeight = FontWeights.Bold,
                    FontSize = 15,
                    Foreground = (Brush)FindResource("TextPrimary"),
                    Margin = new Thickness(0, 7, 0, 3)
                };
                bodyPanel.Children.Add(h1);
            }
            else if (rawLine.StartsWith("- ") || rawLine.StartsWith("* "))
            {
                var bulletBlock = new TextBlock
                {
                    FontSize = 13,
                    FontFamily = (FontFamily)FindResource("FontBody"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 2, 0, 2)
                };
                bulletBlock.Inlines.Add(new Run("• ")
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("Accent")
                });
                AddFormattedInlines(bulletBlock.Inlines, rawLine[2..].TrimStart());
                bodyPanel.Children.Add(bulletBlock);
            }
            else if (string.IsNullOrWhiteSpace(rawLine))
            {
                // Small paragraph spacing
                var spacer = new Rectangle { Height = 4 };
                bodyPanel.Children.Add(spacer);
            }
            else
            {
                var paraBlock = new TextBlock
                {
                    FontSize = 13,
                    FontFamily = (FontFamily)FindResource("FontBody"),
                    Foreground = (Brush)FindResource("TextPrimary"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                AddFormattedInlines(paraBlock.Inlines, rawLine);
                bodyPanel.Children.Add(paraBlock);
            }
        }
    }

    /// <summary>
    /// Parses inline bold (**text**), italic (*text*), and monospace (`code`) markdown into Runs.
    /// </summary>
    private void AddFormattedInlines(InlineCollection inlines, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var fontMono = (FontFamily)FindResource("FontMono");
        var surfaceRaised = (Brush)FindResource("SurfaceRaised");
        var textPrimary = (Brush)FindResource("TextPrimary");

        var regex = new Regex(@"(\*\*(.*?)\*\*|\*(.*?)\*|`(.*?)`)");
        int lastIndex = 0;

        foreach (Match m in regex.Matches(text))
        {
            if (m.Index > lastIndex)
            {
                inlines.Add(new Run(text.Substring(lastIndex, m.Index - lastIndex)) { Foreground = textPrimary });
            }

            if (m.Value.StartsWith("**"))
            {
                inlines.Add(new Run(m.Groups[2].Value) { FontWeight = FontWeights.Bold, Foreground = textPrimary });
            }
            else if (m.Value.StartsWith("*"))
            {
                inlines.Add(new Run(m.Groups[3].Value) { FontStyle = FontStyles.Italic, Foreground = textPrimary });
            }
            else if (m.Value.StartsWith("`"))
            {
                inlines.Add(new Run($" {m.Groups[4].Value} ")
                {
                    FontFamily = fontMono,
                    Background = surfaceRaised,
                    Foreground = textPrimary
                });
            }

            lastIndex = m.Index + m.Length;
        }

        if (lastIndex < text.Length)
        {
            inlines.Add(new Run(text.Substring(lastIndex)) { Foreground = textPrimary });
        }
    }

    /// <summary>
    /// Serializes active cards and invokes the auto-save change callback.
    /// </summary>
    private void SaveActiveNotes()
    {
        string serialized = NoteCardCollection.Serialize(_cards);

        AutoSaveStatusText.Text = "Saving...";
        AutoSaveStatusText.Foreground = (Brush)FindResource("TextSecondary");
        AutoSaveIcon.Stroke = (Brush)FindResource("TextSecondary");

        _autoSaveNoticeTimer.Stop();
        _autoSaveNoticeTimer.Start();

        OnNotesChanged?.Invoke(serialized);
    }

    private void EditNote(NoteCard card)
    {
        _editingCard = card;
        ComposerHeadingText.Text = "Edit Note";
        ComposerTitleInput.Text = card.Title;
        ComposerContentInput.Text = card.MarkdownContent;

        CardsViewPanel.Visibility = Visibility.Collapsed;
        ComposerPanel.Visibility = Visibility.Visible;
        ComposerContentInput.Focus();
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
        ComposerHeadingText.Text = "New Reflection Note";
        ComposerTitleInput.Text = string.Empty;
        ComposerContentInput.Text = string.Empty;

        CardsViewPanel.Visibility = Visibility.Collapsed;
        ComposerPanel.Visibility = Visibility.Visible;
        ComposerTitleInput.Focus();
    }

    private void SaveComposer_Click(object? sender, RoutedEventArgs? e)
    {
        string title = ComposerTitleInput.Text.Trim();
        string content = ComposerContentInput.Text;

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

    private void InsertTask_Click(object sender, RoutedEventArgs e)
    {
        InsertAtCursor("\n- [ ] ");
    }

    private void InsertBullet_Click(object sender, RoutedEventArgs e)
    {
        InsertAtCursor("\n- ");
    }

    private void InsertBold_Click(object sender, RoutedEventArgs e)
    {
        if (ComposerContentInput.SelectionLength > 0)
        {
            string selected = ComposerContentInput.SelectedText;
            ComposerContentInput.SelectedText = $"**{selected}**";
        }
        else
        {
            InsertAtCursor("**bold**");
        }
    }

    private void InsertHeading_Click(object sender, RoutedEventArgs e)
    {
        InsertAtCursor("\n## ");
    }

    private void InsertAtCursor(string text)
    {
        int caret = ComposerContentInput.SelectionStart;
        ComposerContentInput.Text = ComposerContentInput.Text.Insert(caret, text);
        ComposerContentInput.SelectionStart = caret + text.Length;
        ComposerContentInput.Focus();
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
}
