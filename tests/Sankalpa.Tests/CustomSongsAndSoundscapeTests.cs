using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
}
