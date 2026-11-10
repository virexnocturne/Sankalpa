using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Blocker.Core;

// Disambiguate WPF vs WindowsForms/System.Drawing
using Brush = System.Windows.Media.Brush;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using DataObject = System.Windows.DataObject;
using DispatcherPriority = System.Windows.Threading.DispatcherPriority;

namespace Blocker.App;

public partial class NotesDrawerControl : System.Windows.Controls.UserControl
{
    private bool _isPreviewTabActive;
    private bool _isBulkLoading;
    private readonly DispatcherTimer _thumbnailDebounceTimer;
    private List<string> _currentThumbRefs = new();

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

        _thumbnailDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _thumbnailDebounceTimer.Tick += (s, e) =>
        {
            _thumbnailDebounceTimer.Stop();
            RebuildThumbnailsAsync();
        };

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
        RebuildThumbnailsAsync();
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
            NoteMarkdownRenderer.RenderMarkdownToPanel(ComposerPreviewPanel, MarkdownBox.Text, this, null);
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
        NoteMarkdownRenderer.RenderMarkdownToPanel(bodyPanel, markdown, this, onTaskToggled);
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
                ? (System.Windows.Media.Geometry)FindResource("IconChevronRight")
                : (System.Windows.Media.Geometry)FindResource("IconChevronLeft");
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

        _thumbnailDebounceTimer.Stop();
        _thumbnailDebounceTimer.Start();
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
                var vaultNotes = NotesVaultStore.LoadNotesFromVault();
                if (vaultNotes.Count > 0)
                {
                    _isBulkLoading = true;
                    try
                    {
                        NoteCardCollection.SyncCards(vm.NotesCollection, vaultNotes, card =>
                        {
                            card.PropertyChanged -= OnCardPropertyChanged;
                            card.PropertyChanged += OnCardPropertyChanged;
                        });
                    }
                    finally
                    {
                        _isBulkLoading = false;
                    }
                }
            }
        }
    }

    private void OnNotesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_isBulkLoading) return;

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
        if (_isBulkLoading) return;
        PersistNotesVault();
    }

    public static string GetNotesVaultPath() => NotesVaultStore.GetNotesVaultPath();

    public static List<NoteCard> LoadNotesFromVault() => NotesVaultStore.LoadNotesFromVault();

    public static void SaveNotesToVault(IEnumerable<NoteCard> cards) => NotesVaultStore.QueueSave(cards);

    private void PersistNotesVault()
    {
        if (DataContext is MeditationViewModel vm)
        {
            NotesVaultStore.QueueSave(vm.NotesCollection);
        }
    }

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

        string text = MarkdownBox?.Text ?? string.Empty;
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
                    Stretch = System.Windows.Media.Stretch.Uniform
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
                                Stretch = System.Windows.Media.Stretch.Uniform
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

internal static class NoteSmartTypingHelper
{
    private static readonly System.Text.RegularExpressions.Regex EmptyNumberedRegex = new(@"^\d+\.\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex NumberedItemRegex = new(@"^(\d+)\.\s+(.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

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
            else if (EmptyNumberedRegex.IsMatch(trimmedStart))
            {
                // Empty numbered item: exit list
                string before = text.Substring(0, lineStartIndex);
                string after = text.Substring(lineEndIndex);
                textBox.Text = before + after;
                textBox.CaretIndex = lineStartIndex;
                e.Handled = true;
            }
            else if (NumberedItemRegex.Match(trimmedStart) is System.Text.RegularExpressions.Match numMatch && numMatch.Success)
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
