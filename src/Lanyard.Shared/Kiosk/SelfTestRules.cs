using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;

namespace Lanyard.Shared.Kiosk;

/// <summary>
/// Turns what a kiosk observed about its hardware into self-test checks. Pure and clock-driven,
/// so the WPF client only gathers the facts and every pass/fail decision can be unit tested.
/// </summary>
public static class SelfTestRules
{
    public const int MaxDetailLength = 300;

    /// <summary>Most checks a reply may carry; the server refuses more.</summary>
    public const int MaxChecks = 30;

    /// <summary>The flash waits until the lights have been dark this long, so it can't cut into a scene.</summary>
    public static readonly TimeSpan DmxIdleBeforeFlash = TimeSpan.FromSeconds(10);

    /// <summary>A capture card that has seen nothing at all for this long is probably unplugged.</summary>
    public static readonly TimeSpan TrafficWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The oldest WebView2 runtime the kiosk is known to work with. The Evergreen runtime keeps
    /// itself updated, so anything older means Windows Update has stopped reaching the PC.
    /// </summary>
    public static readonly Version MinimumWebView2Version = new(120, 0);

    public sealed record AudioEndpoint(string Id, string Name);

    public sealed record AudioFacts(
        string? PreferredDeviceId,
        string? PreferredDeviceName,
        IReadOnlyList<AudioEndpoint> Devices,
        string? DefaultDeviceId,
        SelfTestToneResult Tone,
        string? ToneError);

    public sealed record FtdiDevice(int Index, string? Description, string? SerialNumber);

    public sealed record DmxFacts(
        bool Configured,
        bool DriverAvailable,
        uint? ConfiguredIndex,
        IReadOnlyList<FtdiDevice> Devices,
        bool PortOpen,
        long FramesBefore,
        long FramesAfter,
        string? LastWriteError,
        SelfTestFlashResult Flash,
        string? FlashError);

    public sealed record CaptureDevice(string Name, string? Description, string? MacAddress);

    public sealed record CaptureFacts(
        bool Configured,
        bool DriverAvailable,
        string? PreferredMacAddress,
        IReadOnlyList<CaptureDevice> Devices,
        bool IsCapturing,
        string? ActiveDeviceName,
        DateTime? CaptureStartedUtc,
        DateTime? LastPacketUtc,
        DateTime? LastGamePacketUtc,
        bool CaptureFiltered = false);

    public sealed record ProjectionWindow(int DisplayIndex, bool IsLoaded, bool IsOffline);

    public sealed record WebView2Facts(
        string? RuntimeVersion,
        string? RuntimeError,
        IReadOnlyList<ProjectionWindow> Windows);

    /// <summary>
    /// The tone would play over whatever is already coming out of the speakers, so it only plays
    /// into silence. Music that is paused or stopped doesn't count.
    /// </summary>
    public static SelfTestToneResult? ToneBlockedBy(bool musicPlaying, bool clipPlaying)
    {
        if (clipPlaying)
        {
            return SelfTestToneResult.SkippedClipPlaying;
        }

        if (musicPlaying)
        {
            return SelfTestToneResult.SkippedMusicPlaying;
        }

        return null;
    }

    /// <summary>
    /// Whether the DMX flash may run: only once every channel has been at zero for
    /// <see cref="DmxIdleBeforeFlash"/>, so a chase that dips to black for a moment still counts as live.
    /// </summary>
    public static bool CanFlashDmx(DateTime? lastNonZeroFrameUtc, DateTime nowUtc)
    {
        return lastNonZeroFrameUtc == null || nowUtc - lastNonZeroFrameUtc.Value >= DmxIdleBeforeFlash;
    }

    /// <summary>
    /// The preferred output: by endpoint ID first, then by name. Windows gives a device a new ID when
    /// it moves to another port, but two identical TVs share a name, so the name only counts when
    /// exactly one device has it.
    /// </summary>
    public static AudioEndpoint? MatchPreferredAudio(IReadOnlyList<AudioEndpoint> devices, string? preferredId, string? preferredName)
    {
        AudioEndpoint? byId = string.IsNullOrWhiteSpace(preferredId)
            ? null
            : devices.FirstOrDefault(d => string.Equals(d.Id, preferredId, StringComparison.OrdinalIgnoreCase));

        if (byId != null || string.IsNullOrWhiteSpace(preferredName))
        {
            return byId;
        }

        List<AudioEndpoint> byName = devices
            .Where(d => string.Equals(d.Name, preferredName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return byName.Count == 1 ? byName[0] : null;
    }

    public static List<KioskSelfTestCheckDTO> EvaluateAudio(AudioFacts facts)
    {
        List<KioskSelfTestCheckDTO> checks = [];

        bool preferenceSet = !string.IsNullOrWhiteSpace(facts.PreferredDeviceId);
        AudioEndpoint? preferred = preferenceSet ? MatchPreferredAudio(facts.Devices, facts.PreferredDeviceId, facts.PreferredDeviceName) : null;
        AudioEndpoint? currentDefault = facts.Devices.FirstOrDefault(d => string.Equals(d.Id, facts.DefaultDeviceId, StringComparison.OrdinalIgnoreCase));

        if (facts.Devices.Count == 0)
        {
            checks.Add(Check(SelfTestCheckKeys.AudioDevice, SelfTestOutcome.Fail, SelfTestCodes.NoOutputDevices, "Windows doesn't list any audio outputs."));
        }
        else if (!preferenceSet)
        {
            checks.Add(Check(SelfTestCheckKeys.AudioDevice, SelfTestOutcome.NotConfigured, null,
                $"No preferred output is set, so music uses the Windows default. Connected: {NameList(facts.Devices.Select(d => d.Name))}."));
        }
        else if (preferred == null)
        {
            checks.Add(Check(SelfTestCheckKeys.AudioDevice, SelfTestOutcome.Fail, SelfTestCodes.PreferredMissing,
                $"\"{facts.PreferredDeviceName ?? facts.PreferredDeviceId}\" isn't connected. Connected: {NameList(facts.Devices.Select(d => d.Name))}."));
        }
        else
        {
            checks.Add(Check(SelfTestCheckKeys.AudioDevice, SelfTestOutcome.Pass, null, $"\"{preferred.Name}\" is connected."));
        }

        if (currentDefault == null)
        {
            checks.Add(Check(SelfTestCheckKeys.AudioDefault, SelfTestOutcome.Fail, SelfTestCodes.NoDefault, "Windows has no default audio output."));
        }
        else if (preferred != null && !string.Equals(preferred.Id, currentDefault.Id, StringComparison.OrdinalIgnoreCase))
        {
            // The kiosk switches it back within a minute, so this is only worth a look if it keeps happening.
            checks.Add(Check(SelfTestCheckKeys.AudioDefault, SelfTestOutcome.Warn, SelfTestCodes.NotDefault,
                $"Windows is playing through \"{currentDefault.Name}\" instead of \"{preferred.Name}\"."));
        }
        else
        {
            checks.Add(Check(SelfTestCheckKeys.AudioDefault, SelfTestOutcome.Pass, null, $"Windows plays through \"{currentDefault.Name}\"."));
        }

        checks.Add(facts.Tone switch
        {
            SelfTestToneResult.Played => Check(SelfTestCheckKeys.AudioTone, SelfTestOutcome.Pass, null,
                $"Played a one-second beep through \"{currentDefault?.Name ?? "the default output"}\"."),
            SelfTestToneResult.SkippedMusicPlaying => Check(SelfTestCheckKeys.AudioTone, SelfTestOutcome.Skipped, SelfTestCodes.MusicPlaying,
                "Not played because music is playing."),
            SelfTestToneResult.SkippedClipPlaying => Check(SelfTestCheckKeys.AudioTone, SelfTestOutcome.Skipped, SelfTestCodes.ClipPlaying,
                "Not played because an audio clip is playing."),
            SelfTestToneResult.SkippedNoOutput => Check(SelfTestCheckKeys.AudioTone, SelfTestOutcome.Skipped, SelfTestCodes.NoOutputDevices,
                "Not played because there's no output to play it on."),
            _ => Check(SelfTestCheckKeys.AudioTone, SelfTestOutcome.Fail, SelfTestCodes.PlaybackFailed,
                $"The beep couldn't be played: {facts.ToneError ?? "unknown error"}."),
        });

        return checks;
    }

    public static List<KioskSelfTestCheckDTO> EvaluateDmx(DmxFacts facts)
    {
        // Most kiosks have no lights; one row says so rather than five grey ones.
        if (!facts.Configured)
        {
            return [Check(SelfTestCheckKeys.DmxDriver, SelfTestOutcome.NotConfigured, null, "DMX lighting isn't turned on for this kiosk.")];
        }

        if (!facts.DriverAvailable)
        {
            return [Check(SelfTestCheckKeys.DmxDriver, SelfTestOutcome.Fail, SelfTestCodes.DriverMissing, "The FTDI USB driver couldn't be loaded.")];
        }

        List<KioskSelfTestCheckDTO> checks = [Check(SelfTestCheckKeys.DmxDriver, SelfTestOutcome.Pass, null, "The FTDI USB driver is loaded.")];

        FtdiDevice? device = facts.Devices.FirstOrDefault(d => facts.ConfiguredIndex != null && d.Index == facts.ConfiguredIndex.Value);

        if (facts.Devices.Count == 0)
        {
            checks.Add(Check(SelfTestCheckKeys.DmxDevice, SelfTestOutcome.Fail, SelfTestCodes.NoDevices, "No USB DMX interface is plugged in."));
        }
        else if (device == null)
        {
            checks.Add(Check(SelfTestCheckKeys.DmxDevice, SelfTestOutcome.Fail, SelfTestCodes.IndexNotFound,
                $"Set to use USB device {facts.ConfiguredIndex?.ToString() ?? "(none)"}, but only {facts.Devices.Count} {(facts.Devices.Count == 1 ? "is" : "are")} plugged in."));
        }
        else
        {
            checks.Add(Check(SelfTestCheckKeys.DmxDevice, SelfTestOutcome.Pass, null, $"USB device {device.Index}: {DescribeFtdi(device)}."));
        }

        if (!facts.PortOpen)
        {
            checks.Add(Check(SelfTestCheckKeys.DmxPort, SelfTestOutcome.Fail, SelfTestCodes.PortNotOpen, "The kiosk couldn't open the DMX interface."));
        }
        else
        {
            checks.Add(Check(SelfTestCheckKeys.DmxPort, SelfTestOutcome.Pass, null, "The DMX interface is open."));

            if (!string.IsNullOrEmpty(facts.LastWriteError))
            {
                checks.Add(Check(SelfTestCheckKeys.DmxOutput, SelfTestOutcome.Fail, SelfTestCodes.WriteFailed, $"Sending to the lights failed: {facts.LastWriteError}."));
            }
            else if (facts.FramesAfter <= facts.FramesBefore)
            {
                checks.Add(Check(SelfTestCheckKeys.DmxOutput, SelfTestOutcome.Fail, SelfTestCodes.NotSending, "The kiosk has stopped sending to the lights."));
            }
            else
            {
                checks.Add(Check(SelfTestCheckKeys.DmxOutput, SelfTestOutcome.Pass, null, "Sending to the lights about 40 times a second."));
            }
        }

        KioskSelfTestCheckDTO? flash = facts.Flash switch
        {
            SelfTestFlashResult.Flashed => Check(SelfTestCheckKeys.DmxFlash, SelfTestOutcome.Pass, null,
                "Turned every channel up for one second, then put them back."),
            SelfTestFlashResult.SkippedOutputLive => Check(SelfTestCheckKeys.DmxFlash, SelfTestOutcome.Skipped, SelfTestCodes.OutputLive,
                "Not flashed because the lights are in use."),
            SelfTestFlashResult.SkippedPortNotOpen => Check(SelfTestCheckKeys.DmxFlash, SelfTestOutcome.Skipped, SelfTestCodes.PortNotOpen,
                "Not flashed because the DMX interface isn't open."),
            SelfTestFlashResult.Failed => Check(SelfTestCheckKeys.DmxFlash, SelfTestOutcome.Fail, SelfTestCodes.WriteFailed,
                $"The flash failed: {facts.FlashError ?? "unknown error"}."),
            _ => null
        };

        if (flash != null)
        {
            checks.Add(flash);
        }

        return checks;
    }

    public static List<KioskSelfTestCheckDTO> EvaluateCapture(CaptureFacts facts, DateTime nowUtc)
    {
        if (!facts.Configured)
        {
            return [Check(SelfTestCheckKeys.CaptureDriver, SelfTestOutcome.NotConfigured, null, "Laser tag scores aren't set up for this kiosk.")];
        }

        if (!facts.DriverAvailable)
        {
            return [Check(SelfTestCheckKeys.CaptureDriver, SelfTestOutcome.Fail, SelfTestCodes.NpcapMissing, "Npcap isn't installed, so the kiosk can't read game traffic.")];
        }

        List<KioskSelfTestCheckDTO> checks = [Check(SelfTestCheckKeys.CaptureDriver, SelfTestOutcome.Pass, null, "Npcap is installed.")];

        CaptureDevice? preferred = FindByMac(facts.Devices, facts.PreferredMacAddress);

        if (facts.Devices.Count == 0)
        {
            checks.Add(Check(SelfTestCheckKeys.CaptureNic, SelfTestOutcome.Fail, SelfTestCodes.NoDevices, "Npcap can't see any network cards."));
        }
        else if (preferred == null)
        {
            checks.Add(Check(SelfTestCheckKeys.CaptureNic, SelfTestOutcome.Fail, SelfTestCodes.NicNotFound,
                $"No network card has the chosen address {facts.PreferredMacAddress ?? "(none)"}. Found: {NameList(facts.Devices.Select(DescribeNic))}."));
        }
        else
        {
            checks.Add(Check(SelfTestCheckKeys.CaptureNic, SelfTestOutcome.Pass, null, $"Found {DescribeNic(preferred)}."));
        }

        if (!facts.IsCapturing)
        {
            checks.Add(Check(SelfTestCheckKeys.CaptureRunning, SelfTestOutcome.Fail, SelfTestCodes.NotRunning, "The kiosk isn't reading game traffic."));
            return checks;
        }

        if (preferred != null && !string.Equals(preferred.Name, facts.ActiveDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            CaptureDevice? active = facts.Devices.FirstOrDefault(d => d.Name == facts.ActiveDeviceName);

            checks.Add(Check(SelfTestCheckKeys.CaptureRunning, SelfTestOutcome.Warn, SelfTestCodes.FallbackDevice,
                $"Reading from {(active != null ? DescribeNic(active) : facts.ActiveDeviceName)} instead of the chosen card. Restart the kiosk to switch back."));
        }
        else
        {
            checks.Add(Check(SelfTestCheckKeys.CaptureRunning, SelfTestOutcome.Pass, null, "Reading game traffic."));
        }

        // Too soon after starting to judge, e.g. the kiosk has just reconnected.
        bool justStarted = facts.CaptureStartedUtc != null && nowUtc - facts.CaptureStartedUtc.Value < TrafficWindow;

        if (facts.CaptureFiltered && (facts.LastGamePacketUtc == null || nowUtc - facts.LastGamePacketUtc.Value >= TrafficWindow))
        {
            // The capture filter drops everything but game messages, so silence between games
            // says nothing about the cable or the network.
            checks.Add(justStarted
                ? Check(SelfTestCheckKeys.CaptureTraffic, SelfTestOutcome.Skipped, null, "Capture only just started. Run the test again in a minute.")
                : Check(SelfTestCheckKeys.CaptureTraffic, SelfTestOutcome.Warn, SelfTestCodes.NoGamePackets,
                    $"The capture filter is on, so only game messages are read, and none arrived in the last minute.{LastSeen(facts.LastGamePacketUtc, nowUtc)}"));
        }
        else if (facts.LastPacketUtc == null || nowUtc - facts.LastPacketUtc.Value >= TrafficWindow)
        {
            checks.Add(justStarted
                ? Check(SelfTestCheckKeys.CaptureTraffic, SelfTestOutcome.Skipped, null, "Capture only just started. Run the test again in a minute.")
                : Check(SelfTestCheckKeys.CaptureTraffic, SelfTestOutcome.Fail, SelfTestCodes.NoTraffic, "No network traffic at all in the last minute."));
        }
        else if (facts.LastGamePacketUtc == null || nowUtc - facts.LastGamePacketUtc.Value >= TrafficWindow)
        {
            // Normal between games, so only a warning.
            checks.Add(Check(SelfTestCheckKeys.CaptureTraffic, SelfTestOutcome.Warn, SelfTestCodes.NoGamePackets,
                $"Network traffic is arriving, but no game messages in the last minute.{LastSeen(facts.LastGamePacketUtc, nowUtc)}"));
        }
        else
        {
            checks.Add(Check(SelfTestCheckKeys.CaptureTraffic, SelfTestOutcome.Pass, null, $"Game messages are arriving.{LastSeen(facts.LastGamePacketUtc, nowUtc)}"));
        }

        return checks;
    }

    public static List<KioskSelfTestCheckDTO> EvaluateWebView2(WebView2Facts facts)
    {
        List<KioskSelfTestCheckDTO> checks = [];

        if (string.IsNullOrWhiteSpace(facts.RuntimeVersion))
        {
            checks.Add(Check(SelfTestCheckKeys.WebView2Runtime, SelfTestOutcome.Fail, SelfTestCodes.Missing,
                $"The Microsoft Edge WebView2 Runtime isn't installed{(string.IsNullOrWhiteSpace(facts.RuntimeError) ? "" : $" ({facts.RuntimeError})")}."));
        }
        else if (TryParseVersion(facts.RuntimeVersion) is Version version && version < MinimumWebView2Version)
        {
            checks.Add(Check(SelfTestCheckKeys.WebView2Runtime, SelfTestOutcome.Warn, SelfTestCodes.Outdated,
                $"Version {facts.RuntimeVersion} is older than {MinimumWebView2Version}."));
        }
        else
        {
            checks.Add(Check(SelfTestCheckKeys.WebView2Runtime, SelfTestOutcome.Pass, null, $"Version {facts.RuntimeVersion}."));
        }

        if (facts.Windows.Count == 0)
        {
            checks.Add(Check(SelfTestCheckKeys.WebView2Windows, SelfTestOutcome.NotConfigured, null, "No projection programs are showing on this kiosk."));
            return checks;
        }

        List<ProjectionWindow> broken = facts.Windows.Where(w => !w.IsLoaded).ToList();
        List<ProjectionWindow> offline = facts.Windows.Where(w => w.IsLoaded && w.IsOffline).ToList();

        if (broken.Count > 0)
        {
            checks.Add(Check(SelfTestCheckKeys.WebView2Windows, SelfTestOutcome.Fail, SelfTestCodes.WindowNotLoaded,
                $"{ScreenList(broken)} didn't finish loading."));
        }
        else if (offline.Count > 0)
        {
            checks.Add(Check(SelfTestCheckKeys.WebView2Windows, SelfTestOutcome.Warn, SelfTestCodes.ShowingOffline,
                $"{ScreenList(offline)} {(offline.Count == 1 ? "is" : "are")} showing the offline copy."));
        }
        else
        {
            checks.Add(Check(SelfTestCheckKeys.WebView2Windows, SelfTestOutcome.Pass, null,
                $"{facts.Windows.Count} {(facts.Windows.Count == 1 ? "screen is" : "screens are")} showing live."));
        }

        return checks;
    }

    /// <summary>A check for a whole group that threw or overran, so one bad device can't lose the others.</summary>
    public static KioskSelfTestCheckDTO GroupFailed(string key, string code, string? error)
    {
        return Check(key, SelfTestOutcome.Fail, code, code == SelfTestCodes.TimedOut
            ? "The check took too long and was abandoned."
            : $"The check crashed: {error ?? "unknown error"}.");
    }

    public static KioskSelfTestCheckDTO Check(string key, SelfTestOutcome outcome, string? code, string? detail)
    {
        return new KioskSelfTestCheckDTO
        {
            Key = key,
            Outcome = outcome,
            Code = code,
            Detail = Truncate(detail)
        };
    }

    public static string? Truncate(string? text, int maxLength = MaxDetailLength)
    {
        if (text == null || text.Length <= maxLength)
        {
            return text;
        }

        return text[..(maxLength - 1)] + "…";
    }

    private static CaptureDevice? FindByMac(IReadOnlyList<CaptureDevice> devices, string? mac)
    {
        string wanted = NormaliseMac(mac);

        return wanted.Length == 0 ? null : devices.FirstOrDefault(d => NormaliseMac(d.MacAddress) == wanted);
    }

    // PhysicalAddress.ToString() gives "001122AABBCC" while settings may hold "00-11-22-AA-BB-CC".
    private static string NormaliseMac(string? mac)
    {
        return new string((mac ?? string.Empty).Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
    }

    private static string DescribeNic(CaptureDevice device)
    {
        string name = string.IsNullOrWhiteSpace(device.Description) ? device.Name : device.Description;

        return string.IsNullOrWhiteSpace(device.MacAddress) ? name : $"{name} ({device.MacAddress})";
    }

    private static string DescribeFtdi(FtdiDevice device)
    {
        string description = string.IsNullOrWhiteSpace(device.Description) ? "FTDI device" : device.Description;

        return string.IsNullOrWhiteSpace(device.SerialNumber) ? description : $"{description} (serial {device.SerialNumber})";
    }

    private static string NameList(IEnumerable<string> names)
    {
        List<string> list = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

        return list.Count == 0 ? "none" : string.Join(", ", list.Select(n => $"\"{n}\""));
    }

    // DisplayIndex is zero-based; people count screens from 1.
    private static string ScreenList(IEnumerable<ProjectionWindow> windows)
    {
        List<int> numbers = windows.Select(w => w.DisplayIndex + 1).Order().ToList();

        return numbers.Count == 1 ? $"Screen {numbers[0]}" : $"Screens {string.Join(", ", numbers)}";
    }

    private static string LastSeen(DateTime? lastUtc, DateTime nowUtc)
    {
        if (lastUtc == null)
        {
            return string.Empty;
        }

        TimeSpan ago = nowUtc - lastUtc.Value;

        return ago < TimeSpan.FromMinutes(1)
            ? $" Last one {Math.Max(0, (int)ago.TotalSeconds)}s ago."
            : ago < TimeSpan.FromHours(1)
                ? $" Last one {(int)ago.TotalMinutes} min ago."
                : $" Last one {(int)ago.TotalHours} h ago.";
    }

    private static Version? TryParseVersion(string text)
    {
        // WebView2 versions can carry a channel suffix, e.g. "121.0.2277.83 canary".
        string numeric = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        return Version.TryParse(numeric, out Version? version) ? version : null;
    }
}
