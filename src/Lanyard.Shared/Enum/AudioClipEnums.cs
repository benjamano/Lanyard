namespace Lanyard.Shared.Enum;

/// <summary>What a kiosk reports about a clip it was asked to play.</summary>
public enum AudioClipPlaybackStatus
{
    // Fetching the clip before it can start.
    Preparing = 1,
    Playing = 2,
    Finished = 3,
    // Stopped on request, or cut off by another clip.
    Stopped = 4,
    Failed = 5
}
