namespace Lanyard.Shared.LaserGame.Capture;

/// <summary>
/// A recording of a laser game's control box traffic: one JSON object per line, UTF-8.
/// <list type="bullet">
/// <item>Line 1 is a <see cref="LaserCaptureHeader"/>, which says what made the file and how capture was set up.</item>
/// <item>Each following line is one game packet: <c>{"t":ms,"s":source,"d":destination,"p":hex payload}</c>.</item>
/// <item>The last line is a footer, <c>{"end":true,...}</c>, written when recording stops cleanly.</item>
/// </list>
/// Only the UDP payloads that matched the game filter are kept, never whole frames, so no other
/// traffic on the LAN ends up in a file. Lines are appended as packets arrive, so a kiosk that
/// crashes mid-recording leaves a file that still reads up to its last whole line.
/// </summary>
public static class LaserCaptureFormat
{
    public const string FormatName = "lanyard-laser-capture";

    /// <summary>
    /// Bump this only for a change an older reader can't safely skip. New optional fields don't
    /// need a bump: readers ignore fields they don't know.
    /// </summary>
    public const int CurrentVersion = 1;

    public const string FileExtension = ".lanyardcap";
}

/// <summary>How the kiosk was capturing when the recording was made. Kept for reference; replay doesn't use it.</summary>
public sealed record LaserCaptureSettings
{
    public string? SourceIp { get; init; }
    public string? DestinationIp { get; init; }
    public bool UseCaptureFilter { get; init; }
    public bool PromiscuousCapture { get; init; }

    /// <summary>The BPF filter Npcap actually applied, or null when everything was captured.</summary>
    public string? CaptureFilterApplied { get; init; }

    public string? ZoneScoreboardVersion { get; init; }
}

public sealed record LaserCaptureHeader
{
    public string Format { get; init; } = LaserCaptureFormat.FormatName;
    public int Version { get; init; } = LaserCaptureFormat.CurrentVersion;

    public DateTime RecordedAtUtc { get; init; }

    /// <summary>The kiosk client version that made the recording.</summary>
    public string? ClientVersion { get; init; }

    public LaserCaptureSettings? Settings { get; init; }

    /// <summary>Free text from whoever started the recording, e.g. "Saturday 3pm, 14 players".</summary>
    public string? Note { get; init; }

    /// <summary>How recording was asked to stop, e.g. "NextGame" or "UntilStopped". Informational.</summary>
    public string? Mode { get; init; }
}

/// <summary>One game packet.</summary>
/// <param name="OffsetMs">Milliseconds after the recording's first packet, from the capture timestamp.</param>
/// <param name="SourceIp">The control box address the packet came from.</param>
/// <param name="DestinationIp">The address it was sent to.</param>
/// <param name="Payload">The UDP payload exactly as the control box sent it.</param>
public sealed record LaserCapturePacket(long OffsetMs, string? SourceIp, string? DestinationIp, byte[] Payload);

/// <summary>Written when recording stops cleanly. A file without one was cut off, e.g. by a crash.</summary>
public sealed record LaserCaptureFooter
{
    public long Packets { get; init; }

    /// <summary>Packets that arrived while the writer was too far behind to keep them.</summary>
    public long Dropped { get; init; }

    public long DurationMs { get; init; }

    /// <summary>Complete games (a start followed by an end) seen while recording.</summary>
    public int Games { get; init; }
}

/// <summary>A whole recording, read back.</summary>
public sealed record LaserCapture(LaserCaptureHeader Header, IReadOnlyList<LaserCapturePacket> Packets, LaserCaptureFooter? Footer)
{
    /// <summary>True when the last line was only partly written, so it was skipped.</summary>
    public bool Truncated { get; init; }

    public long DurationMs => Packets.Count == 0 ? 0 : Packets[^1].OffsetMs;
}

/// <summary>The file isn't a readable recording. <see cref="LineNumber"/> is 1-based, or 0 for the file as a whole.</summary>
public sealed class LaserCaptureFormatException(string message, int lineNumber = 0) : Exception(message)
{
    public int LineNumber { get; } = lineNumber;
}
