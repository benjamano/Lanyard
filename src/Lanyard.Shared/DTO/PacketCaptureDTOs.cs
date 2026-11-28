using Lanyard.Shared.LaserGame.Capture;

namespace Lanyard.Shared.DTO;

/// <summary>When a recording stops by itself.</summary>
public enum LaserCaptureStopMode
{
    /// <summary>Stop a few seconds after the next game ends.</summary>
    NextGame = 0,

    /// <summary>Keep going until someone stops it (or a limit is reached).</summary>
    UntilStopped = 1,
}

/// <summary>What a kiosk is doing with recordings right now.</summary>
public enum PacketCaptureMode
{
    Idle = 0,
    Recording = 1,

    /// <summary>Fetching a recording from the server before replaying it.</summary>
    Downloading = 2,

    Replaying = 3,

    /// <summary>A replay has finished; its final scores stay up until the next live game or Stop.</summary>
    ReplayFinished = 4,
}

public sealed class StartPacketRecordingRequestDTO
{
    public LaserCaptureStopMode Mode { get; set; } = LaserCaptureStopMode.NextGame;

    /// <summary>Recording stops after this long whatever happens. Clamped to the rules' limits.</summary>
    public int MaxMinutes { get; set; } = PacketCaptureRules.DefaultMaxMinutes;

    /// <summary>Saved in the recording's header, e.g. "Saturday 3pm, 14 players".</summary>
    public string? Note { get; set; }

    /// <summary>The longest note kept; anything after is cut off.</summary>
    public const int MaxNoteLength = 200;
}

public sealed class StartPacketReplayRequestDTO
{
    /// <summary>The server's id for the recording, to name the kiosk's cached copy.</summary>
    public Guid RecordingId { get; set; }

    /// <summary>The File Manager file holding the recording.</summary>
    public Guid FileId { get; set; }

    /// <summary>One of <see cref="PacketCaptureRules.ReplaySpeeds"/>.</summary>
    public double Speed { get; set; } = 1;

    /// <summary>Shown in the kiosk's state report, e.g. the recording's note or date.</summary>
    public string? Name { get; set; }
}

public sealed class PacketCaptureCommandResultDTO
{
    public bool Accepted { get; set; }

    /// <summary>Why the kiosk refused, worded for staff. Null when accepted.</summary>
    public string? Error { get; set; }

    public PacketCaptureStateDTO? State { get; set; }

    public static PacketCaptureCommandResultDTO Ok(PacketCaptureStateDTO state) => new() { Accepted = true, State = state };

    public static PacketCaptureCommandResultDTO Refused(string error, PacketCaptureStateDTO state) => new() { Accepted = false, Error = error, State = state };
}

public sealed class PacketCaptureStateDTO
{
    public PacketCaptureMode Mode { get; set; } = PacketCaptureMode.Idle;

    /// <summary>When the current recording or replay started; null when idle.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>Packets recorded so far, or replayed so far.</summary>
    public long Packets { get; set; }

    /// <summary>Packets the recorder couldn't keep up with.</summary>
    public long Dropped { get; set; }

    /// <summary>Replay progress, 0 to 100.</summary>
    public int ProgressPercent { get; set; }

    public double Speed { get; set; }

    /// <summary>The note of the recording being made, or the name of the one being replayed.</summary>
    public string? Name { get; set; }

    /// <summary>Recordings waiting to upload to the server.</summary>
    public int PendingUploads { get; set; }

    /// <summary>What happened last, worded for staff, e.g. "Recording uploaded" or why a replay stopped.</summary>
    public string? LastMessage { get; set; }

    public bool LastMessageIsError { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
