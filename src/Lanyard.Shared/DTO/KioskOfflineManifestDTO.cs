namespace Lanyard.Shared.DTO;

/// <summary>
/// Everything a kiosk needs to keep its screens playing from disk while it can't reach the
/// server: each display's ambient program, already reduced to the steps that can play offline
/// (Show Text, Play a Video, Delay), plus this kiosk's offline settings.
/// </summary>
public class KioskOfflineManifestDTO
{
    /// <summary>
    /// A hash of the content below. Changes whenever anything a kiosk would play or download
    /// changes, so the kiosk only re-syncs when it has to.
    /// </summary>
    public string ManifestVersion { get; set; } = string.Empty;

    public DateTime GeneratedUtc { get; set; }

    public bool OfflineFallbackEnabled { get; set; }
    public int OfflineAfterSeconds { get; set; }
    public int CacheLimitMb { get; set; }

    public List<KioskOfflineDisplayDTO> Displays { get; set; } = [];
}

public class KioskOfflineDisplayDTO
{
    public int DisplayIndex { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsFullScreen { get; set; } = true;
    public bool IsBorderless { get; set; } = true;
    public bool IsDarkTheme { get; set; }

    public bool RepeatInfinitely { get; set; } = true;
    public int RepeatNumberOfTimes { get; set; }

    public Guid ProgramId { get; set; }
    public string ProgramName { get; set; } = string.Empty;

    /// <summary>Steps the server left out because they need it live (Dashboard, Live Capture).</summary>
    public int SkippedStepCount { get; set; }

    public List<KioskOfflineStepDTO> Steps { get; set; } = [];
}

public class KioskOfflineStepDTO
{
    public Guid StepId { get; set; }
    public int SortOrder { get; set; }

    /// <summary>One of ProjectionTemplateNames.ShowText, PlayVideo or Delay.</summary>
    public string TemplateName { get; set; } = string.Empty;

    public int HoldForMilliseconds { get; set; }

    public string? Text { get; set; }

    public Guid? VideoFileId { get; set; }
    public long? VideoSizeBytes { get; set; }
    public bool VideoShouldRepeat { get; set; }

    /// <summary>0-100, as the admin entered it.</summary>
    public int VideoVolume { get; set; } = 100;
}
