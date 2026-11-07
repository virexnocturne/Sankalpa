using System;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;

namespace Blocker.App;

public static class NotesAttachmentManager
{
    private static readonly string[] SupportedImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"
    };

    public static string GetAttachmentsDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(localAppData, "Sankalpa", "attachments");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    /// <summary>
    /// Saves a BitmapSource to the attachments directory as a PNG file and returns the file path and markdown tag.
    /// </summary>
    public static (string filePath, string markdownTag)? SaveBitmap(BitmapSource? bitmapSource, string prefix = "Snip")
    {
        if (bitmapSource == null) return null;

        try
        {
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
            string fileName = $"{prefix}_{timestamp}.png";
            string destination = Path.Combine(GetAttachmentsDirectory(), fileName);

            using (var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                encoder.Save(fileStream);
            }

            return (destination, $"![[{fileName}]]");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if the clipboard contains an image bitmap or image file drops.
    /// If present, saves the image to the attachments directory and returns the markdown tag, e.g. ![[Pasted_Image_...png]].
    /// Returns null if no image is present or an error occurs.
    /// </summary>
    public static string? TrySaveClipboardImage()
    {
        try
        {
            // 1. Direct Bitmap in Clipboard
            if (Clipboard.ContainsImage())
            {
                var bitmapSource = Clipboard.GetImage();
                if (bitmapSource != null)
                {
                    var saved = SaveBitmap(bitmapSource, "Pasted_Image");
                    if (saved != null)
                    {
                        return saved.Value.markdownTag;
                    }
                }
            }

            // 2. File drop list in Clipboard (e.g. copied image file from Explorer)
            if (Clipboard.ContainsFileDropList())
            {
                StringCollection? files = Clipboard.GetFileDropList();
                if (files != null && files.Count > 0)
                {
                    foreach (string? filePath in files)
                    {
                        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                        {
                            continue;
                        }

                        string ext = Path.GetExtension(filePath).ToLowerInvariant();
                        if (Array.IndexOf(SupportedImageExtensions, ext) >= 0)
                        {
                            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
                            string fileName = $"Pasted_Image_{timestamp}{ext}";
                            string destination = Path.Combine(GetAttachmentsDirectory(), fileName);

                            File.Copy(filePath, destination, overwrite: true);
                            return $"![[{fileName}]]";
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // Clipboard may be temporarily locked by another process
            return null;
        }

        return null;
    }

    /// <summary>
    /// Helper to handle paste event: checks clipboard for image, returns true and the markdown if saved.
    /// </summary>
    public static bool TryHandleImagePaste(out string? imageMarkdown)
    {
        imageMarkdown = TrySaveClipboardImage();
        return !string.IsNullOrEmpty(imageMarkdown);
    }

    /// <summary>
    /// Resolves an attachment reference (e.g. "Pasted_Image_...png", "![[...]]", or full path) to a full on-disk path.
    /// </summary>
    public static string ResolveAttachmentPath(string attachmentRef)
    {
        if (string.IsNullOrWhiteSpace(attachmentRef))
        {
            return string.Empty;
        }

        string cleaned = CleanAttachmentReference(attachmentRef);

        // If it's already an absolute path that exists, return it
        if (Path.IsPathRooted(cleaned) && File.Exists(cleaned))
        {
            return cleaned;
        }

        // Check in attachments directory
        string inAttachments = Path.Combine(GetAttachmentsDirectory(), Path.GetFileName(cleaned));
        if (File.Exists(inAttachments))
        {
            return inAttachments;
        }

        // Return path in attachments directory even if not yet on disk
        return inAttachments;
    }

    /// <summary>
    /// Cleans formatting from an image reference.
    /// e.g. "![[Pasted_Image_1.png]]" -> "Pasted_Image_1.png"
    /// "![alt](Pasted_Image_1.png)" -> "Pasted_Image_1.png"
    /// "Pasted_Image_1.png|300" -> "Pasted_Image_1.png"
    /// </summary>
    public static string CleanAttachmentReference(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string trimmed = raw.Trim();

        // ![[...]]
        if (trimmed.StartsWith("![[") && trimmed.EndsWith("]]"))
        {
            trimmed = trimmed.Substring(3, trimmed.Length - 5).Trim();
        }
        // [[...]]
        else if (trimmed.StartsWith("[[") && trimmed.EndsWith("]]"))
        {
            trimmed = trimmed.Substring(2, trimmed.Length - 4).Trim();
        }
        // ![...](...)
        else if (trimmed.StartsWith("![") && trimmed.EndsWith(")") && trimmed.Contains("]("))
        {
            int idx = trimmed.IndexOf("](", StringComparison.Ordinal);
            trimmed = trimmed.Substring(idx + 2, trimmed.Length - idx - 3).Trim();
        }

        // Handle size parameter: "img.png|300"
        if (trimmed.Contains('|'))
        {
            trimmed = trimmed.Split('|')[0].Trim();
        }

        return trimmed;
    }

    /// <summary>
    /// Safely loads a BitmapImage from the specified attachment reference without locking the underlying file on disk.
    /// </summary>
    public static BitmapImage? LoadBitmapSafely(string imageRefOrPath)
    {
        string fullPath = ResolveAttachmentPath(imageRefOrPath);
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
