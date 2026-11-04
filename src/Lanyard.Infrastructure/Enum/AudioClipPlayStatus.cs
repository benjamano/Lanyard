namespace Lanyard.Infrastructure.Enum;

// Stored, so never renumber.
/// <summary>How one play of an audio clip on one kiosk went.</summary>
public enum AudioClipPlayStatus
{
    // Sent to the kiosk; nothing heard back yet.
    Sent = 1,
    Preparing = 2,
    Playing = 3,
    Finished = 4,
    // Stopped by someone, or cut off by another clip.
    Stopped = 5,
    Failed = 6,
    // Never sent: the kiosk wasn't connected.
    Offline = 7,
    // Never sent: the kiosk's software is too old to play clips.
    OutdatedKiosk = 8
}
