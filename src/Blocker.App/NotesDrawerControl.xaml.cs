using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Blocker.Core;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Rectangle = System.Windows.Shapes.Rectangle;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Cursors = System.Windows.Input.Cursors;
using DataObject = System.Windows.DataObject;
using DispatcherPriority = System.Windows.Threading.DispatcherPriority;

namespace Blocker.App;

public partial class NotesDrawerControl : System.Windows.Controls.UserControl
{
    private bool _isPreviewTabActive;

    public static readonly DependencyProperty IsDrawerOpenProperty =
        DependencyProperty.Register(
            nameof(IsDrawerOpen),
            typeof(bool),
            typeof(NotesDrawerControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsDrawerOpenChanged));

    public bool IsDrawerOpen
    {
        get => (bool)GetValue(IsDrawerOpenProperty);
        set => SetValue(IsDrawerOpenProperty, value);
    }

    public Action? OnSnipRequested { get; set; }

    private void ComposerSnip_Click(object sender, RoutedEventArgs e)
    {
        OnSnipRequested?.Invoke();
    }

    private static void OnIsDrawerOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NotesDrawerControl control && e.NewValue is bool isOpen)
        {
            control.ApplyDrawerState(isOpen);
        }
    }

    private MeditationViewModel? _subscribedVm;

    public NotesDrawerControl()
    {
        InitializeComponent();
        if (MarkdownBox != null)
        {
            MarkdownBox.PreviewKeyDown += MarkdownBox_PreviewKeyDown;
            MarkdownBox.TextChanged += MarkdownBox_TextChanged;
            DataObject.AddPastingHandler(MarkdownBox, OnMarkdownBoxPasting);
        }

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void MarkdownBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl + Enter: Save Note
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (DataContext is MeditationViewModel vm)
            {
                vm.ComposerMarkdown = MarkdownBox?.Text ?? string.Empty;
                if (vm.SaveNoteCardCommand.CanExecute(null))
                {
                    vm.SaveNoteCardCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
            }
        }

        // Esc: Cancel Note Composer
        if (e.Key == Key.Escape)
        {
            if (DataContext is MeditationViewModel vm && vm.IsComposerOpen)
            {
                vm.CancelNoteComposerCommand?.Execute(null);
                e.Handled = true;
                return;
            }
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

        // Ctrl + Shift + S: Snip / Screenshot
        if ((e.Key == Key.S && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift)) || e.Key == Key.Snapshot)
        {
            OnSnipRequested?.Invoke();
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

    private void OnMarkdownBoxPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (NotesAttachmentManager.TryHandleImagePaste(out string? imageMarkdown) && !string.IsNullOrEmpty(imageMarkdown))
        {
            InsertAtCursor(imageMarkdown);
            e.CancelCommand();
            e.Handled = true;
        }
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

    public void FocusComposer()
    {
        SwitchComposerToEditMode();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            SwitchComposerToEditMode();
            if (MarkdownBox != null)
            {
                MarkdownBox.Focus();
                Keyboard.Focus(MarkdownBox);
                MarkdownBox.CaretIndex = MarkdownBox.Text.Length;
            }
        }));
    }

    private void SwitchComposerToEditMode()
    {
        _isPreviewTabActive = false;
        if (ComposerEditArea != null) ComposerEditArea.Visibility = Visibility.Visible;
        if (MarkdownBox != null) MarkdownBox.Visibility = Visibility.Visible;
        if (ComposerPreviewContainer != null) ComposerPreviewContainer.Visibility = Visibility.Collapsed;
        if (ComposerEditTabBtn != null) ComposerEditTabBtn.FontWeight = FontWeights.Bold;
        if (ComposerPreviewTabBtn != null) ComposerPreviewTabBtn.FontWeight = FontWeights.Normal;
        MarkdownBox?.Focus();
        UpdateInEditScreenshotPreviews();
    }

    private void SwitchComposerToPreviewMode()
    {
        _isPreviewTabActive = true;
        if (ComposerEditArea != null) ComposerEditArea.Visibility = Visibility.Collapsed;
        if (MarkdownBox != null) MarkdownBox.Visibility = Visibility.Collapsed;
        if (ComposerPreviewContainer != null) ComposerPreviewContainer.Visibility = Visibility.Visible;
        if (ComposerEditTabBtn != null) ComposerEditTabBtn.FontWeight = FontWeights.Normal;
        if (ComposerPreviewTabBtn != null) ComposerPreviewTabBtn.FontWeight = FontWeights.Bold;
        if (ComposerPreviewPanel != null && MarkdownBox != null)
        {
            RenderMarkdownToPanel(ComposerPreviewPanel, MarkdownBox.Text);
        }
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
        if (MarkdownBox == null) return;

        if (MarkdownBox.SelectionLength > 0)
        {
            string selected = MarkdownBox.SelectedText;
            MarkdownBox.SelectedText = $"{prefix}{selected}{suffix}";
        }
        else
        {
            InsertAtCursor($"{prefix}{suffix}");
            if (!string.IsNullOrEmpty(suffix))
            {
                MarkdownBox.SelectionStart = Math.Max(0, MarkdownBox.SelectionStart - suffix.Length);
            }
        }
        MarkdownBox.Focus();
    }

    public void InsertAtCursor(string text)
    {
        if (MarkdownBox == null) return;
        int caret = MarkdownBox.SelectionStart;
        MarkdownBox.Text = (MarkdownBox.Text ?? string.Empty).Insert(caret, text);
        MarkdownBox.SelectionStart = caret + text.Length;
        MarkdownBox.Focus();
    }

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

    private void AddFormattedInlines(InlineCollection inlines, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var fontMono = (FontFamily)FindResource("FontMono");
        var surfaceRaised = (Brush)FindResource("SurfaceRaised");
        var textPrimary = (Brush)FindResource("TextPrimary");
        var accentBrush = (Brush)FindResource("Accent");
        var warningBrush = (Brush)FindResource("Warning");

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
                inlines.Add(new Run($" {m.Groups[6].Value} ")
                {
                    Background = warningBrush,
                    Foreground = textPrimary,
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
                inlines.Add(new Run(m.Groups[8].Value)
                {
                    Foreground = accentBrush,
                    TextDecorations = TextDecorations.Underline
                });
            }
            else if (val.StartsWith("#"))
            {
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

    private void DrawerTabButton_Click(object sender, RoutedEventArgs e)
    {
        IsDrawerOpen = !IsDrawerOpen;
    }

    private void CloseDrawerButton_Click(object sender, RoutedEventArgs e)
    {
        IsDrawerOpen = false;
    }

    private void ApplyDrawerState(bool open)
    {
        if (TabChevronPath != null)
        {
            TabChevronPath.Data = open
                ? (Geometry)FindResource("IconChevronRight")
                : (Geometry)FindResource("IconChevronLeft");
        }

        if (DrawerContainer == null) return;

        if (open)
        {
            DrawerContainer.Visibility = Visibility.Visible;
        }

        var anim = new DoubleAnimation
        {
            From = DrawerContainer.ActualWidth,
            To = open ? 340.0 : 0.0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        if (!open)
        {
            anim.Completed += (s, e) =>
            {
                if (!IsDrawerOpen)
                {
                    DrawerContainer.Visibility = Visibility.Collapsed;
                    DrawerContainer.Width = 0;
                }
            };
        }

        DrawerContainer.BeginAnimation(WidthProperty, anim);
    }

    // ==========================================
    // PERSISTENT NOTES VAULT & TEXT SYNC
    // ==========================================
    private void MarkdownBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is MeditationViewModel vm && vm.ComposerMarkdown != MarkdownBox.Text)
        {
            vm.ComposerMarkdown = MarkdownBox.Text;
        }
        UpdateInEditScreenshotPreviews();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureVaultNotesLoaded();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm.NotesCollection.CollectionChanged -= OnNotesCollectionChanged;
            foreach (var card in _subscribedVm.NotesCollection)
            {
                card.PropertyChanged -= OnCardPropertyChanged;
            }
            _subscribedVm = null;
        }

        if (e.NewValue is MeditationViewModel vm)
        {
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.NotesCollection.CollectionChanged += OnNotesCollectionChanged;
            foreach (var card in vm.NotesCollection)
            {
                card.PropertyChanged -= OnCardPropertyChanged;
                card.PropertyChanged += OnCardPropertyChanged;
            }

            EnsureVaultNotesLoaded();
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is MeditationViewModel vm)
        {
            if (e.PropertyName == nameof(MeditationViewModel.ComposerMarkdown))
            {
                if (MarkdownBox != null && MarkdownBox.Text != (vm.ComposerMarkdown ?? string.Empty))
                {
                    int caret = MarkdownBox.CaretIndex;
                    MarkdownBox.Text = vm.ComposerMarkdown ?? string.Empty;
                    MarkdownBox.CaretIndex = Math.Min(caret, MarkdownBox.Text.Length);
                }
            }
            else if (e.PropertyName == nameof(MeditationViewModel.IsComposerOpen))
            {
                if (vm.IsComposerOpen)
                {
                    if (MarkdownBox != null)
                    {
                        MarkdownBox.Text = vm.ComposerMarkdown ?? string.Empty;
                    }
                    FocusComposer();
                }
            }
        }
    }

    private void EnsureVaultNotesLoaded()
    {
        if (DataContext is MeditationViewModel vm)
        {
            if (vm.NotesCollection.Count == 0)
            {
                var vaultNotes = LoadNotesFromVault();
                if (vaultNotes.Count > 0)
                {
                    foreach (var card in vaultNotes)
                    {
                        card.PropertyChanged -= OnCardPropertyChanged;
                        card.PropertyChanged += OnCardPropertyChanged;
                        vm.NotesCollection.Add(card);
                    }
                }
            }
        }
    }

    private void OnNotesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (NoteCard card in e.NewItems)
            {
                card.PropertyChanged -= OnCardPropertyChanged;
                card.PropertyChanged += OnCardPropertyChanged;
            }
        }

        PersistNotesVault();
    }

    private void OnCardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        PersistNotesVault();
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

    private void PersistNotesVault()
    {
        if (DataContext is MeditationViewModel vm)
        {
            SaveNotesToVault(vm.NotesCollection);
        }
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

        string text = MarkdownBox?.Text ?? string.Empty;
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

internal static class NoteSmartTypingHelper
{
    public static void HandlePreviewKeyDown(TextBox textBox, KeyEventArgs e)
    {
        if (textBox == null || e == null) return;

        // Skip when Ctrl or Alt is held
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            if (textBox.SelectionLength > 0) return;

            int caret = textBox.CaretIndex;
            string text = textBox.Text ?? string.Empty;
            int lineStartIndex = caret > 0 ? text.LastIndexOf('\n', caret - 1) + 1 : 0;
            string linePrefix = text.Substring(lineStartIndex, caret - lineStartIndex);
            string trimmedPrefix = linePrefix.TrimStart(' ', '\t');
            int indentLen = linePrefix.Length - trimmedPrefix.Length;
            string indent = linePrefix.Substring(0, indentLen);

            // Trigger for Task Checkbox:
            // Standard markdown: - [ ], * [ ], [ ], - [x], * [x], [x], -[], *[], []
            // Bullet variations: • [ ], • [x], •[], •[x]
            if (trimmedPrefix == "- [ ]" || trimmedPrefix == "* [ ]" || trimmedPrefix == "[ ]" ||
                trimmedPrefix == "-[]" || trimmedPrefix == "*[]" || trimmedPrefix == "[]" ||
                trimmedPrefix == "• [ ]" || trimmedPrefix == "•[]")
            {
                string before = text.Substring(0, lineStartIndex);
                string after = text.Substring(caret);
                string replacement = indent + "- [ ] ";
                textBox.Text = before + replacement + after;
                textBox.CaretIndex = lineStartIndex + replacement.Length;
                e.Handled = true;
            }
            else if (trimmedPrefix == "- [x]" || trimmedPrefix == "* [x]" || trimmedPrefix == "[x]" ||
                     trimmedPrefix == "- [X]" || trimmedPrefix == "* [X]" || trimmedPrefix == "[X]" ||
                     trimmedPrefix == "-[x]" || trimmedPrefix == "-[X]" ||
                     trimmedPrefix == "• [x]" || trimmedPrefix == "• [X]" ||
                     trimmedPrefix == "•[x]" || trimmedPrefix == "•[X]")
            {
                string before = text.Substring(0, lineStartIndex);
                string after = text.Substring(caret);
                string replacement = indent + "- [x] ";
                textBox.Text = before + replacement + after;
                textBox.CaretIndex = lineStartIndex + replacement.Length;
                e.Handled = true;
            }
            // Trigger for Bullet: - or *
            else if (trimmedPrefix == "-" || trimmedPrefix == "*")
            {
                string before = text.Substring(0, lineStartIndex);
                string after = text.Substring(caret);
                string replacement = indent + "- ";
                textBox.Text = before + replacement + after;
                textBox.CaretIndex = lineStartIndex + replacement.Length;
                e.Handled = true;
            }
            // Trigger for Blockquote: >
            else if (trimmedPrefix == ">")
            {
                string before = text.Substring(0, lineStartIndex);
                string after = text.Substring(caret);
                string replacement = indent + "> ";
                textBox.Text = before + replacement + after;
                textBox.CaretIndex = lineStartIndex + replacement.Length;
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter)
        {
            int caret = textBox.CaretIndex;
            string text = textBox.Text ?? string.Empty;
            int lineStartIndex = caret > 0 ? text.LastIndexOf('\n', caret - 1) + 1 : 0;
            int lineEndIndex = text.IndexOfAny(new[] { '\r', '\n' }, caret);
            if (lineEndIndex == -1) lineEndIndex = text.Length;

            string fullLine = text.Substring(lineStartIndex, lineEndIndex - lineStartIndex);
            string trimmedStart = fullLine.TrimStart(' ', '\t');
            int indentLen = fullLine.Length - trimmedStart.Length;
            string indent = fullLine.Substring(0, indentLen);

            // 1. Task Checkbox list continuation / exit
            if (trimmedStart == "- [ ] " || trimmedStart == "- [ ]" ||
                trimmedStart == "* [ ] " || trimmedStart == "* [ ]" ||
                trimmedStart == "• [ ] " || trimmedStart == "• [ ]" ||
                trimmedStart == "[ ] " || trimmedStart == "[ ]" ||
                trimmedStart == "☐ " || trimmedStart == "☐" ||
                trimmedStart == "☑ " || trimmedStart == "☑")
            {
                // Empty checkbox line: exit task list by clearing the prefix
                string before = text.Substring(0, lineStartIndex);
                string after = text.Substring(lineEndIndex);
                textBox.Text = before + after;
                textBox.CaretIndex = lineStartIndex;
                e.Handled = true;
            }
            else if (trimmedStart.StartsWith("- [ ] ") || trimmedStart.StartsWith("* [ ] ") ||
                     trimmedStart.StartsWith("• [ ] ") || trimmedStart.StartsWith("[ ] ") ||
                     trimmedStart.StartsWith("☐ ") ||
                     trimmedStart.StartsWith("- [x] ") || trimmedStart.StartsWith("- [X] ") ||
                     trimmedStart.StartsWith("• [x] ") || trimmedStart.StartsWith("• [X] ") ||
                     trimmedStart.StartsWith("☑ "))
            {
                // Non-empty task line: continue with a new empty checkbox on next line
                string before = text.Substring(0, caret);
                string after = text.Substring(caret);
                string insert = Environment.NewLine + indent + "- [ ] ";
                textBox.Text = before + insert + after;
                textBox.CaretIndex = caret + insert.Length;
                e.Handled = true;
            }
            // 2. Bullet list continuation / exit
            else if (trimmedStart == "- " || trimmedStart == "-" ||
                     trimmedStart == "* " || trimmedStart == "*" ||
                     trimmedStart == "• " || trimmedStart == "•")
            {
                string before = text.Substring(0, lineStartIndex);
                string after = text.Substring(lineEndIndex);
                textBox.Text = before + after;
                textBox.CaretIndex = lineStartIndex;
                e.Handled = true;
            }
            else if (trimmedStart.StartsWith("- ") || trimmedStart.StartsWith("* ") || trimmedStart.StartsWith("• "))
            {
                string before = text.Substring(0, caret);
                string after = text.Substring(caret);
                string insert = Environment.NewLine + indent + "- ";
                textBox.Text = before + insert + after;
                textBox.CaretIndex = caret + insert.Length;
                e.Handled = true;
            }
            // 3. Numbered list continuation / exit (e.g. 1. Item)
            else if (Regex.IsMatch(trimmedStart, @"^\d+\.\s*$"))
            {
                // Empty numbered item: exit list
                string before = text.Substring(0, lineStartIndex);
                string after = text.Substring(lineEndIndex);
                textBox.Text = before + after;
                textBox.CaretIndex = lineStartIndex;
                e.Handled = true;
            }
            else if (Regex.Match(trimmedStart, @"^(\d+)\.\s+(.+)$") is Match numMatch && numMatch.Success)
            {
                if (int.TryParse(numMatch.Groups[1].Value, out int currentNum))
                {
                    string before = text.Substring(0, caret);
                    string after = text.Substring(caret);
                    string insert = Environment.NewLine + indent + $"{currentNum + 1}. ";
                    textBox.Text = before + insert + after;
                    textBox.CaretIndex = caret + insert.Length;
                    e.Handled = true;
                }
            }
            // 4. Blockquote continuation / exit
            else if (trimmedStart == "> " || trimmedStart == ">")
            {
                string before = text.Substring(0, lineStartIndex);
                string after = text.Substring(lineEndIndex);
                textBox.Text = before + after;
                textBox.CaretIndex = lineStartIndex;
                e.Handled = true;
            }
            else if (trimmedStart.StartsWith("> "))
            {
                string before = text.Substring(0, caret);
                string after = text.Substring(caret);
                string insert = Environment.NewLine + indent + "> ";
                textBox.Text = before + insert + after;
                textBox.CaretIndex = caret + insert.Length;
                e.Handled = true;
            }
            // 5. Headings: cleanly transition to normal paragraph (no # prefix on next line)
            else if (trimmedStart.StartsWith("# ") || trimmedStart.StartsWith("## ") ||
                     trimmedStart.StartsWith("### ") || trimmedStart.StartsWith("#### "))
            {
                string before = text.Substring(0, caret);
                string after = text.Substring(caret);
                string insert = Environment.NewLine + indent;
                textBox.Text = before + insert + after;
                textBox.CaretIndex = caret + insert.Length;
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Back)
        {
            if (textBox.SelectionLength > 0) return;

            int caret = textBox.CaretIndex;
            string text = textBox.Text ?? string.Empty;
            int lineStartIndex = caret > 0 ? text.LastIndexOf('\n', caret - 1) + 1 : 0;
            string linePrefix = text.Substring(lineStartIndex, caret - lineStartIndex);
            string trimmedPrefix = linePrefix.TrimStart(' ', '\t');

            // Quick backspace removal for prefixes
            if (trimmedPrefix == "- [ ] " || trimmedPrefix == "- " || trimmedPrefix == "• " ||
                trimmedPrefix == "☐ " || trimmedPrefix == "> ")
            {
                int indentLen = linePrefix.Length - trimmedPrefix.Length;
                string before = text.Substring(0, lineStartIndex + indentLen);
                string after = text.Substring(caret);
                textBox.Text = before + after;
                textBox.CaretIndex = lineStartIndex + indentLen;
                e.Handled = true;
            }
        }
    }
}
