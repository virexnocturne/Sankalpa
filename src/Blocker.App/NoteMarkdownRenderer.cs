using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;
using CheckBox = System.Windows.Controls.CheckBox;
using Cursors = System.Windows.Input.Cursors;
using Rectangle = System.Windows.Shapes.Rectangle;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using IOPath = System.IO.Path;

namespace Blocker.App;

public class NoteRenderResources
{
    public Brush TextPrimary { get; }
    public Brush TextSecondary { get; }
    public Brush TextDisabled { get; }
    public Brush Accent { get; }
    public Brush Warning { get; }
    public Brush SurfaceRaised { get; }
    public Brush Border { get; }
    public FontFamily FontBody { get; }
    public FontFamily FontMono { get; }

    public NoteRenderResources(FrameworkElement context)
    {
        TextPrimary = ResolveResource<Brush>(context, "TextPrimary", Brushes.White);
        TextSecondary = ResolveResource<Brush>(context, "TextSecondary", Brushes.LightGray);
        TextDisabled = ResolveResource<Brush>(context, "TextDisabled", Brushes.Gray);
        Accent = ResolveResource<Brush>(context, "Accent", Brushes.MediumPurple);
        Warning = ResolveResource<Brush>(context, "Warning", Brushes.Orange);
        SurfaceRaised = ResolveResource<Brush>(context, "SurfaceRaised", Brushes.DarkSlateGray);
        Border = ResolveResource<Brush>(context, "Border", Brushes.DimGray);
        FontBody = ResolveResource<FontFamily>(context, "FontBody", new FontFamily("Segoe UI"));
        FontMono = ResolveResource<FontFamily>(context, "FontMono", new FontFamily("Consolas"));
    }

    private static T ResolveResource<T>(FrameworkElement context, string key, T fallback)
    {
        try
        {
            var res = context.TryFindResource(key);
            if (res is T typed) return typed;
        }
        catch { }
        return fallback;
    }
}

public static class NoteMarkdownRenderer
{
    private static readonly Regex TaskRegex = new(
        @"^\s*(?:[-*•]\s*)?(?:\[([ xX])\]|([☐☑]))\s*(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex NumListRegex = new(
        @"^\s*(\d+)\.\s+(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex WikiImgRegex = new(
        @"!\[\[(.*?)\]\]",
        RegexOptions.Compiled);

    private static readonly Regex MdImgRegex = new(
        @"!\[(.*?)\]\((.*?)\)",
        RegexOptions.Compiled);

    private static readonly Regex InlineFormatRegex = new(
        @"(\*\*\*(.*?)\*\*\*|\*\*(.*?)\*\*|\*(.*?)\*|~~(.*?)~~|==(.*?)==|`(.*?)`|\[\[(.*?)\]\]|(?<=\s|^)(#[a-zA-Z0-9_\-]+))",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses and renders markdown text into WPF elements within bodyPanel.
    /// Uses precompiled regexes and resolves resources once per pass for maximum throughput.
    /// </summary>
    public static void RenderMarkdownToPanel(
        StackPanel bodyPanel,
        string markdown,
        FrameworkElement resourceContext,
        Action<int, bool>? onTaskToggled = null)
    {
        bodyPanel.Children.Clear();

        var res = new NoteRenderResources(resourceContext);

        if (string.IsNullOrWhiteSpace(markdown))
        {
            var emptyText = new TextBlock
            {
                Text = "(No content)",
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                Foreground = res.TextDisabled
            };
            bodyPanel.Children.Add(emptyText);
            return;
        }

        string[] lines = markdown.Replace("\r", "").Split('\n');
        int taskCheckboxCount = 0;

        foreach (var rawLine in lines)
        {
            string trimmedStart = rawLine.TrimStart();

            // Image tags
            var wikiImgMatch = WikiImgRegex.Match(trimmedStart);
            var mdImgMatch = MdImgRegex.Match(trimmedStart);

            if (wikiImgMatch.Success)
            {
                string before = trimmedStart.Substring(0, wikiImgMatch.Index).Trim();
                if (!string.IsNullOrEmpty(before))
                {
                    var paraBefore = new TextBlock
                    {
                        FontSize = 13,
                        FontFamily = res.FontBody,
                        Foreground = res.TextPrimary,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    AddFormattedInlines(paraBefore.Inlines, before, res);
                    bodyPanel.Children.Add(paraBefore);
                }

                string target = wikiImgMatch.Groups[1].Value.Trim();
                string imgRef = target.Contains('|') ? target.Split('|')[0].Trim() : target;
                var imgControl = CreateImageBorder(imgRef, res);
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
                        FontFamily = res.FontBody,
                        Foreground = res.TextPrimary,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    AddFormattedInlines(paraAfter.Inlines, after, res);
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
                        FontFamily = res.FontBody,
                        Foreground = res.TextPrimary,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    AddFormattedInlines(paraBefore.Inlines, before, res);
                    bodyPanel.Children.Add(paraBefore);
                }

                string imgRef = mdImgMatch.Groups[2].Value.Trim();
                var imgControl = CreateImageBorder(imgRef, res);
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
                        FontFamily = res.FontBody,
                        Foreground = res.TextPrimary,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    AddFormattedInlines(paraAfter.Inlines, after, res);
                    bodyPanel.Children.Add(paraAfter);
                }
                continue;
            }

            // 1. Task Checkbox item
            var taskMatch = TaskRegex.Match(rawLine);
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
                    FontFamily = res.FontBody,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = isChecked ? res.TextDisabled : res.TextPrimary,
                    TextDecorations = isChecked ? TextDecorations.Strikethrough : null
                };
                AddFormattedInlines(taskLabel.Inlines, taskDescription, res);
                cb.Content = taskLabel;

                cb.Click += (s, e) =>
                {
                    bool newChecked = cb.IsChecked == true;
                    taskLabel.Foreground = newChecked ? res.TextDisabled : res.TextPrimary;
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
                    Foreground = res.TextSecondary,
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
                    Foreground = res.TextSecondary,
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
                    Foreground = res.TextPrimary,
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
                    Foreground = res.TextPrimary,
                    Margin = new Thickness(0, 8, 0, 4)
                };
                bodyPanel.Children.Add(h1);
            }
            // 6. Numbered list items (1. , 2. )
            else if (NumListRegex.Match(rawLine) is Match nm && nm.Success)
            {
                var numBlock = new TextBlock
                {
                    FontSize = 13,
                    FontFamily = res.FontBody,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 2, 0, 2)
                };
                numBlock.Inlines.Add(new Run($"{nm.Groups[1].Value}. ")
                {
                    FontWeight = FontWeights.SemiBold,
                    Foreground = res.Accent
                });
                AddFormattedInlines(numBlock.Inlines, nm.Groups[2].Value.Trim(), res);
                bodyPanel.Children.Add(numBlock);
            }
            // 7. Unordered Bullet list items
            else if (trimmedStart.StartsWith("• ") || trimmedStart.StartsWith("- ") || trimmedStart.StartsWith("* "))
            {
                var bulletBlock = new TextBlock
                {
                    FontSize = 13,
                    FontFamily = res.FontBody,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 2, 0, 2)
                };
                bulletBlock.Inlines.Add(new Run("• ")
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = res.Accent
                });
                AddFormattedInlines(bulletBlock.Inlines, trimmedStart[2..].TrimStart(), res);
                bodyPanel.Children.Add(bulletBlock);
            }
            // 8. Blockquotes (> )
            else if (trimmedStart.StartsWith("> ") || trimmedStart == ">")
            {
                string quoteText = trimmedStart.Length > 2 ? trimmedStart[2..].Trim() : string.Empty;
                var quoteBorder = new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    BorderBrush = res.Accent,
                    Background = res.SurfaceRaised,
                    CornerRadius = new CornerRadius(0, 4, 4, 0),
                    Padding = new Thickness(10, 4, 8, 4),
                    Margin = new Thickness(0, 4, 0, 4)
                };
                var quoteBlock = new TextBlock
                {
                    FontSize = 13,
                    FontStyle = FontStyles.Italic,
                    Foreground = res.TextSecondary,
                    TextWrapping = TextWrapping.Wrap
                };
                AddFormattedInlines(quoteBlock.Inlines, quoteText, res);
                quoteBorder.Child = quoteBlock;
                bodyPanel.Children.Add(quoteBorder);
            }
            // 9. Horizontal Divider (---, ***, ___)
            else if (trimmedStart == "---" || trimmedStart == "***" || trimmedStart == "___")
            {
                var hr = new Rectangle
                {
                    Height = 1,
                    Fill = res.Border,
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
                    FontFamily = res.FontBody,
                    Foreground = res.TextPrimary,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                AddFormattedInlines(paraBlock.Inlines, rawLine, res);
                bodyPanel.Children.Add(paraBlock);
            }
        }
    }

    public static UIElement? CreateImageBorder(string imgRef, NoteRenderResources res)
    {
        string fullPath = NotesAttachmentManager.ResolveAttachmentPath(imgRef);
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            return new TextBlock
            {
                Text = $"[Image: {IOPath.GetFileName(imgRef)}]",
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                Foreground = res.TextDisabled,
                Margin = new Thickness(0, 2, 0, 4)
            };
        }

        var image = new System.Windows.Controls.Image
        {
            MaxHeight = 240,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderBrush = res.Border,
            BorderThickness = new Thickness(1),
            Background = res.SurfaceRaised,
            Padding = new Thickness(2),
            Margin = new Thickness(0, 4, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = image
        };

        // Try load from cache synchronously first (0 overhead if cached)
        var cached = NotesAttachmentManager.LoadBitmapSafely(imgRef, 480);
        if (cached != null)
        {
            image.Source = cached;
        }
        else
        {
            // Asynchronously load and assign on UI thread without blocking
            NotesAttachmentManager.LoadBitmapSafelyAsync(imgRef, 480).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result != null)
                {
                    border.Dispatcher.InvokeAsync(() =>
                    {
                        image.Source = t.Result;
                    });
                }
            });
        }

        return border;
    }

    public static void AddFormattedInlines(InlineCollection inlines, string text, NoteRenderResources res)
    {
        if (string.IsNullOrEmpty(text)) return;

        int lastIndex = 0;

        foreach (Match m in InlineFormatRegex.Matches(text))
        {
            if (m.Index > lastIndex)
            {
                inlines.Add(new Run(text.Substring(lastIndex, m.Index - lastIndex)) { Foreground = res.TextPrimary });
            }

            string val = m.Value;
            if (val.StartsWith("***") && val.EndsWith("***"))
            {
                inlines.Add(new Run(m.Groups[2].Value)
                {
                    FontWeight = FontWeights.Bold,
                    FontStyle = FontStyles.Italic,
                    Foreground = res.TextPrimary
                });
            }
            else if (val.StartsWith("**") && val.EndsWith("**"))
            {
                inlines.Add(new Run(m.Groups[3].Value)
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = res.TextPrimary
                });
            }
            else if (val.StartsWith("*") && val.EndsWith("*"))
            {
                inlines.Add(new Run(m.Groups[4].Value)
                {
                    FontStyle = FontStyles.Italic,
                    Foreground = res.TextPrimary
                });
            }
            else if (val.StartsWith("~~") && val.EndsWith("~~"))
            {
                inlines.Add(new Run(m.Groups[5].Value)
                {
                    TextDecorations = TextDecorations.Strikethrough,
                    Foreground = res.TextDisabled
                });
            }
            else if (val.StartsWith("==") && val.EndsWith("=="))
            {
                // Highlight style: amber background pill
                inlines.Add(new Run($" {m.Groups[6].Value} ")
                {
                    Background = res.Warning,
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 0, 0)),
                    FontWeight = FontWeights.SemiBold
                });
            }
            else if (val.StartsWith("`") && val.EndsWith("`"))
            {
                inlines.Add(new Run($" {m.Groups[7].Value} ")
                {
                    FontFamily = res.FontMono,
                    Background = res.SurfaceRaised,
                    Foreground = res.TextPrimary
                });
            }
            else if (val.StartsWith("[[") && val.EndsWith("]]"))
            {
                inlines.Add(new Run(m.Groups[8].Value)
                {
                    Foreground = res.Accent,
                    TextDecorations = TextDecorations.Underline
                });
            }
            else if (val.StartsWith("#"))
            {
                inlines.Add(new Run(val)
                {
                    Foreground = res.Accent,
                    FontWeight = FontWeights.SemiBold
                });
            }

            lastIndex = m.Index + m.Length;
        }

        if (lastIndex < text.Length)
        {
            inlines.Add(new Run(text.Substring(lastIndex)) { Foreground = res.TextPrimary });
        }
    }
}
