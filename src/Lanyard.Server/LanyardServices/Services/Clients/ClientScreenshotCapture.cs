namespace Lanyard.Application.Services;

/// <summary>
/// Screenshots of a kiosk's projection windows as the server holds them, plus who asked for
/// them and when they arrived. Kept in memory only, see <see cref="IClientDiagnosticsStore"/>.
/// </summary>
public class ClientScreenshotCapture
{
    public Guid ClientId { get; init; }

    /// <summary>One per projection window, in screen order. Empty when nothing was projected.</summary>
    public required IReadOnlyList<ClientScreenSnapshot> Screens { get; init; }

    public required string RequestedBy { get; init; }

    public string? ClientVersion { get; init; }

    public DateTime ReceivedAtUtc { get; init; }

    public DateTime ExpiresAtUtc { get; init; }
}

public class ClientScreenSnapshot
{
    /// <summary>The screen's position on the kiosk, from 0. Negative when the kiosk couldn't tell.</summary>
    public int DisplayIndex { get; init; }

    /// <summary>Null when the program has since been deleted or belongs to no visible company.</summary>
    public string? ProgramName { get; init; }

    /// <summary>True while the screen plays the kiosk's offline copy.</summary>
    public bool IsOffline { get; init; }

    /// <summary>Null when the capture failed; <see cref="Error"/> says why.</summary>
    public byte[]? ImageJpeg { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string? Error { get; init; }
}
