namespace Lanyard.Shared.DTO;

/// <summary>
/// One projection window the kiosk has open, returned by <c>ClientHubMethods.ListProjectionWindows</c>.
/// </summary>
public class ClientProjectionWindowDTO
{
    /// <summary>The Windows device name of the screen, e.g. \\.\DISPLAY1. Used to ask for its capture.</summary>
    public string DisplayKey { get; set; } = string.Empty;

    /// <summary>The screen's position in the kiosk's screen list, matching ClientProjectionSettings.DisplayIndex.</summary>
    public int DisplayIndex { get; set; }

    public Guid ProgramId { get; set; }

    /// <summary>True while the window plays the kiosk's offline copy rather than the live page.</summary>
    public bool IsOffline { get; set; }

    /// <summary>False when the window's browser never finished starting, so it can't be captured.</summary>
    public bool IsLoaded { get; set; }
}

/// <summary>
/// Sent by the server with <c>ClientHubMethods.CaptureProjectionWindow</c>. The kiosk clamps both
/// limits to its own, so a bad request can't make it reply with more than the hub will accept.
/// </summary>
public class ClientScreenshotRequestDTO
{
    public string DisplayKey { get; set; } = string.Empty;

    public int MaxWidth { get; set; }

    public int MaxBytes { get; set; }
}

/// <summary>
/// The kiosk's reply to <c>ClientHubMethods.CaptureProjectionWindow</c>: a JPEG of what one
/// projection window shows, or the reason it couldn't be captured.
/// </summary>
public class ClientScreenshotDTO
{
    public string DisplayKey { get; set; } = string.Empty;

    /// <summary>Null when the capture failed; <see cref="Error"/> then says why.</summary>
    public byte[]? ImageJpeg { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public string? Error { get; set; }

    public string? ClientVersion { get; set; }

    public DateTime CapturedAtUtc { get; set; }
}
