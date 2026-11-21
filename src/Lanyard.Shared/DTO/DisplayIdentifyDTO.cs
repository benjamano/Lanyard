namespace Lanyard.Shared.DTO;

/// <summary>
/// Sent by the server with <c>ClientHubMethods.IdentifyDisplays</c>: put a numbered overlay on
/// every screen for <see cref="DurationSeconds"/>, showing each screen's program from
/// <see cref="Labels"/>. The kiosk clamps the duration, so a bad request can't leave it up for good.
/// </summary>
public class DisplayIdentifyRequestDTO
{
    public Guid RequestId { get; set; }

    public int DurationSeconds { get; set; }

    /// <summary>What the server has assigned to each screen index. A screen with no entry shows "No program assigned".</summary>
    public List<DisplayIdentifyLabelDTO> Labels { get; set; } = [];
}

public class DisplayIdentifyLabelDTO
{
    public int DisplayIndex { get; set; }

    public string? ProgramName { get; set; }
}

/// <summary>
/// The kiosk's reply to <c>ClientHubMethods.IdentifyDisplays</c>: the screens it found right now,
/// in the order it numbers them, and whether each overlay opened.
/// </summary>
public class DisplayIdentifyResultDTO
{
    public Guid RequestId { get; set; }

    public string? ClientVersion { get; set; }

    public List<IdentifiedDisplayDTO> Displays { get; set; } = [];
}

public class IdentifiedDisplayDTO
{
    /// <summary>The screen's position in the kiosk's screen list, matching ClientProjectionSettings.DisplayIndex.</summary>
    public int Index { get; set; }

    /// <summary>The Windows device name, e.g. \\.\DISPLAY1.</summary>
    public string DeviceName { get; set; } = string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public bool IsPrimary { get; set; }

    /// <summary>False when the overlay couldn't be opened on this screen; <see cref="Error"/> then says why.</summary>
    public bool Shown { get; set; }

    public string? Error { get; set; }
}
