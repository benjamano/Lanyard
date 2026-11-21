using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services;

public interface IDisplayIdentifyService
{
    /// <summary>
    /// Asks a connected kiosk to put a numbered overlay on every screen for a few seconds, showing
    /// each screen's number, device name, resolution and program, and returns the screens it found.
    /// Fails when the caller isn't an Admin of the kiosk's company, the kiosk is offline or too old,
    /// or doesn't reply.
    /// </summary>
    Task<Result<DisplayIdentifyView>> IdentifyAsync(Guid clientId);
}

public class DisplayIdentifyView
{
    public Guid ClientId { get; set; }

    public int DurationSeconds { get; set; }

    public string? ClientVersion { get; set; }

    public List<IdentifiedDisplayView> Displays { get; set; } = [];

    /// <summary>True when any screen's number now points at a different screen than Lanyard had stored.</summary>
    public bool AnyChanged => Displays.Any(x => x.Changed);
}

public class IdentifiedDisplayView
{
    public int Index { get; set; }

    /// <summary>Index + 1, as shown on the overlay.</summary>
    public int Number { get; set; }

    public string DeviceName { get; set; } = string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public bool IsPrimary { get; set; }

    public string? ProgramName { get; set; }

    public bool Shown { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// True when Lanyard had a different screen stored at this number, i.e. the kiosk's screens
    /// were reordered since it last reported them. Anything set up for this number now plays here.
    /// </summary>
    public bool Changed { get; set; }

    /// <summary>The screen Lanyard had stored at this number before, when <see cref="Changed"/>.</summary>
    public string? PreviousDeviceName { get; set; }
}
