using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Blocker.Core;

/// <summary>
/// Catalog and validation service for ambient soundscapes and custom user audio tracks.
/// </summary>
public static class SoundscapeCatalog
{
    public const string RainId = "rain";
    public const string LibraryId = "library";
    public const string ForestId = "forest";
    public const string SingingBowlsId = "bowls";
    public const string MuteId = "mute";
    public const string CustomPrefix = "custom:";

    public const long MaxFileSizeBytes = 50 * 1024 * 1024; // 50 MB

    public static readonly string[] AllowedExtensions =
    {
        ".mp3",
        ".wav",
        ".m4a",
        ".wma",
        ".aac",
        ".flac"
    };

    public static string GetCustomAudioDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Sankalpa", "custom_audio");
    }

    /// <summary>
    /// Normalizes any raw soundscape identifier, legacy enum name, or display string into a canonical ID.
    /// Falls back to 'rain' if unparseable or corrupted.
    /// </summary>
    public static string NormalizeId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return RainId;
        }

        string trimmed = raw.Trim();

        // Check for custom:<guid>
        if (trimmed.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string guidPart = trimmed[CustomPrefix.Length..].Trim();
            if (Guid.TryParse(guidPart, out var parsedGuid))
            {
                return $"{CustomPrefix}{parsedGuid:D}".ToLowerInvariant();
            }
            // Allow alpha-numeric guid string
            if (guidPart.Length > 0 && !guidPart.Any(char.IsWhiteSpace))
            {
                return $"{CustomPrefix}{guidPart}".ToLowerInvariant();
            }
            return RainId;
        }

        string lower = trimmed.ToLowerInvariant().Replace(" ", "").Replace("_", "");

        return lower switch
        {
            "rain" => RainId,
            "library" => LibraryId,
            "forest" => ForestId,
            "bowls" or "singingbowls" => SingingBowlsId,
            "mute" or "none" => MuteId,
            _ => RainId
        };
    }

    /// <summary>
    /// Returns the standard built-in soundscape items.
    /// </summary>
    public static IReadOnlyList<SoundscapeItem> GetBuiltInTracks()
    {
        return new List<SoundscapeItem>
        {
            new(RainId, "Rain", "rain", isBuiltIn: true, filePath: "rain.wav", volumeTrim: 1.0, loop: true),
            new(LibraryId, "Library", "library", isBuiltIn: true, filePath: "library.wav", volumeTrim: 1.0, loop: true),
            new(ForestId, "Forest", "forest", isBuiltIn: true, filePath: "forest.wav", volumeTrim: 1.0, loop: true),
            new(SingingBowlsId, "Singing Bowls", "bowls", isBuiltIn: true, filePath: "bowls.wav", volumeTrim: 1.0, loop: true),
            new(MuteId, "Mute", "mute", isBuiltIn: true, filePath: null, volumeTrim: 0.0, loop: false)
        };
    }

    /// <summary>
    /// Returns all available tracks (built-in presets followed by valid user custom soundtracks).
    /// </summary>
    public static List<SoundscapeItem> GetAllTracks(UiSettings? settings, string? customAudioDir = null)
    {
        var tracks = new List<SoundscapeItem>(GetBuiltInTracks());

        if (settings?.CustomSoundtracks != null)
        {
            string dir = customAudioDir ?? GetCustomAudioDirectory();
            foreach (var meta in settings.CustomSoundtracks)
            {
                if (string.IsNullOrWhiteSpace(meta.Id) || string.IsNullOrWhiteSpace(meta.FileName))
                {
                    continue;
                }

                // Path traversal defense: ensure FileName has no directory separators
                string safeFileName = Path.GetFileName(meta.FileName);
                string resolvedPath = Path.Combine(dir, safeFileName);

                tracks.Add(new SoundscapeItem(
                    id: meta.Id,
                    displayName: string.IsNullOrWhiteSpace(meta.DisplayName) ? "Custom Track" : meta.DisplayName,
                    iconGlyph: "music",
                    isBuiltIn: false,
                    filePath: resolvedPath,
                    volumeTrim: meta.VolumeTrim > 0 ? meta.VolumeTrim : 1.0,
                    loop: true,
                    duration: meta.DurationSeconds.HasValue ? TimeSpan.FromSeconds(meta.DurationSeconds.Value) : null,
                    fileSizeBytes: meta.FileSizeBytes));
            }
        }

        return tracks;
    }

    /// <summary>
    /// Finds a track by normalized ID. If not found or if a custom track's file is missing,
    /// falls back gracefully to Rain.
    /// </summary>
    public static SoundscapeItem FindTrack(string? soundscapeId, UiSettings? settings, string? customAudioDir = null)
    {
        string normalized = NormalizeId(soundscapeId);
        var allTracks = GetAllTracks(settings, customAudioDir);

        var match = allTracks.FirstOrDefault(t => string.Equals(t.Id, normalized, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            // If it's a custom track and the file is missing on disk, fallback gracefully to Rain
            if (!match.IsBuiltIn && !string.IsNullOrEmpty(match.FilePath) && !File.Exists(match.FilePath))
            {
                System.Diagnostics.Trace.WriteLine($"[SoundscapeCatalog] Custom audio file missing at '{match.FilePath}'. Falling back to default Rain.");
                return allTracks.First(t => t.Id == RainId);
            }
            return match;
        }

        return allTracks.First(t => t.Id == RainId);
    }

    /// <summary>
    /// Validates an audio file against extension allow-list, size limits, and existence.
    /// </summary>
    public static bool ValidateAudioFile(string filePath, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            errorMessage = "No file specified.";
            return false;
        }

        if (!File.Exists(filePath))
        {
            errorMessage = "Audio file does not exist.";
            return false;
        }

        string ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
        {
            errorMessage = "File must have an audio extension.";
            return false;
        }

        string extLower = ext.ToLowerInvariant();
        if (extLower == ".ogg")
        {
            errorMessage = "OGG format is not natively supported by Windows Media Foundation. Please convert to MP3, WAV, FLAC, or M4A.";
            return false;
        }

        if (!AllowedExtensions.Contains(extLower, StringComparer.OrdinalIgnoreCase))
        {
            errorMessage = $"Unsupported audio extension '{extLower}'. Supported formats: {string.Join(", ", AllowedExtensions)}.";
            return false;
        }

        try
        {
            var info = new FileInfo(filePath);
            if (info.Length <= 0)
            {
                errorMessage = "Audio file is empty (0 bytes).";
                return false;
            }

            if (info.Length > MaxFileSizeBytes)
            {
                double mb = info.Length / (1024.0 * 1024.0);
                errorMessage = $"Audio file size ({mb:F1} MB) exceeds the 50 MB limit.";
                return false;
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Unable to read file info: {ex.Message}";
            return false;
        }

        errorMessage = null;
        return true;
    }

    /// <summary>
    /// Sanitizes a track display name for UI rendering and safety.
    /// Truncates to 40 characters and removes control characters.
    /// </summary>
    public static string SanitizeDisplayName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "Custom Track";
        }

        string name = Path.GetFileNameWithoutExtension(rawName.Trim());

        // Strip non-printable or control characters
        char[] cleanChars = name.Where(c => !char.IsControl(c)).ToArray();
        string clean = new string(cleanChars);

        // Collapse whitespace
        clean = Regex.Replace(clean, @"\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(clean))
        {
            return "Custom Track";
        }

        const int maxLen = 40;
        if (clean.Length > maxLen)
        {
            clean = clean[..maxLen].TrimEnd();
        }

        return clean;
    }

    public static SoundscapeType ToLegacyEnum(string? id)
    {
        string norm = NormalizeId(id);
        return norm switch
        {
            RainId => SoundscapeType.Rain,
            LibraryId => SoundscapeType.Library,
            ForestId => SoundscapeType.Forest,
            SingingBowlsId => SoundscapeType.SingingBowls,
            MuteId => SoundscapeType.None,
            _ => norm.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase) ? SoundscapeType.Custom : SoundscapeType.Rain
        };
    }

    public static string FromLegacyEnum(SoundscapeType type)
    {
        return type switch
        {
            SoundscapeType.Rain => RainId,
            SoundscapeType.Library => LibraryId,
            SoundscapeType.Forest => ForestId,
            SoundscapeType.SingingBowls => SingingBowlsId,
            SoundscapeType.None => MuteId,
            SoundscapeType.Custom => CustomPrefix,
            _ => RainId
        };
    }

    /// <summary>
    /// Computes a SHA256 hex string of the audio file to detect duplicate imports.
    /// </summary>
    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Checks if a file with the same content hash already exists in custom soundtracks.
    /// </summary>
    public static bool IsDuplicate(string sha256, UiSettings? settings, out CustomSoundtrackMetadata? existing)
    {
        existing = null;
        if (string.IsNullOrWhiteSpace(sha256) || settings?.CustomSoundtracks == null)
        {
            return false;
        }

        existing = settings.CustomSoundtracks.FirstOrDefault(t =>
            string.Equals(t.Sha256Hash, sha256, StringComparison.OrdinalIgnoreCase));

        return existing != null;
    }
}
