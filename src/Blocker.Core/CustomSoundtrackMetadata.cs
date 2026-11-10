using System;

namespace Blocker.Core;

/// <summary>
/// Persisted metadata for user-imported custom audio soundtracks.
/// </summary>
public class CustomSoundtrackMetadata
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public double VolumeTrim { get; set; } = 1.0;
    public double? DurationSeconds { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? OriginalFileName { get; set; }
    public string? Sha256Hash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public CustomSoundtrackMetadata()
    {
    }

    public CustomSoundtrackMetadata(string id, string displayName, string fileName, double volumeTrim = 1.0)
    {
        Id = id ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
        FileName = fileName ?? string.Empty;
        VolumeTrim = Math.Clamp(volumeTrim, 0.0, 1.0);
    }
}
