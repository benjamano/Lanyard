namespace Lanyard.Shared.Kiosk;

/// <summary>
/// The checks a kiosk self-test can report. Stored with each result and matched by the server's
/// fix hints, so never rename one: add a new key instead.
/// </summary>
public static class SelfTestCheckKeys
{
    public const string AudioDevice = "audio.device";
    public const string AudioDefault = "audio.default";
    public const string AudioTone = "audio.tone";

    public const string DmxDriver = "dmx.driver";
    public const string DmxDevice = "dmx.device";
    public const string DmxPort = "dmx.port";
    public const string DmxOutput = "dmx.output";
    public const string DmxFlash = "dmx.flash";

    public const string CaptureDriver = "capture.driver";
    public const string CaptureNic = "capture.nic";
    public const string CaptureRunning = "capture.running";
    public const string CaptureTraffic = "capture.traffic";

    public const string WebView2Runtime = "webview2.runtime";
    public const string WebView2Windows = "webview2.windows";

    public const string GroupAudio = "audio";
    public const string GroupDmx = "dmx";
    public const string GroupCapture = "capture";
    public const string GroupWebView2 = "webview2";

    /// <summary>Every known key, in the order they're shown.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        AudioDevice, AudioDefault, AudioTone,
        DmxDriver, DmxDevice, DmxPort, DmxOutput, DmxFlash,
        CaptureDriver, CaptureNic, CaptureRunning, CaptureTraffic,
        WebView2Runtime, WebView2Windows
    ];

    /// <summary>The hardware group a key belongs to: the part before the first dot.</summary>
    public static string GroupOf(string key)
    {
        int dot = key.IndexOf('.');

        return dot > 0 ? key[..dot] : key;
    }

    public static bool IsKnown(string key) => All.Contains(key);
}

/// <summary>
/// Why a check didn't pass. The server maps (key, code) to a fix hint. Stored, so never rename.
/// </summary>
public static class SelfTestCodes
{
    // Any check
    public const string Exception = "Exception";
    public const string TimedOut = "TimedOut";
    public const string NotWindows = "NotWindows";

    // Audio
    public const string NoOutputDevices = "NoOutputDevices";
    public const string PreferredMissing = "PreferredMissing";
    public const string NoDefault = "NoDefault";
    public const string NotDefault = "NotDefault";
    public const string MusicPlaying = "MusicPlaying";
    public const string ClipPlaying = "ClipPlaying";
    public const string PlaybackFailed = "PlaybackFailed";

    // DMX
    public const string DriverMissing = "DriverMissing";
    public const string NoDevices = "NoDevices";
    public const string IndexNotFound = "IndexNotFound";
    public const string PortNotOpen = "PortNotOpen";
    public const string NotSending = "NotSending";
    public const string WriteFailed = "WriteFailed";
    public const string OutputLive = "OutputLive";

    // Packet capture
    public const string NpcapMissing = "NpcapMissing";
    public const string NicNotFound = "NicNotFound";
    public const string NotRunning = "NotRunning";
    public const string FallbackDevice = "FallbackDevice";
    public const string NoTraffic = "NoTraffic";
    public const string NoGamePackets = "NoGamePackets";

    // WebView2
    public const string Missing = "Missing";
    public const string Outdated = "Outdated";
    public const string WindowNotLoaded = "WindowNotLoaded";
    public const string ShowingOffline = "ShowingOffline";
}
