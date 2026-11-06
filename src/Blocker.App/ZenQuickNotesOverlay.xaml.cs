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
/// </summary>
public partial class ZenQuickNotesOverlay : Window
{
    private readonly List<NoteCard> _cards = new();
    private NoteCard? _editingCard;
    private readonly DispatcherTimer _autoSaveNoticeTimer;
    private bool _isPreviewTabActive;

    public Action<string>? OnNotesChanged { get; set; }

    public ZenQuickNotesOverlay()
    {
        InitializeComponent();

        if (ComposerContentInput != null)
        {
            ComposerContentInput.PreviewKeyDown += ComposerContentInput_PreviewKeyDown;
            ComposerContentInput.TextChanged += (s, e) => UpdateInEditScreenshotPreviews();
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
    /// </summary>
    public void LoadNotes(string? notesData)
    {
        _cards.Clear();
        var deserialized = NoteCardCollection.Deserialize(notesData);
        if (deserialized.Count == 0)
        {
            deserialized = LoadNotesFromVault();
        }
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
        RenderMarkdownToPanel(bodyPanel, card.MarkdownContent, (taskIndex, isChecked) =>
        {
            card.ToggleTaskCheckbox(taskIndex, isChecked);
            SaveActiveNotes();
        });
        cardStack.Children.Add(bodyPanel);

        cardBorder.Child = cardStack;
        return cardBorder;
    }

    /// <summary>
    /// Parses and renders markdown text into WPF elements.
    /// Supports:
    /// - Interactive task checkboxes (- [ ], - [x], • [ ], • [x], [ ], [x], ☐, ☑)
    /// - Headings: # H1, ## H2, ### H3, #### H4
    /// - Numbered lists (1. , 2. )
    /// - Unordered bullets (- , * , • )
    /// - Blockquotes (> )
    /// - Horizontal rules (---, ***)
    /// - Inline formatting: Bold (**text**), Italic (*text*), Strikethrough (~~text~~), Highlight (==text==), Code (`code`), Tags (#tag)
    /// </summary>
    public void RenderMarkdownToPanel(StackPanel bodyPanel, string markdown, Action<int, bool>? onTaskToggled = null)
    {
        bodyPanel.Children.Clear();

        if (string.IsNullOrWhiteSpace(markdown))
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

        string[] lines = markdown.Replace("\r", "").Split('\n');
        int taskCheckboxCount = 0;
        var taskRegex = new Regex(@"^\s*(?:[-*•]\s*)?(?:\[([ xX])\]|([☐☑]))\s*(.*)$");
        var numListRegex = new Regex(@"^\s*(\d+)\.\s+(.*)$");
        var wikiImgRegex = new Regex(@"!\[\[(.*?)\]\]");
        var mdImgRegex = new Regex(@"!\[(.*?)\]\((.*?)\)");

        foreach (var rawLine in lines)
        {
            string trimmedStart = rawLine.TrimStart();

            // Image tag: ![[image.png]] or ![alt](path)
            var wikiImgMatch = wikiImgRegex.Match(trimmedStart);
            var mdImgMatch = mdImgRegex.Match(trimmedStart);

            if (wikiImgMatch.Success)
            {
                string before = trimmedStart.Substring(0, wikiImgMatch.Index).Trim();
                if (!string.IsNullOrEmpty(before))
                {
                    var paraBefore = new TextBlock
                    {
                        FontSize = 13,
                        FontFamily = (FontFamily)FindResource("FontBody"),
                        Foreground = (Brush)FindResource("TextPrimary"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    AddFormattedInlines(paraBefore.Inlines, before);
                    bodyPanel.Children.Add(paraBefore);
                }

                string target = wikiImgMatch.Groups[1].Value.Trim();
                string imgRef = target.Contains('|') ? target.Split('|')[0].Trim() : target;
                var imgControl = CreateImageBorder(imgRef);
                if (imgControl != null)
                {
                    bodyPanel.Children.Add(imgControl);
                }

                string after = trimmedStart.Substring(wikiImgMatch.Index + wikiImgMatch.Length).Trim();
                if (!string.IsNullOrEmpty(after))
                {
                    var paraAfter = new TextBlock
                    {
                        FontSize = 13,
                        FontFamily = (FontFamily)FindResource("FontBody"),
                        Foreground = (Brush)FindResource("TextPrimary"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    AddFormattedInlines(paraAfter.Inlines, after);
                    bodyPanel.Children.Add(paraAfter);
                }
                continue;
            }
            else if (mdImgMatch.Success)
            {
                string before = trimmedStart.Substring(0, mdImgMatch.Index).Trim();
                if (!string.IsNullOrEmpty(before))
                {
                    var paraBefore = new TextBlock
                    {
                        FontSize = 13,
                        FontFamily = (FontFamily)FindResource("FontBody"),
                        Foreground = (Brush)FindResource("TextPrimary"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    AddFormattedInlines(paraBefore.Inlines, before);
                    bodyPanel.Children.Add(paraBefore);
                }

                string imgRef = mdImgMatch.Groups[2].Value.Trim();
                var imgControl = CreateImageBorder(imgRef);
                if (imgControl != null)
                {
                    bodyPanel.Children.Add(imgControl);
                }

                string after = trimmedStart.Substring(mdImgMatch.Index + mdImgMatch.Length).Trim();
                if (!string.IsNullOrEmpty(after))
                {
                    var paraAfter = new TextBlock
                    {
                        FontSize = 13,
                        FontFamily = (FontFamily)FindResource("FontBody"),
                        Foreground = (Brush)FindResource("TextPrimary"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    AddFormattedInlines(paraAfter.Inlines, after);
                    bodyPanel.Children.Add(paraAfter);
                }
                continue;
            }

            // 1. Task Checkbox item
            var taskMatch = taskRegex.Match(rawLine);
            if (taskMatch.Success)
            {
                int currentTaskIndex = taskCheckboxCount++;
                bool isChecked = taskMatch.Groups[1].Success
                    ? taskMatch.Groups[1].Value.Trim().Equals("x", StringComparison.OrdinalIgnoreCase)
                    : (taskMatch.Groups[2].Value == "☑");
                string taskDescription = taskMatch.Groups[3].Value.Trim();

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
                    taskLabel.Foreground = newChecked ? (Brush)FindResource("TextDisabled") : (Brush)FindResource("TextPrimary");
                    taskLabel.TextDecorations = newChecked ? TextDecorations.Strikethrough : null;
                    onTaskToggled?.Invoke(currentTaskIndex, newChecked);
                };

                bodyPanel.Children.Add(cb);
            }
            // 2. Heading 4 (#### )
            else if (trimmedStart.StartsWith("#### ") || trimmedStart.StartsWith("####"))
            {
                string headerText = trimmedStart.TrimStart('#').Trim();
                var h4 = new TextBlock
                {
                    Text = headerText,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12.5,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    Margin = new Thickness(0, 4, 0, 2)
                };
                bodyPanel.Children.Add(h4);
            }
            // 3. Heading 3 (### )
            else if (trimmedStart.StartsWith("### ") || trimmedStart.StartsWith("###"))
            {
                string headerText = trimmedStart.TrimStart('#').Trim();
                var h3 = new TextBlock
                {
                    Text = headerText,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 14,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    Margin = new Thickness(0, 5, 0, 2)
                };
                bodyPanel.Children.Add(h3);
            }
            // 4. Heading 2 (## )
            else if (trimmedStart.StartsWith("## ") || trimmedStart.StartsWith("##"))
            {
                string headerText = trimmedStart.TrimStart('#').Trim();
                var h2 = new TextBlock
                {
                    Text = headerText,
                    FontWeight = FontWeights.Bold,
                    FontSize = 15.5,
                    Foreground = (Brush)FindResource("TextPrimary"),
                    Margin = new Thickness(0, 6, 0, 3)
                };
                bodyPanel.Children.Add(h2);
            }
            // 5. Heading 1 (# )
            else if (trimmedStart.StartsWith("# ") || trimmedStart == "#")
            {
                string headerText = trimmedStart.TrimStart('#').Trim();
                var h1 = new TextBlock
                {
                    Text = headerText,
                    FontWeight = FontWeights.Bold,
                    FontSize = 18,
                    Foreground = (Brush)FindResource("TextPrimary"),
                    Margin = new Thickness(0, 8, 0, 4)
                };
                bodyPanel.Children.Add(h1);
            }
            // 6. Numbered list items (1. , 2. )
            else if (numListRegex.Match(rawLine) is Match nm && nm.Success)
            {
                var numBlock = new TextBlock
                {
                    FontSize = 13,
                    FontFamily = (FontFamily)FindResource("FontBody"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 2, 0, 2)
                };
                numBlock.Inlines.Add(new Run($"{nm.Groups[1].Value}. ")
                {
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("Accent")
                });
                AddFormattedInlines(numBlock.Inlines, nm.Groups[2].Value.Trim());
                bodyPanel.Children.Add(numBlock);
            }
            // 7. Unordered Bullet list items
            else if (trimmedStart.StartsWith("• ") || trimmedStart.StartsWith("- ") || trimmedStart.StartsWith("* "))
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
                AddFormattedInlines(bulletBlock.Inlines, trimmedStart[2..].TrimStart());
                bodyPanel.Children.Add(bulletBlock);
            }
            // 8. Blockquotes (> )
            else if (trimmedStart.StartsWith("> ") || trimmedStart == ">")
            {
                string quoteText = trimmedStart.Length > 2 ? trimmedStart[2..].Trim() : string.Empty;
                var quoteBorder = new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    BorderBrush = (Brush)FindResource("Accent"),
                    Background = (Brush)FindResource("SurfaceRaised"),
                    CornerRadius = new CornerRadius(0, 4, 4, 0),
                    Padding = new Thickness(10, 4, 8, 4),
                    Margin = new Thickness(0, 4, 0, 4)
                };
                var quoteBlock = new TextBlock
                {
                    FontSize = 13,
                    FontStyle = FontStyles.Italic,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    TextWrapping = TextWrapping.Wrap
                };
                AddFormattedInlines(quoteBlock.Inlines, quoteText);
                quoteBorder.Child = quoteBlock;
                bodyPanel.Children.Add(quoteBorder);
            }
            // 9. Horizontal Divider (---, ***, ___)
            else if (trimmedStart == "---" || trimmedStart == "***" || trimmedStart == "___")
            {
                var hr = new Rectangle
                {
                    Height = 1,
                    Fill = (Brush)FindResource("Border"),
                    Margin = new Thickness(0, 8, 0, 8)
                };
                bodyPanel.Children.Add(hr);
            }
            // 10. Empty line spacing
            else if (string.IsNullOrWhiteSpace(rawLine))
            {
                var spacer = new Rectangle { Height = 4 };
                bodyPanel.Children.Add(spacer);
            }
            // 11. Normal paragraph text
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

    private UIElement? CreateImageBorder(string imgRef)
    {
        var bitmap = NotesAttachmentManager.LoadBitmapSafely(imgRef);
        if (bitmap != null)
        {
            var image = new System.Windows.Controls.Image
            {
                Source = bitmap,
                MaxHeight = 240,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left
            };

            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(1),
                Background = (Brush)FindResource("SurfaceRaised"),
                Padding = new Thickness(2),
                Margin = new Thickness(0, 4, 0, 6),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Child = image
            };
            return border;
        }

        var fallbackBlock = new TextBlock
        {
            Text = $"[Image: {System.IO.Path.GetFileName(imgRef)}]",
            FontSize = 12,
            FontStyle = FontStyles.Italic,
            Foreground = (Brush)FindResource("TextDisabled"),
            Margin = new Thickness(0, 2, 0, 4)
        };
        return fallbackBlock;
    }

    /// <summary>
    /// Parses inline Markdown:
    /// - Bold (**text** or __text__)
    /// - Italic (*text* or _text_)
    /// - Strikethrough (~~text~~)
    /// - Highlight (==text==)
    /// - Monospace Code (`code`)
    /// - Wikilinks ([[Note]])
    /// - Tags (#tag)
    /// </summary>
    private void AddFormattedInlines(InlineCollection inlines, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var fontMono = (FontFamily)FindResource("FontMono");
        var surfaceRaised = (Brush)FindResource("SurfaceRaised");
        var textPrimary = (Brush)FindResource("TextPrimary");
        var accentBrush = (Brush)FindResource("Accent");
        var warningBrush = (Brush)FindResource("Warning");

        // Regex capturing bold/italic, strikethrough, highlight, inline code, wikilinks, tags
        var regex = new Regex(@"(\*\*\*(.*?)\*\*\*|\*\*(.*?)\*\*|\*(.*?)\*|~~(.*?)~~|==(.*?)==|`(.*?)`|\[\[(.*?)\]\]|(?<=\s|^)(#[a-zA-Z0-9_\-]+))");
        int lastIndex = 0;

        foreach (Match m in regex.Matches(text))
        {
            if (m.Index > lastIndex)
            {
                inlines.Add(new Run(text.Substring(lastIndex, m.Index - lastIndex)) { Foreground = textPrimary });
            }

            string val = m.Value;
            if (val.StartsWith("***") && val.EndsWith("***"))
            {
                inlines.Add(new Run(m.Groups[2].Value)
                {
                    FontWeight = FontWeights.Bold,
                    FontStyle = FontStyles.Italic,
                    Foreground = textPrimary
                });
            }
            else if (val.StartsWith("**") && val.EndsWith("**"))
            {
                inlines.Add(new Run(m.Groups[3].Value)
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = textPrimary
                });
            }
            else if (val.StartsWith("*") && val.EndsWith("*"))
            {
                inlines.Add(new Run(m.Groups[4].Value)
                {
                    FontStyle = FontStyles.Italic,
                    Foreground = textPrimary
                });
            }
            else if (val.StartsWith("~~") && val.EndsWith("~~"))
            {
                inlines.Add(new Run(m.Groups[5].Value)
                {
                    TextDecorations = TextDecorations.Strikethrough,
                    Foreground = (Brush)FindResource("TextDisabled")
                });
            }
            else if (val.StartsWith("==") && val.EndsWith("=="))
            {
                // Highlight style: amber background pill
                inlines.Add(new Run($" {m.Groups[6].Value} ")
                {
                    Background = warningBrush,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 0, 0)),
                    FontWeight = FontWeights.SemiBold
                });
            }
            else if (val.StartsWith("`") && val.EndsWith("`"))
            {
                inlines.Add(new Run($" {m.Groups[7].Value} ")
                {
                    FontFamily = fontMono,
                    Background = surfaceRaised,
                    Foreground = textPrimary
                });
            }
            else if (val.StartsWith("[[") && val.EndsWith("]]"))
            {
                // Wikilink
                inlines.Add(new Run(m.Groups[8].Value)
                {
                    Foreground = accentBrush,
                    TextDecorations = TextDecorations.Underline
                });
            }
            else if (val.StartsWith("#"))
            {
                // Tag
                inlines.Add(new Run(val)
                {
                    Foreground = accentBrush,
                    FontWeight = FontWeights.SemiBold
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
        string serialized = _cards.Count == 0 ? string.Empty : NoteCardCollection.Serialize(_cards);

        AutoSaveStatusText.Text = "Saving...";
        AutoSaveStatusText.Foreground = (Brush)FindResource("TextSecondary");
        AutoSaveIcon.Stroke = (Brush)FindResource("TextSecondary");

        _autoSaveNoticeTimer.Stop();
        _autoSaveNoticeTimer.Start();

        SaveNotesToVault(_cards);

        OnNotesChanged?.Invoke(serialized);
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
        UpdateInEditScreenshotPreviews();
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
        UpdateInEditScreenshotPreviews();
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
        UpdateInEditScreenshotPreviews();
    }

    private void SwitchComposerToPreviewMode()
    {
        _isPreviewTabActive = true;
        if (ComposerEditArea != null) ComposerEditArea.Visibility = Visibility.Collapsed;
        ComposerContentInput.Visibility = Visibility.Collapsed;
        ComposerPreviewContainer.Visibility = Visibility.Visible;
        ComposerEditTabBtn.FontWeight = FontWeights.Normal;
        ComposerPreviewTabBtn.FontWeight = FontWeights.Bold;
        RenderMarkdownToPanel(ComposerPreviewPanel, ComposerContentInput.Text, null);
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

    private void InsertAtCursor(string text)
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
    public static int QuickNotesOpenCount => GetQuickNotesOpenCount();

    private void RecordOpenAndCheckHintBadge()
    {
        int count = IncrementAndGetQuickNotesOpenCount();
        if (QuickNotesToggleHintBadge != null)
        {
            QuickNotesToggleHintBadge.Visibility = count >= 3 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public static int GetQuickNotesOpenCount()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string settingsPath = System.IO.Path.Combine(localAppData, "Blocker", "ui-settings.json");
            if (System.IO.File.Exists(settingsPath))
            {
                string json = System.IO.File.ReadAllText(settingsPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
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

    public static int IncrementAndGetQuickNotesOpenCount()
    {
        int count = GetQuickNotesOpenCount() + 1;
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
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
                dict["QuickNotesOpenCount"] = count;
                System.IO.File.WriteAllText(settingsPath, System.Text.Json.JsonSerializer.Serialize(dict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { }
        return count;
    }

    public static string GetNotesVaultPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(localAppData, "Sankalpa", "notes", "active_notes.json");
    }

    public static List<NoteCard> LoadNotesFromVault()
    {
        try
        {
            string path = GetNotesVaultPath();
            if (System.IO.File.Exists(path))
            {
                string json = System.IO.File.ReadAllText(path);
                return NoteCardCollection.Deserialize(json);
            }
        }
        catch { }
        return new List<NoteCard>();
    }

    public static void SaveNotesToVault(IEnumerable<NoteCard> cards)
    {
        try
        {
            string path = GetNotesVaultPath();
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            string json = NoteCardCollection.Serialize(cards.ToList());
            string tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, json);
            System.IO.File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    // ==========================================
    // IN-EDIT-MODE SCREENSHOT PREVIEWS
    // ==========================================
    public static List<string> ExtractImageReferences(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        var wikiMatches = Regex.Matches(text, @"!\[\[(.*?)\]\]");
        foreach (Match m in wikiMatches)
        {
            string refStr = m.Groups[1].Value.Trim();
            if (refStr.Contains('|')) refStr = refStr.Split('|')[0].Trim();
            if (!string.IsNullOrEmpty(refStr) && !list.Contains(refStr))
            {
                list.Add(refStr);
            }
        }

        var mdMatches = Regex.Matches(text, @"!\[(.*?)\]\((.*?)\)");
        foreach (Match m in mdMatches)
        {
            string path = m.Groups[2].Value.Trim();
            if (path.Contains(' ')) path = path.Split(' ')[0].Trim('"', '\'');
            if (!string.IsNullOrEmpty(path) && !list.Contains(path))
            {
                list.Add(path);
            }
        }

        return list;
    }

    private void UpdateInEditScreenshotPreviews()
    {
        if (EditModeImagePreviewStrip == null || EditModeImageThumbnailsPanel == null) return;

        string text = ComposerContentInput?.Text ?? string.Empty;
        var imageRefs = ExtractImageReferences(text);

        if (imageRefs.Count == 0)
        {
            EditModeImagePreviewStrip.Visibility = Visibility.Collapsed;
            EditModeImageThumbnailsPanel.Children.Clear();
            return;
        }

        EditModeImageThumbnailsPanel.Children.Clear();
        foreach (var imgRef in imageRefs)
        {
            var bitmap = NotesAttachmentManager.LoadBitmapSafely(imgRef);
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

            if (bitmap != null)
            {
                var img = new System.Windows.Controls.Image
                {
                    Source = bitmap,
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
            }

            EditModeImageThumbnailsPanel.Children.Add(thumbBorder);
        }

        EditModeImagePreviewStrip.Visibility = Visibility.Visible;
    }
}
