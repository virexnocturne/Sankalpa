using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Blocker.Core;

public enum AudioImportStatus
{
    Success,
    SkippedDuplicate,
    Failed
}

public class AudioImportItemResult
{
    public string SourcePath { get; set; } = string.Empty;
    public AudioImportStatus Status { get; set; }
    public CustomSoundtrackMetadata? Metadata { get; set; }
    public string? Reason { get; set; }
}

public class AudioImportBatchResult
{
    public List<CustomSoundtrackMetadata> ImportedTracks { get; } = new();
    public List<(string SourcePath, string ExistingDisplayName)> SkippedDuplicates { get; } = new();
    public List<(string SourcePath, string Reason)> FailedFiles { get; } = new();

    public int TotalAttempted => ImportedTracks.Count + SkippedDuplicates.Count + FailedFiles.Count;
    public int SuccessCount => ImportedTracks.Count;
    public int DuplicateCount => SkippedDuplicates.Count;
    public int FailureCount => FailedFiles.Count;

    public string GetSummaryMessage()
    {
        if (TotalAttempted == 0)
        {
            return "No audio files were found to import.";
        }

        if (FailureCount == 0 && DuplicateCount == 0)
        {
            return SuccessCount == 1
                ? $"Successfully imported '{ImportedTracks[0].DisplayName}'."
                : $"Successfully imported {SuccessCount} audio tracks.";
        }

        var parts = new List<string>();
        if (SuccessCount > 0)
        {
            parts.Add($"{SuccessCount} imported");
        }
        if (DuplicateCount > 0)
        {
            parts.Add($"{DuplicateCount} duplicate{(DuplicateCount > 1 ? "s" : "")} skipped");
        }
        if (FailureCount > 0)
        {
            parts.Add($"{FailureCount} failed");
        }

        string summary = string.Join(", ", parts) + ".";
        if (FailureCount > 0 && FailedFiles.Count > 0)
        {
            summary += $" Reason: {FailedFiles[0].Reason}";
        }

        return summary;
    }
}

/// <summary>
/// Service for secure validation, playlist parsing, deduplication, and atomic import of custom audio tracks.
/// </summary>
public static class AudioImportService
{
    public const int MaxPlaylistEntries = 100;

    /// <summary>
    /// Parses an M3U or M3U8 playlist file and safely resolves its entries into local audio file paths.
    /// Strictly rejects remote URLs, UNC/network shares, nested playlists, missing files, and path traversal attempts.
    /// </summary>
    public static bool TryParsePlaylist(
        string playlistPath,
        out List<string> resolvedAudioFiles,
        out List<string> warnings,
        out string? errorMessage)
    {
        resolvedAudioFiles = new List<string>();
        warnings = new List<string>();
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(playlistPath))
        {
            errorMessage = "Playlist path cannot be empty.";
            return false;
        }

        if (!File.Exists(playlistPath))
        {
            errorMessage = "Playlist file does not exist.";
            return false;
        }

        string ext = Path.GetExtension(playlistPath).ToLowerInvariant();
        if (ext != ".m3u" && ext != ".m3u8")
        {
            errorMessage = $"Unsupported playlist format '{ext}'. Supported formats: .m3u, .m3u8";
            return false;
        }

        string playlistDir = Path.GetDirectoryName(Path.GetFullPath(playlistPath)) ?? string.Empty;
        if (string.IsNullOrEmpty(playlistDir))
        {
            errorMessage = "Invalid playlist directory.";
            return false;
        }

        List<string> lines;
        try
        {
            // Use UTF-8 with BOM detection fallback
            using var reader = new StreamReader(playlistPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Add(line);
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Unable to read playlist file: {ex.Message}";
            return false;
        }

        int entryCount = 0;
        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Skip M3U comment/directive lines (#EXTM3U, #EXTINF, etc.)
            if (line.StartsWith('#'))
            {
                continue;
            }

            entryCount++;
            if (entryCount > MaxPlaylistEntries)
            {
                warnings.Add($"Playlist contains more than {MaxPlaylistEntries} entries; remaining entries were skipped.");
                break;
            }

            // Security check 1: Reject remote URLs (http://, https://, ftp://, file://, etc.)
            if (line.Contains("://", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Skipped remote URL entry: '{line}'. Only local audio files are permitted.");
                continue;
            }

            // Security check 2: Reject UNC network paths (\\server\share or //server/share)
            // This prevents SMB relay attacks and NTLM credential leakage.
            if (line.StartsWith(@"\\") || line.StartsWith("//"))
            {
                warnings.Add($"Skipped network share entry: '{line}'. Network/UNC paths are not permitted.");
                continue;
            }

            // Security check 3: Reject nested playlists to prevent recursion or parser denial of service
            string entryExt = Path.GetExtension(line).ToLowerInvariant();
            if (entryExt == ".m3u" || entryExt == ".m3u8")
            {
                warnings.Add($"Skipped nested playlist: '{line}'. Recursive playlists are not permitted.");
                continue;
            }

            // Resolve full canonical path
            string fullPath;
            try
            {
                if (Path.IsPathRooted(line))
                {
                    fullPath = Path.GetFullPath(line);
                }
                else
                {
                    fullPath = Path.GetFullPath(Path.Combine(playlistDir, line));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Invalid path '{line}': {ex.Message}");
                continue;
            }

            // Verify file exists
            if (!File.Exists(fullPath))
            {
                warnings.Add($"File not found: '{fullPath}'");
                continue;
            }

            // Validate extension against allowed audio list
            string resolvedExt = Path.GetExtension(fullPath).ToLowerInvariant();
            if (!SoundscapeCatalog.AllowedExtensions.Contains(resolvedExt, StringComparer.OrdinalIgnoreCase))
            {
                warnings.Add($"Unsupported audio format in playlist: '{Path.GetFileName(fullPath)}' ({resolvedExt})");
                continue;
            }

            resolvedAudioFiles.Add(fullPath);
        }

        if (resolvedAudioFiles.Count == 0 && warnings.Count > 0)
        {
            errorMessage = $"No valid local audio files found in playlist. ({warnings[0]})";
            return false;
        }

        if (resolvedAudioFiles.Count == 0)
        {
            errorMessage = "Playlist does not contain any audio entries.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Imports one or more audio files into Sankalpa's custom audio repository with duplicate detection,
    /// safe rollback on error, metadata registration, and settings persistence.
    /// </summary>
    public static AudioImportBatchResult ImportAudioFiles(
        IEnumerable<string> filePaths,
        UiSettings settings,
        string? customAudioDir = null,
        Func<string, (bool Decodable, double? Duration, string? Error)>? decodabilityProbe = null)
    {
        Func<string, System.Threading.Tasks.Task<(bool Decodable, double? Duration, string? Error)>>? asyncProbe = null;
        if (decodabilityProbe != null)
        {
            asyncProbe = path => System.Threading.Tasks.Task.FromResult(decodabilityProbe(path));
        }

        return ImportAudioFilesAsync(filePaths, settings, customAudioDir, asyncProbe).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously imports one or more audio files into Sankalpa's custom audio repository with duplicate detection,
    /// non-blocking async decodability probe, safe rollback on error, metadata registration, and settings persistence.
    /// </summary>
    public static async System.Threading.Tasks.Task<AudioImportBatchResult> ImportAudioFilesAsync(
        IEnumerable<string> filePaths,
        UiSettings settings,
        string? customAudioDir = null,
        Func<string, System.Threading.Tasks.Task<(bool Decodable, double? Duration, string? Error)>>? decodabilityProbe = null)
    {
        var result = new AudioImportBatchResult();
        settings.CustomSoundtracks ??= new List<CustomSoundtrackMetadata>();
        string targetDir = customAudioDir ?? SoundscapeCatalog.GetCustomAudioDirectory();

        try
        {
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }
        }
        catch (Exception ex)
        {
            result.FailedFiles.Add(("<storage>", $"Failed to create custom audio directory: {ex.Message}"));
            return result;
        }

        // Cache existing SHA-256 hashes to prevent duplicates both across existing tracks and within the batch
        var existingHashes = new Dictionary<string, CustomSoundtrackMetadata>(StringComparer.OrdinalIgnoreCase);
        if (settings.CustomSoundtracks != null)
        {
            foreach (var meta in settings.CustomSoundtracks)
            {
                if (!string.IsNullOrWhiteSpace(meta.Sha256Hash) && !existingHashes.ContainsKey(meta.Sha256Hash))
                {
                    existingHashes[meta.Sha256Hash] = meta;
                }
            }
        }

        foreach (var sourcePath in filePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            // 1. Validation
            if (!SoundscapeCatalog.ValidateAudioFile(sourcePath, out string? valErr))
            {
                result.FailedFiles.Add((sourcePath, valErr ?? "Invalid audio file."));
                continue;
            }

            // 2. Compute SHA-256 for duplicate detection
            string hash;
            try
            {
                hash = SoundscapeCatalog.ComputeSha256(sourcePath);
            }
            catch (Exception ex)
            {
                result.FailedFiles.Add((sourcePath, $"Unable to read audio file for hash calculation: {ex.Message}"));
                continue;
            }

            if (existingHashes.TryGetValue(hash, out var existingTrack))
            {
                result.SkippedDuplicates.Add((sourcePath, existingTrack.DisplayName));
                continue;
            }

            // 3. Optional decodability probe
            double? probedDuration = null;
            if (decodabilityProbe != null)
            {
                try
                {
                    var probe = await decodabilityProbe(sourcePath);
                    if (!probe.Decodable)
                    {
                        result.FailedFiles.Add((sourcePath, $"Audio file could not be decoded: {probe.Error ?? "Unsupported or corrupted stream."}"));
                        continue;
                    }
                    probedDuration = probe.Duration;
                }
                catch (Exception ex)
                {
                    result.FailedFiles.Add((sourcePath, $"Audio probe failed: {ex.Message}"));
                    continue;
                }
            }

            // 4. Copy file with GUID filename & rollback on error
            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            var trackGuid = Guid.NewGuid();
            string targetFileName = $"{trackGuid:N}{ext}";
            string targetPath = Path.Combine(targetDir, targetFileName);

            try
            {
                await System.Threading.Tasks.Task.Run(() => File.Copy(sourcePath, targetPath, overwrite: false));

                var fileInfo = new FileInfo(targetPath);
                string displayName = SoundscapeCatalog.SanitizeDisplayName(Path.GetFileNameWithoutExtension(sourcePath));
                string canonicalTrackId = SoundscapeCatalog.NormalizeId($"{SoundscapeCatalog.CustomPrefix}{trackGuid:D}");

                var newTrack = new CustomSoundtrackMetadata
                {
                    Id = canonicalTrackId,
                    DisplayName = displayName,
                    FileName = targetFileName,
                    VolumeTrim = 1.0,
                    DurationSeconds = probedDuration,
                    FileSizeBytes = fileInfo.Length,
                    Sha256Hash = hash,
                    OriginalFileName = Path.GetFileName(sourcePath),
                    CreatedAt = DateTime.UtcNow
                };

                settings.CustomSoundtracks.Add(newTrack);
                existingHashes[hash] = newTrack;
                result.ImportedTracks.Add(newTrack);
            }
            catch (Exception ex)
            {
                // Rollback: delete target file if partially written or left on disk
                if (File.Exists(targetPath))
                {
                    try
                    {
                        File.Delete(targetPath);
                    }
                    catch { }
                }

                result.FailedFiles.Add((sourcePath, $"Copy or registration failed: {ex.Message}"));
            }
        }

        // If any tracks were imported, update settings atomically
        if (result.ImportedTracks.Count > 0)
        {
            try
            {
                // Auto-select the last imported track
                var last = result.ImportedTracks[^1];
                settings.MeditationSoundscape = last.Id;
                UiSettings.LastSelectedCustomSoundscapeId = last.Id;
                UiSettingsStore.Save(settings);
                SoundscapeCatalog.NotifyCatalogChanged();
            }
            catch (Exception ex)
            {
                result.FailedFiles.Add(("<settings>", $"Failed to persist settings: {ex.Message}"));
            }
        }

        return result;
    }

    /// <summary>
    /// Safely deletes a custom soundtrack from settings and removes its physical file from disk.
    /// If the deleted track is currently selected, reverts selection to Rain.
    /// </summary>
    public static bool DeleteCustomTrack(string trackId, UiSettings settings, string? customAudioDir = null)
    {
        if (string.IsNullOrWhiteSpace(trackId) || settings.CustomSoundtracks == null)
        {
            return false;
        }

        var meta = settings.CustomSoundtracks.FirstOrDefault(t =>
            string.Equals(t.Id, trackId, StringComparison.OrdinalIgnoreCase));
        if (meta == null)
        {
            return false;
        }

        settings.CustomSoundtracks.Remove(meta);

        if (string.Equals(settings.MeditationSoundscape, trackId, StringComparison.OrdinalIgnoreCase))
        {
            settings.MeditationSoundscape = SoundscapeCatalog.RainId;
        }

        if (string.Equals(UiSettings.LastSelectedCustomSoundscapeId, trackId, StringComparison.OrdinalIgnoreCase))
        {
            UiSettings.LastSelectedCustomSoundscapeId = null;
        }

        UiSettingsStore.Save(settings);
        SoundscapeCatalog.NotifyCatalogChanged();

        // Remove file from disk
        try
        {
            string dir = customAudioDir ?? SoundscapeCatalog.GetCustomAudioDirectory();
            string safeFileName = Path.GetFileName(meta.FileName);
            string fullPath = Path.Combine(dir, safeFileName);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch
        {
            // Best effort file deletion
        }

        return true;
    }

    /// <summary>
    /// Renames a custom soundtrack's display name and persists the change.
    /// </summary>
    public static bool RenameCustomTrack(string trackId, string newDisplayName, UiSettings settings)
    {
        if (string.IsNullOrWhiteSpace(trackId) || settings.CustomSoundtracks == null)
        {
            return false;
        }

        var meta = settings.CustomSoundtracks.FirstOrDefault(t =>
            string.Equals(t.Id, trackId, StringComparison.OrdinalIgnoreCase));
        if (meta == null)
        {
            return false;
        }

        meta.DisplayName = SoundscapeCatalog.SanitizeDisplayName(newDisplayName);
        UiSettingsStore.Save(settings);
        SoundscapeCatalog.NotifyCatalogChanged();
        return true;
    }
}
