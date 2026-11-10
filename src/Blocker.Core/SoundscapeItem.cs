using System;

namespace Blocker.Core;

/// <summary>
/// Legacy soundscape type enum maintained for backward-compatibility with saved presets and IPC.
/// </summary>
public enum SoundscapeType
{
    None,
    Rain,
    Library,
    Forest,
    SingingBowls,
    Custom
}

/// <summary>
/// Represents a selectable soundscape track (built-in preset or user-imported custom track).
/// </summary>
public class SoundscapeItem
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = "music";
    public bool IsBuiltIn { get; set; }
    public string? FilePath { get; set; }
    public double VolumeTrim { get; set; } = 1.0;
    public bool Loop { get; set; } = true;
    public TimeSpan? Duration { get; set; }
    public long? FileSizeBytes { get; set; }

    public SoundscapeItem()
    {
    }

    public SoundscapeItem(
        string id,
        string displayName,
        string iconGlyph,
        bool isBuiltIn,
        string? filePath = null,
        double volumeTrim = 1.0,
        bool loop = true,
        TimeSpan? duration = null,
        long? fileSizeBytes = null)
    {
        Id = id ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
        IconGlyph = iconGlyph ?? "music";
        IsBuiltIn = isBuiltIn;
        FilePath = filePath;
        VolumeTrim = Math.Clamp(volumeTrim, 0.0, 1.0);
        Loop = loop;
        Duration = duration;
        FileSizeBytes = fileSizeBytes;
    }

    public override string ToString() => $"{DisplayName} ({Id})";
}
