using Lanyard.Shared.Enum;
using Lanyard.Shared.Kiosk;

namespace Lanyard.Application.Services;

/// <summary>
/// What each self-test check is called and what to do when it doesn't pass. Lives on the server
/// rather than the kiosk so the wording can improve without a kiosk release. The kiosk sends a
/// stable (key, code); anything this doesn't know still gets a sensible title and hint.
/// </summary>
public static class KioskSelfTestCatalogue
{
    private static readonly Dictionary<string, string> Titles = new()
    {
        [SelfTestCheckKeys.AudioDevice] = "Preferred speakers connected",
        [SelfTestCheckKeys.AudioDefault] = "Windows sound output",
        [SelfTestCheckKeys.AudioTone] = "Test beep",
        [SelfTestCheckKeys.DmxDriver] = "DMX USB driver",
        [SelfTestCheckKeys.DmxDevice] = "DMX interface plugged in",
        [SelfTestCheckKeys.DmxPort] = "DMX interface open",
        [SelfTestCheckKeys.DmxOutput] = "Sending to the lights",
        [SelfTestCheckKeys.DmxFlash] = "Light flash",
        [SelfTestCheckKeys.CaptureDriver] = "Npcap installed",
        [SelfTestCheckKeys.CaptureNic] = "Game network card",
        [SelfTestCheckKeys.CaptureRunning] = "Reading game traffic",
        [SelfTestCheckKeys.CaptureTraffic] = "Game messages arriving",
        [SelfTestCheckKeys.WebView2Runtime] = "WebView2 runtime",
        [SelfTestCheckKeys.WebView2Windows] = "Projection screens",
    };

    private static readonly Dictionary<string, string> GroupTitles = new()
    {
        [SelfTestCheckKeys.GroupAudio] = "Audio",
        [SelfTestCheckKeys.GroupDmx] = "DMX lighting",
        [SelfTestCheckKeys.GroupCapture] = "Laser tag network",
        [SelfTestCheckKeys.GroupWebView2] = "Projection display",
    };

    private const string RestartHint = "Restart the kiosk from the Clients page (Restart, then Restart Lanyard Client). If it happens again, send the kiosk's log from Diagnostics.";

    // Hints by key and code. A code that can turn up for any key (a crash, a time-out) is matched
    // without a key; see Hint().
    private static readonly Dictionary<(string Key, string Code), string> Hints = new()
    {
        [(SelfTestCheckKeys.AudioDevice, SelfTestCodes.NoOutputDevices)] = "Windows can't see any speakers. Check the speaker or amplifier cable, and that the TV or projector carrying the sound is switched on.",
        [(SelfTestCheckKeys.AudioDevice, SelfTestCodes.PreferredMissing)] = "Switch on and reconnect the chosen speakers. If they've been replaced, pick the new ones in the kiosk's Settings > Music.",
        [(SelfTestCheckKeys.AudioDefault, SelfTestCodes.NoDefault)] = "Windows has no sound output. Plug in speakers, or switch on the TV or projector that carries the sound.",
        [(SelfTestCheckKeys.AudioDefault, SelfTestCodes.NotDefault)] = "Something changed the Windows sound output, often a TV waking up. The kiosk switches it back within a minute; if this keeps happening, check the chosen speakers in Settings > Music.",
        [(SelfTestCheckKeys.AudioTone, SelfTestCodes.MusicPlaying)] = "Pause the music and run the test again to hear the beep.",
        [(SelfTestCheckKeys.AudioTone, SelfTestCodes.ClipPlaying)] = "Wait for the audio clip to finish and run the test again.",
        [(SelfTestCheckKeys.AudioTone, SelfTestCodes.NoOutputDevices)] = "Fix the sound output above first.",
        [(SelfTestCheckKeys.AudioTone, SelfTestCodes.PlaybackFailed)] = "Windows couldn't play sound. Check the output isn't in use by another program, then restart the kiosk.",

        [(SelfTestCheckKeys.DmxDriver, SelfTestCodes.DriverMissing)] = "Install the FTDI D2XX driver from ftdichip.com, then restart the PC.",
        [(SelfTestCheckKeys.DmxDevice, SelfTestCodes.NoDevices)] = "Plug the USB DMX interface in. Try another USB port, and avoid unpowered USB hubs.",
        [(SelfTestCheckKeys.DmxDevice, SelfTestCodes.IndexNotFound)] = "Fewer DMX interfaces are plugged in than the kiosk is set to use. Plug the other one in, or pick the right interface in Settings > DMX.",
        [(SelfTestCheckKeys.DmxPort, SelfTestCodes.PortNotOpen)] = "Unplug the USB DMX interface, plug it back in, then restart the kiosk. Close any other lighting software on the PC.",
        [(SelfTestCheckKeys.DmxOutput, SelfTestCodes.NotSending)] = RestartHint,
        [(SelfTestCheckKeys.DmxOutput, SelfTestCodes.WriteFailed)] = "The USB DMX interface stopped accepting data. Unplug it, plug it back in, then restart the kiosk.",
        [(SelfTestCheckKeys.DmxFlash, SelfTestCodes.OutputLive)] = "Stop the lighting scene, wait ten seconds and run the test again with Flash lights on.",
        [(SelfTestCheckKeys.DmxFlash, SelfTestCodes.PortNotOpen)] = "Fix the DMX interface above first.",
        [(SelfTestCheckKeys.DmxFlash, SelfTestCodes.WriteFailed)] = "The USB DMX interface stopped accepting data. Unplug it, plug it back in, then restart the kiosk.",

        [(SelfTestCheckKeys.CaptureDriver, SelfTestCodes.NpcapMissing)] = "Install Npcap from npcap.com with \"WinPcap API-compatible mode\" ticked, then restart the kiosk.",
        [(SelfTestCheckKeys.CaptureNic, SelfTestCodes.NoDevices)] = "Npcap can't see any network cards. Reinstall Npcap, then restart the PC.",
        [(SelfTestCheckKeys.CaptureNic, SelfTestCodes.NicNotFound)] = "The chosen network card has been removed or replaced. Pick the card connected to the laser tag network in Settings > Zone Scoreboard.",
        [(SelfTestCheckKeys.CaptureRunning, SelfTestCodes.NotRunning)] = RestartHint,
        [(SelfTestCheckKeys.CaptureRunning, SelfTestCodes.FallbackDevice)] = "The chosen card wasn't there when the kiosk started, so it's using another one. Check the card is enabled, then restart the kiosk.",
        [(SelfTestCheckKeys.CaptureTraffic, SelfTestCodes.NoTraffic)] = "The network card isn't receiving anything. Check its cable is plugged into the laser tag network switch and the link lights are on.",
        [(SelfTestCheckKeys.CaptureTraffic, SelfTestCodes.NoGamePackets)] = "Normal when no game has run in the last minute. If a game is running, check the cable goes to the laser tag system's network, not the venue's.",

        [(SelfTestCheckKeys.WebView2Runtime, SelfTestCodes.Missing)] = "Install the Evergreen WebView2 Runtime from Microsoft, then restart the kiosk.",
        [(SelfTestCheckKeys.WebView2Runtime, SelfTestCodes.Outdated)] = "Run Windows Update, or reinstall the Evergreen WebView2 Runtime from Microsoft.",
        [(SelfTestCheckKeys.WebView2Windows, SelfTestCodes.WindowNotLoaded)] = "Use Screens on the Clients page to see what's showing, then restart the kiosk.",
        [(SelfTestCheckKeys.WebView2Windows, SelfTestCodes.ShowingOffline)] = "The kiosk lost the server and is showing its offline copy. Check the PC's internet connection; it switches back on its own.",
    };

    public static string GroupTitle(string group)
    {
        return GroupTitles.TryGetValue(group, out string? title) ? title : "Other checks";
    }

    /// <summary>The order groups are shown in; unknown groups come last.</summary>
    public static int GroupOrder(string group)
    {
        int index = Array.IndexOf(
            [SelfTestCheckKeys.GroupAudio, SelfTestCheckKeys.GroupDmx, SelfTestCheckKeys.GroupCapture, SelfTestCheckKeys.GroupWebView2],
            group);

        return index < 0 ? int.MaxValue : index;
    }

    public static string Title(string key)
    {
        // A newer kiosk can report a check this server doesn't know yet.
        return Titles.TryGetValue(key, out string? title) ? title : $"Unrecognised check ({key})";
    }

    /// <summary>What to do about a check, or null when it passed or there is nothing to do.</summary>
    public static string? Hint(string key, SelfTestOutcome outcome, string? code)
    {
        if (outcome is SelfTestOutcome.Pass or SelfTestOutcome.NotConfigured)
        {
            return null;
        }

        if (code == SelfTestCodes.TimedOut)
        {
            return "The kiosk gave up on this check because it took too long, which usually means the device's driver is stuck. " + RestartHint;
        }

        if (code == SelfTestCodes.Exception)
        {
            return RestartHint;
        }

        if (code != null && Hints.TryGetValue((key, code), out string? hint))
        {
            return hint;
        }

        // A skip with no specific advice (e.g. traffic right after start-up) needs nothing doing.
        return outcome == SelfTestOutcome.Skipped ? null : RestartHint;
    }

    /// <summary>Every (key, code) pair with a specific hint, for the tests.</summary>
    public static IEnumerable<(string Key, string Code)> KnownHints => Hints.Keys;
}
