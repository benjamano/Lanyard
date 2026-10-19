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

    /// <summary>
    /// Server to kiosk: the <c>AudioClipPlayCommand</c>s (PlayId empty) of the clips scheduled at the
    /// kiosk's location, to download ahead so they start on time.
    /// </summary>
    public const string PrefetchAudioClips = "PrefetchAudioClips";

    /// <summary>Kiosk to server: an <c>AudioClipPlaybackReport</c> each time a clip's playback moves on.</summary>
    public const string AudioClipPlaybackChanged = "AudioClipPlaybackChanged";
    /// <summary>Server to kiosk, client result: returns a list of <c>ClientProjectionWindowDTO</c>.</summary>
    public const string ListProjectionWindows = "ListProjectionWindows";

    /// <summary>
    /// Server to kiosk, client result: takes a <c>ClientScreenshotRequestDTO</c> and returns a
    /// <c>ClientScreenshotDTO</c>. One call per screen, so each reply stays under the hub's size limit.
    /// </summary>
    public const string CaptureProjectionWindow = "CaptureProjectionWindow";

    /// <summary>
    /// Server to kiosk, client result: takes a <c>KioskSelfTestRequestDTO</c>, checks the kiosk's
    /// audio, DMX, packet capture and WebView2, and returns a <c>KioskSelfTestResultDTO</c>.
    /// </summary>
    public const string RunSelfTest = "RunSelfTest";
}
