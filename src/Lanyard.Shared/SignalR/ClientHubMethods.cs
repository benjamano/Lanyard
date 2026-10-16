namespace Lanyard.Shared.SignalR;

/// <summary>
/// Hub method names used by both the server and the kiosk client. A name that differs between
/// the two sides silently drops the call, so new names live here rather than as string literals
/// on each side. Never rename one: kiosks in the field still use the old value until they update.
/// </summary>
public static class ClientHubMethods
{
    /// <summary>Server to kiosk, client result: returns a <c>ClientLogTailDTO</c>.</summary>
    public const string RequestLogTail = "RequestLogTail";

    /// <summary>Server to kiosk: play an <c>AudioClipPlayCommand</c> over the music, ducking it.</summary>
    public const string PlayAudioClip = "PlayAudioClip";

    /// <summary>Server to kiosk: stop the clip with this play id (Guid.Empty stops whatever is playing).</summary>
    public const string StopAudioClip = "StopAudioClip";

    /// <summary>Kiosk to server: an <c>AudioClipPlaybackReport</c> each time a clip's playback moves on.</summary>
    public const string AudioClipPlaybackChanged = "AudioClipPlaybackChanged";
}
