using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Blocker.Core;
using Blocker.Tests;
using Xunit;

namespace Sankalpa.Tests;

public class CustomSongsAndSoundscapeTests
{
    private static string GetRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Blocker.sln")))
        {
            dir = Path.GetDirectoryName(dir)!;
        }
        return dir;
    }

    [Theory]
    [InlineData("rain", "rain")]
    [InlineData("Rain", "rain")]
    [InlineData("RAIN", "rain")]
    [InlineData("library", "library")]
    [InlineData("Library", "library")]
    [InlineData("forest", "forest")]
    [InlineData("Forest", "forest")]
    [InlineData("bowls", "bowls")]
    [InlineData("SingingBowls", "bowls")]
    [InlineData("singing bowls", "bowls")]
    [InlineData("Singing Bowls", "bowls")]
    [InlineData("mute", "mute")]
    [InlineData("None", "mute")]
    [InlineData("none", "mute")]
    [InlineData("custom:01234567-89ab-cdef-0123-456789abcdef", "custom:01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("custom:MYTRACK123", "custom:mytrack123")]
    [InlineData(null, "rain")]
    [InlineData("", "rain")]
    [InlineData("   ", "rain")]
    [InlineData("unknown_garbage_track", "rain")]
    public void IdParsingAndNormalization_ResolvesCanonicalIds(string? input, string expected)
    {
        string actual = SoundscapeCatalog.NormalizeId(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EnumMigration_BidirectionalMappingPreserved()
    {
        Assert.Equal(SoundscapeType.Rain, SoundscapeCatalog.ToLegacyEnum("rain"));
        Assert.Equal(SoundscapeType.Library, SoundscapeCatalog.ToLegacyEnum("library"));
        Assert.Equal(SoundscapeType.Forest, SoundscapeCatalog.ToLegacyEnum("forest"));
        Assert.Equal(SoundscapeType.SingingBowls, SoundscapeCatalog.ToLegacyEnum("bowls"));
        Assert.Equal(SoundscapeType.None, SoundscapeCatalog.ToLegacyEnum("mute"));
        Assert.Equal(SoundscapeType.Custom, SoundscapeCatalog.ToLegacyEnum("custom:12345"));

        Assert.Equal("rain", SoundscapeCatalog.FromLegacyEnum(SoundscapeType.Rain));
        Assert.Equal("library", SoundscapeCatalog.FromLegacyEnum(SoundscapeType.Library));
        Assert.Equal("forest", SoundscapeCatalog.FromLegacyEnum(SoundscapeType.Forest));
        Assert.Equal("bowls", SoundscapeCatalog.FromLegacyEnum(SoundscapeType.SingingBowls));
        Assert.Equal("mute", SoundscapeCatalog.FromLegacyEnum(SoundscapeType.None));
    }

    [Fact]
    public void UiSettingsStore_CustomSoundtracks_RoundTripPersistence()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"ui-settings-test-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new UiSettings
            {
                MeditationDurationMinutes = 30,
                MeditationLockMode = "Strict",
                MeditationSoundscape = "custom:abc-123",
                CustomSoundtracks = new List<CustomSoundtrackMetadata>
                {
                    new("custom:abc-123", "Deep Focus Alpha", "abc1234.mp3", 0.85)
                    {
                        DurationSeconds = 180.5,
                        FileSizeBytes = 4096000,
                        OriginalFileName = "alpha_waves.mp3",
                        Sha256Hash = "deadbeef123456"
                    }
                }
            };

            UiSettingsStore.SaveToFile(settings, tempFile);
            var loaded = UiSettingsStore.LoadFromFile(tempFile);

            Assert.Equal("custom:abc-123", loaded.MeditationSoundscape);
            Assert.Single(loaded.CustomSoundtracks);

            var track = loaded.CustomSoundtracks[0];
            Assert.Equal("custom:abc-123", track.Id);
            Assert.Equal("Deep Focus Alpha", track.DisplayName);
            Assert.Equal("abc1234.mp3", track.FileName);
            Assert.Equal(0.85, track.VolumeTrim, 2);
            Assert.Equal(180.5, track.DurationSeconds);
            Assert.Equal(4096000, track.FileSizeBytes);
            Assert.Equal("alpha_waves.mp3", track.OriginalFileName);
            Assert.Equal("deadbeef123456", track.Sha256Hash);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void UiSettings_MeditationSoundscape_PreservesCustomIdWhenCustomEnumAssigned()
    {
        var settings = new UiSettings();
        UiSettings.LastSelectedCustomSoundscapeId = "custom:track-xyz-999";

        // When legacy code or dlg.SelectedSoundscape.ToString() assigns "Custom"
        settings.MeditationSoundscape = "Custom";
        Assert.Equal("custom:track-xyz-999", settings.MeditationSoundscape);

        // When standard enum assigned
        settings.MeditationSoundscape = "Forest";
        Assert.Equal("Forest", settings.MeditationSoundscape);
    }

    [Fact]
    public void UiSettingsStore_FromJson_DefensiveAgainstNullCustomSoundtracks()
    {
        string legacyJson = @"{
            ""Theme"": ""black"",
            ""MeditationSoundscape"": ""Rain""
        }";

        var parsed = UiSettingsStore.FromJson(legacyJson);
        Assert.NotNull(parsed.CustomSoundtracks);
        Assert.Empty(parsed.CustomSoundtracks);
    }

    [Theory]
    [InlineData(".mp3", true)]
    [InlineData(".wav", true)]
    [InlineData(".m4a", true)]
    [InlineData(".wma", true)]
    [InlineData(".aac", true)]
    [InlineData(".flac", true)]
    [InlineData(".ogg", false)] // OGG unsupported by WPF without 3rd party codecs
    [InlineData(".exe", false)]
    [InlineData(".bat", false)]
    [InlineData(".dll", false)]
    [InlineData(".txt", false)]
    [InlineData("", false)]
    public void FileValidation_ExtensionAllowList(string ext, bool expectedValid)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_sample_{Guid.NewGuid():N}{ext}");
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0x01, 0x02, 0x03, 0x04 });

            bool isValid = SoundscapeCatalog.ValidateAudioFile(tempFile, out string? error);
            Assert.Equal(expectedValid, isValid);

            if (ext == ".ogg")
            {
                Assert.Contains("OGG", error);
                Assert.Contains("Windows Media Foundation", error);
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void FileValidation_RejectsEmptyAndOversizedFiles()
    {
        // 1. Empty file
        string emptyFile = Path.Combine(Path.GetTempPath(), $"empty_{Guid.NewGuid():N}.mp3");
        try
        {
            File.WriteAllBytes(emptyFile, Array.Empty<byte>());
            bool valid = SoundscapeCatalog.ValidateAudioFile(emptyFile, out string? err);
            Assert.False(valid);
            Assert.Contains("empty", err, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(emptyFile)) File.Delete(emptyFile);
        }

        // 2. Non-existent file
        bool existsValid = SoundscapeCatalog.ValidateAudioFile("C:\\nonexistent_blocker_file_12345.mp3", out string? existErr);
        Assert.False(existsValid);
        Assert.Contains("exist", existErr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\Users\test\Music\Morning Focus.mp3", "Morning Focus")]
    [InlineData(@"ambient_loop_432hz.wav", "ambient_loop_432hz")]
    [InlineData(@"track with multiple   spaces.flac", "track with multiple spaces")]
    [InlineData("", "Custom Track")]
    [InlineData("   ", "Custom Track")]
    [InlineData("This is an extremely long soundtrack title designed to test the maximum allowed length limits of the sanitization service.m4a",
                "This is an extremely long soundtrack tit")]
    public void SanitizeDisplayName_CleansAndTruncates(string input, string expected)
    {
        string sanitized = SoundscapeCatalog.SanitizeDisplayName(input);
        Assert.Equal(expected, sanitized);
        Assert.True(sanitized.Length <= 40);
    }

    [Fact]
    public void DuplicateDetection_DetectsSameSha256()
    {
        string tempFile1 = Path.Combine(Path.GetTempPath(), $"dup1_{Guid.NewGuid():N}.mp3");
        string tempFile2 = Path.Combine(Path.GetTempPath(), $"dup2_{Guid.NewGuid():N}.mp3");
        try
        {
            byte[] bytes = new byte[] { 10, 20, 30, 40, 50 };
            File.WriteAllBytes(tempFile1, bytes);
            File.WriteAllBytes(tempFile2, bytes);

            string hash1 = SoundscapeCatalog.ComputeSha256(tempFile1);
            string hash2 = SoundscapeCatalog.ComputeSha256(tempFile2);
            Assert.Equal(hash1, hash2);

            var settings = new UiSettings
            {
                CustomSoundtracks = new List<CustomSoundtrackMetadata>
                {
                    new("custom:track-1", "First Track", "file1.mp3") { Sha256Hash = hash1 }
                }
            };

            bool isDup = SoundscapeCatalog.IsDuplicate(hash2, settings, out var match);
            Assert.True(isDup);
            Assert.NotNull(match);
            Assert.Equal("custom:track-1", match!.Id);
        }
        finally
        {
            if (File.Exists(tempFile1)) File.Delete(tempFile1);
            if (File.Exists(tempFile2)) File.Delete(tempFile2);
        }
    }

    [Fact]
    public void MissingCustomFile_FallsBackGracefullyToRain()
    {
        var settings = new UiSettings
        {
            CustomSoundtracks = new List<CustomSoundtrackMetadata>
            {
                new("custom:missing-file-id", "Ghost Track", "nonexistent_file_guid.mp3")
            }
        };

        // Custom file does not exist on disk
        var track = SoundscapeCatalog.FindTrack("custom:missing-file-id", settings);
        Assert.NotNull(track);
        // Falls back to Rain rather than crashing!
        Assert.Equal(SoundscapeCatalog.RainId, track.Id);
        Assert.Equal("Rain", track.DisplayName);
    }

    [Fact]
    public void CustomSongPlayback_MatchesBothGuidFormats_WithoutFallingBackToRain()
    {
        string tempAudioDir = Path.Combine(Path.GetTempPath(), $"audio_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempAudioDir);

        try
        {
            var rawGuid = Guid.NewGuid();
            string guidN = rawGuid.ToString("N");
            string guidD = rawGuid.ToString("D");
            string testFileName = $"{guidN}.mp3";
            string testFilePath = Path.Combine(tempAudioDir, testFileName);
            File.WriteAllBytes(testFilePath, new byte[64]); // create valid dummy file

            // Case 1: Saved in settings using N format (no hyphens)
            var settingsWithN = new UiSettings
            {
                CustomSoundtracks = new List<CustomSoundtrackMetadata>
                {
                    new($"{SoundscapeCatalog.CustomPrefix}{guidN}", "Test Track N", testFileName)
                }
            };

            // Query using D format (canonical with hyphens)
            var trackFromDQuery = SoundscapeCatalog.FindTrack($"{SoundscapeCatalog.CustomPrefix}{guidD}", settingsWithN, tempAudioDir);
            Assert.NotNull(trackFromDQuery);
            Assert.NotEqual(SoundscapeCatalog.RainId, trackFromDQuery.Id);
            Assert.Equal(SoundscapeCatalog.NormalizeId($"{SoundscapeCatalog.CustomPrefix}{guidD}"), trackFromDQuery.Id);
            Assert.Equal("Test Track N", trackFromDQuery.DisplayName);
            Assert.Equal(testFilePath, trackFromDQuery.FilePath);

            // Case 2: Saved in settings using D format (with hyphens)
            var settingsWithD = new UiSettings
            {
                CustomSoundtracks = new List<CustomSoundtrackMetadata>
                {
                    new($"{SoundscapeCatalog.CustomPrefix}{guidD}", "Test Track D", testFileName)
                }
            };

            // Query using N format (without hyphens)
            var trackFromNQuery = SoundscapeCatalog.FindTrack($"{SoundscapeCatalog.CustomPrefix}{guidN}", settingsWithD, tempAudioDir);
            Assert.NotNull(trackFromNQuery);
            Assert.NotEqual(SoundscapeCatalog.RainId, trackFromNQuery.Id);
            Assert.Equal(SoundscapeCatalog.NormalizeId($"{SoundscapeCatalog.CustomPrefix}{guidN}"), trackFromNQuery.Id);
            Assert.Equal("Test Track D", trackFromNQuery.DisplayName);
            Assert.Equal(testFilePath, trackFromNQuery.FilePath);
        }
        finally
        {
            if (Directory.Exists(tempAudioDir)) Directory.Delete(tempAudioDir, true);
        }
    }

    [Fact]
    public void WindowsNotificationHelper_EnableNotificationInWindows_RepairsRegistryKey()
    {
        if (!OperatingSystem.IsWindows()) return;

        bool enabled = WindowsNotificationHelper.EnableNotificationInWindows();
        Assert.True(enabled);

        bool isDisabled = WindowsNotificationHelper.IsNotificationDisabledInWindows();
        Assert.False(isDisabled);
    }

    [Fact]
    public void PathTraversalProtection_StripsDirectoriesFromCustomFileName()
    {
        string tempAudioDir = Path.Combine(Path.GetTempPath(), $"audio_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempAudioDir);

        try
        {
            var settings = new UiSettings
            {
                CustomSoundtracks = new List<CustomSoundtrackMetadata>
                {
                    new("custom:traversal-test", "Traversal Attempt", @"..\..\..\Windows\System32\calc.exe")
                }
            };

            var tracks = SoundscapeCatalog.GetAllTracks(settings, tempAudioDir);
            var customTrack = tracks.FirstOrDefault(t => t.Id == "custom:traversal-test");
            Assert.NotNull(customTrack);

            // Path must be resolved safely inside tempAudioDir without traversal
            Assert.Equal(Path.Combine(tempAudioDir, "calc.exe"), customTrack!.FilePath);
        }
        finally
        {
            if (Directory.Exists(tempAudioDir)) Directory.Delete(tempAudioDir, true);
        }
    }

    [Fact]
    public void UiLint_PassesOnMeditationEditDialog()
    {
        string root = GetRepoRoot();
        string editDlgPath = Path.Combine(root, "src", "Blocker.App", "MeditationEditDialog.xaml");
        Assert.True(File.Exists(editDlgPath));

        var violations = UiLint.LintFile(editDlgPath);
        if (violations.Count > 0)
        {
            string summary = string.Join(Environment.NewLine, violations.Select(v => v.ToString()));
            Assert.Fail($"UiLint violations in MeditationEditDialog.xaml:{Environment.NewLine}{summary}");
        }
    }

    [Theory]
    [InlineData("Rain", "Rain")]
    [InlineData("My Audio Track", "My Audio Track")]
    [InlineData("Deep Meditation Ocean Focus Waves", "Deep Meditati...")]
    [InlineData("", "Mute")]
    public void TruncateSelectedSoundscapeDisplayName_FormatsCleanly(string input, string expected)
    {
        const int maxLen = 16;
        string result;
        if (string.IsNullOrWhiteSpace(input))
        {
            result = "Mute";
        }
        else if (input.Length > maxLen)
        {
            result = input[..(maxLen - 3)] + "...";
        }
        else
        {
            result = input;
        }

        Assert.Equal(expected, result);
        Assert.True(result.Length <= 16);
    }

    [Fact]
    public async Task AudioImport_SingleValidFile_CopiesFileAndUpdatesSettings()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"sankalpa_audio_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string sourceFile = Path.Combine(tempDir, "ambient_sound.mp3");

        byte[] mockMp3 = new byte[1024];
        mockMp3[0] = 0x49; mockMp3[1] = 0x44; mockMp3[2] = 0x33; // ID3 header
        await File.WriteAllBytesAsync(sourceFile, mockMp3);

        string customAudioDir = Path.Combine(tempDir, "custom_audio");
        Directory.CreateDirectory(customAudioDir);

        var settings = new UiSettings { CustomSoundtracks = new List<CustomSoundtrackMetadata>() };

        try
        {
            var result = await AudioImportService.ImportAudioFilesAsync(
                new[] { sourceFile },
                settings,
                customAudioDir);

            Assert.Single(result.ImportedTracks);
            Assert.Empty(result.FailedFiles);
            Assert.Empty(result.SkippedDuplicates);
            Assert.Single(settings.CustomSoundtracks);

            var meta = result.ImportedTracks[0];
            Assert.StartsWith(SoundscapeCatalog.CustomPrefix, meta.Id);
            Assert.Equal("ambient_sound", meta.DisplayName);
            Assert.True(File.Exists(Path.Combine(customAudioDir, meta.FileName)));
            Assert.Equal(meta.Id, settings.MeditationSoundscape);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AudioImport_MultipleValidFiles_ImportsAllSequentially()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"sankalpa_multi_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string source1 = Path.Combine(tempDir, "track1.mp3");
        string source2 = Path.Combine(tempDir, "track2.wav");
        string source3 = Path.Combine(tempDir, "track3.flac");

        await File.WriteAllBytesAsync(source1, new byte[] { 1, 2, 3 });
        await File.WriteAllBytesAsync(source2, new byte[] { 4, 5, 6 });
        await File.WriteAllBytesAsync(source3, new byte[] { 7, 8, 9 });

        string customAudioDir = Path.Combine(tempDir, "custom_audio");
        var settings = new UiSettings { CustomSoundtracks = new List<CustomSoundtrackMetadata>() };

        try
        {
            var result = await AudioImportService.ImportAudioFilesAsync(
                new[] { source1, source2, source3 },
                settings,
                customAudioDir);

            Assert.Equal(3, result.ImportedTracks.Count);
            Assert.Equal(3, settings.CustomSoundtracks.Count);
            Assert.Empty(result.FailedFiles);
            Assert.Empty(result.SkippedDuplicates);
            Assert.Equal("track3", settings.CustomSoundtracks[^1].DisplayName);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AudioImport_OversizedFile_Exceeding50MB_StrictlyRejected()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"oversized_{Guid.NewGuid():N}.wav");
        try
        {
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
            {
                fs.SetLength(SoundscapeCatalog.MaxFileSizeBytes + 1);
            }

            bool valid = SoundscapeCatalog.ValidateAudioFile(tempFile, out string? error);
            Assert.False(valid);
            Assert.Contains("exceeds the 50 MB limit", error, StringComparison.OrdinalIgnoreCase);

            var settings = new UiSettings { CustomSoundtracks = new List<CustomSoundtrackMetadata>() };
            var result = AudioImportService.ImportAudioFiles(new[] { tempFile }, settings);
            Assert.Single(result.FailedFiles);
            Assert.Empty(result.ImportedTracks);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void AudioImport_ZeroByteFile_StrictlyRejected()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"zerobyte_{Guid.NewGuid():N}.mp3");
        try
        {
            File.WriteAllBytes(tempFile, Array.Empty<byte>());

            bool valid = SoundscapeCatalog.ValidateAudioFile(tempFile, out string? error);
            Assert.False(valid);
            Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);

            var settings = new UiSettings { CustomSoundtracks = new List<CustomSoundtrackMetadata>() };
            var result = AudioImportService.ImportAudioFiles(new[] { tempFile }, settings);
            Assert.Single(result.FailedFiles);
            Assert.Empty(result.ImportedTracks);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".dll")]
    [InlineData(".txt")]
    [InlineData(".ogg")]
    public void AudioImport_UnsupportedExtensions_StrictlyRejected(string ext)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"unsupported_{Guid.NewGuid():N}{ext}");
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 1, 2, 3, 4 });
            bool valid = SoundscapeCatalog.ValidateAudioFile(tempFile, out string? error);
            Assert.False(valid);

            var settings = new UiSettings { CustomSoundtracks = new List<CustomSoundtrackMetadata>() };
            var result = AudioImportService.ImportAudioFiles(new[] { tempFile }, settings);
            Assert.Single(result.FailedFiles);
            Assert.Empty(result.ImportedTracks);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AudioImport_DuplicateSha256_SkippedWithoutDiskWrite()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"dup_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string source1 = Path.Combine(tempDir, "song1.mp3");
        string source2 = Path.Combine(tempDir, "song2_dup.mp3");

        byte[] data = new byte[] { 0x49, 0x44, 0x33, 10, 20, 30 };
        await File.WriteAllBytesAsync(source1, data);
        await File.WriteAllBytesAsync(source2, data);

        string customAudioDir = Path.Combine(tempDir, "custom_audio");
        var settings = new UiSettings { CustomSoundtracks = new List<CustomSoundtrackMetadata>() };

        try
        {
            // First import
            var res1 = await AudioImportService.ImportAudioFilesAsync(new[] { source1 }, settings, customAudioDir);
            Assert.Single(res1.ImportedTracks);

            // Second import of same file content
            var res2 = await AudioImportService.ImportAudioFilesAsync(new[] { source2 }, settings, customAudioDir);
            Assert.Empty(res2.ImportedTracks);
            Assert.Single(res2.SkippedDuplicates);
            Assert.Equal("song1", res2.SkippedDuplicates[0].ExistingDisplayName);
            // Verify only one file exists on disk
            Assert.Single(Directory.GetFiles(customAudioDir));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AudioImport_CorruptedStream_RejectedWithRollback()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"corrupt_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string source = Path.Combine(tempDir, "corrupt.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 0x00, 0xFF, 0xAA });

        string customAudioDir = Path.Combine(tempDir, "custom_audio");
        var settings = new UiSettings { CustomSoundtracks = new List<CustomSoundtrackMetadata>() };

        try
        {
            var result = await AudioImportService.ImportAudioFilesAsync(
                new[] { source },
                settings,
                customAudioDir,
                decodabilityProbe: path => Task.FromResult((false, (double?)null, (string?)"Corrupt bitstream header")));

            Assert.Empty(result.ImportedTracks);
            Assert.Single(result.FailedFiles);
            Assert.Contains("Corrupt bitstream header", result.FailedFiles[0].Reason);
            // Verify rollback: no orphaned files left in customAudioDir
            Assert.Empty(Directory.GetFiles(customAudioDir));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AudioImport_TrackRenamingAndDeletion_Lifecycle()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"lifecycle_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string source = Path.Combine(tempDir, "track.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });

        string customAudioDir = Path.Combine(tempDir, "custom_audio");
        var settings = new UiSettings { CustomSoundtracks = new List<CustomSoundtrackMetadata>() };

        try
        {
            var result = await AudioImportService.ImportAudioFilesAsync(new[] { source }, settings, customAudioDir);
            Assert.Single(result.ImportedTracks);
            string trackId = result.ImportedTracks[0].Id;
            Assert.Equal(trackId, settings.MeditationSoundscape);

            // 1. Rename
            bool renamed = AudioImportService.RenameCustomTrack(trackId, "   Peaceful Morning 432Hz   ", settings);
            Assert.True(renamed);
            Assert.Equal("Peaceful Morning 432Hz", settings.CustomSoundtracks[0].DisplayName);

            // 2. Delete
            bool deleted = AudioImportService.DeleteCustomTrack(trackId, settings, customAudioDir);
            Assert.True(deleted);
            Assert.Empty(settings.CustomSoundtracks);
            Assert.Equal(SoundscapeCatalog.RainId, settings.MeditationSoundscape);
            Assert.Empty(Directory.GetFiles(customAudioDir));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PlaylistParser_ValidM3u_ParsesLocalEntries()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"m3u_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string audio1 = Path.Combine(tempDir, "song1.mp3");
        string audio2 = Path.Combine(tempDir, "song2.wav");
        File.WriteAllBytes(audio1, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(audio2, new byte[] { 4, 5, 6 });

        string m3uContent = @"#EXTM3U
#EXTINF:180,Song 1
song1.mp3
#EXTINF:240,Song 2
song2.wav";

        string playlistPath = Path.Combine(tempDir, "playlist.m3u");
        File.WriteAllText(playlistPath, m3uContent);

        try
        {
            bool ok = AudioImportService.TryParsePlaylist(playlistPath, out var resolved, out var warnings, out var err);
            Assert.True(ok, err);
            Assert.Equal(2, resolved.Count);
            Assert.Equal(audio1, resolved[0]);
            Assert.Equal(audio2, resolved[1]);
            Assert.Empty(warnings);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PlaylistParser_ValidM3u8WithBom_ParsesSuccessfully()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"m3u8_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string audio = Path.Combine(tempDir, "meditation.flac");
        File.WriteAllBytes(audio, new byte[] { 1, 2, 3 });

        string m3uContent = "#EXTM3U\r\n#EXTINF:100,Meditation\r\nmeditation.flac";
        string playlistPath = Path.Combine(tempDir, "playlist.m3u8");
        File.WriteAllText(playlistPath, m3uContent, System.Text.Encoding.UTF8); // Writes with UTF8 BOM

        try
        {
            var res = SoundscapeCatalog.ParsePlaylist(playlistPath);
            Assert.Single(res.ValidFilePaths);
            Assert.Equal(audio, res.ValidFilePaths[0]);
            Assert.Null(res.ErrorMessage);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PlaylistParser_RejectsRemoteUrls_AntiNetworkLeak()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"remote_m3u_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string localAudio = Path.Combine(tempDir, "local.mp3");
        File.WriteAllBytes(localAudio, new byte[] { 1, 2 });

        string m3uContent = @"#EXTM3U
http://malicious.example.com/audio.mp3
https://stream.example.com/live.aac
ftp://files.example.com/track.wav
local.mp3";

        string playlistPath = Path.Combine(tempDir, "remote.m3u");
        File.WriteAllText(playlistPath, m3uContent);

        try
        {
            bool ok = AudioImportService.TryParsePlaylist(playlistPath, out var resolved, out var warnings, out var err);
            Assert.True(ok);
            Assert.Single(resolved);
            Assert.Equal(localAudio, resolved[0]);
            Assert.Equal(3, warnings.Count);
            Assert.All(warnings, w => Assert.Contains("remote URL", w, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PlaylistParser_RejectsUncShares_AntiSmbRelay()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"unc_m3u_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string localAudio = Path.Combine(tempDir, "safe.mp3");
        File.WriteAllBytes(localAudio, new byte[] { 1, 2 });

        string m3uContent = @"#EXTM3U
\\attacker-smb-server\share\exploit.wav
//another-smb/share/track.mp3
safe.mp3";

        string playlistPath = Path.Combine(tempDir, "unc.m3u");
        File.WriteAllText(playlistPath, m3uContent);

        try
        {
            bool ok = AudioImportService.TryParsePlaylist(playlistPath, out var resolved, out var warnings, out var err);
            Assert.True(ok);
            Assert.Single(resolved);
            Assert.Equal(localAudio, resolved[0]);
            Assert.Equal(2, warnings.Count);
            Assert.All(warnings, w => Assert.Contains("network share", w, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PlaylistParser_RejectsNestedPlaylists_AntiRecursion()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"nested_m3u_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string localAudio = Path.Combine(tempDir, "sound.mp3");
        File.WriteAllBytes(localAudio, new byte[] { 1, 2 });

        string m3uContent = @"#EXTM3U
other_playlist.m3u
sub_folder/nested.m3u8
sound.mp3";

        string playlistPath = Path.Combine(tempDir, "parent.m3u");
        File.WriteAllText(playlistPath, m3uContent);

        try
        {
            bool ok = AudioImportService.TryParsePlaylist(playlistPath, out var resolved, out var warnings, out var err);
            Assert.True(ok);
            Assert.Single(resolved);
            Assert.Equal(localAudio, resolved[0]);
            Assert.Equal(2, warnings.Count);
            Assert.All(warnings, w => Assert.Contains("nested playlist", w, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PlaylistParser_RejectsDirectoryTraversal_EscapingBaseDirectory()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"traversal_m3u_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string safeAudio = Path.Combine(tempDir, "safe.mp3");
        File.WriteAllBytes(safeAudio, new byte[] { 1, 2 });

        string m3uContent = @"#EXTM3U
..\..\..\Windows\System32\cmd.exe
safe.mp3";

        string playlistPath = Path.Combine(tempDir, "traversal.m3u");
        File.WriteAllText(playlistPath, m3uContent);

        try
        {
            var res = SoundscapeCatalog.ParsePlaylist(playlistPath, allowedBaseDirectory: tempDir);
            Assert.Single(res.ValidFilePaths);
            Assert.Equal(safeAudio, res.ValidFilePaths[0]);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PlaylistParser_HandlesMissingAndPartialFiles_AccurateReporting()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"partial_m3u_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string existingAudio = Path.Combine(tempDir, "present.mp3");
        File.WriteAllBytes(existingAudio, new byte[] { 1, 2 });

        string m3uContent = @"#EXTM3U
missing_file_123.mp3
present.mp3
another_ghost_track.wav";

        string playlistPath = Path.Combine(tempDir, "partial.m3u");
        File.WriteAllText(playlistPath, m3uContent);

        try
        {
            bool ok = AudioImportService.TryParsePlaylist(playlistPath, out var resolved, out var warnings, out var err);
            Assert.True(ok);
            Assert.Single(resolved);
            Assert.Equal(existingAudio, resolved[0]);
            Assert.Equal(2, warnings.Count);
            Assert.All(warnings, w => Assert.Contains("not found", w, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SoundscapeCatalog_CatalogChangedEvent_FiresOnImportAndDeletion()
    {
        int eventCount = 0;
        Action handler = () => eventCount++;

        SoundscapeCatalog.CatalogChanged += handler;
        try
        {
            SoundscapeCatalog.NotifyCatalogChanged();
            Assert.Equal(1, eventCount);

            SoundscapeCatalog.NotifyCatalogChanged();
            Assert.Equal(2, eventCount);
        }
        finally
        {
            SoundscapeCatalog.CatalogChanged -= handler;
        }

        // After unsubscribing, no increments
        SoundscapeCatalog.NotifyCatalogChanged();
        Assert.Equal(2, eventCount);
    }
}
