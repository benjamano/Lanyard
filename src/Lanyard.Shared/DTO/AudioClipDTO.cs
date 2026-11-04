using Lanyard.Shared.Enum;

namespace Lanyard.Shared.DTO;

/// <summary>
/// Tells a kiosk to play an audio clip over its music. The kiosk ducks the music itself, so the
/// server's idea of the kiosk's volume (and the music schedule's) never changes.
/// </summary>
public class AudioClipPlayCommand
{
    // Identifies this one play on this one kiosk; the kiosk's reports carry it back.
    public Guid PlayId { get; set; }
    public Guid ClipId { get; set; }

    // Changes whenever the clip's audio is replaced, so a kiosk never plays a stale cached copy.
    public string ContentVersion { get; set; } = string.Empty;

    // One of AudioClipFormats.Extensions, e.g. ".mp3".
    public string FileExtension { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }

    // 0-100: how loud the clip itself plays.
    public int Volume { get; set; } = 100;

    // 0-100: the music's level while the clip plays, as a share of its normal level.
    public int DuckToPercent { get; set; }
    public int FadeDownMs { get; set; }
    public int FadeUpMs { get; set; }
}

public class AudioClipPlaybackReport
{
    public Guid PlayId { get; set; }
    public AudioClipPlaybackStatus Status { get; set; }
    public string? Error { get; set; }
}
