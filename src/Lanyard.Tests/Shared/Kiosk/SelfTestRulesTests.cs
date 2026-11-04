using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Lanyard.Shared.Kiosk;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Lanyard.Shared.Kiosk.SelfTestRules;

namespace Lanyard.Tests.Shared.Kiosk;

[TestClass]
public class SelfTestRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 14, 0, 0, DateTimeKind.Utc);

    private static readonly AudioEndpoint Speakers = new("{speakers}", "Speakers (Realtek)");
    private static readonly AudioEndpoint Tv = new("{tv}", "LG TV (HDMI)");

    private static KioskSelfTestCheckDTO Get(List<KioskSelfTestCheckDTO> checks, string key)
    {
        KioskSelfTestCheckDTO? check = checks.SingleOrDefault(x => x.Key == key);
        Assert.IsNotNull(check, $"No {key} check");
        return check;
    }

    // ---- Tone and flash gating ----

    [TestMethod]
    public void ToneBlockedBy_OnlyPlaysIntoSilence()
    {
        Assert.IsNull(ToneBlockedBy(musicPlaying: false, clipPlaying: false));
        Assert.AreEqual(SelfTestToneResult.SkippedMusicPlaying, ToneBlockedBy(musicPlaying: true, clipPlaying: false));
        Assert.AreEqual(SelfTestToneResult.SkippedClipPlaying, ToneBlockedBy(musicPlaying: false, clipPlaying: true));
        Assert.AreEqual(SelfTestToneResult.SkippedClipPlaying, ToneBlockedBy(musicPlaying: true, clipPlaying: true));
    }

    [TestMethod]
    public void CanFlashDmx_OnlyOnceTheLightsHaveBeenDarkForTheIdleWindow()
    {
        Assert.IsTrue(CanFlashDmx(null, Now), "Never lit since the port opened");
        Assert.IsFalse(CanFlashDmx(Now, Now), "Lit right now");
        Assert.IsFalse(CanFlashDmx(Now - DmxIdleBeforeFlash + TimeSpan.FromMilliseconds(1), Now), "A chase that dipped to black a moment ago");
        Assert.IsTrue(CanFlashDmx(Now - DmxIdleBeforeFlash, Now));
    }

    // ---- Audio ----

    [TestMethod]
    public void MatchPreferredAudio_PrefersIdThenAnUnambiguousName()
    {
        AudioEndpoint moved = new("{new-id}", "LG TV (HDMI)");

        Assert.AreEqual(Tv, MatchPreferredAudio([Speakers, Tv], "{TV}", "anything"), "ID match ignores case");
        Assert.AreEqual(moved, MatchPreferredAudio([Speakers, moved], "{tv}", "LG TV (HDMI)"), "Falls back to the name when the ID moved");
        Assert.IsNull(MatchPreferredAudio([moved, new AudioEndpoint("{twin}", "LG TV (HDMI)")], "{tv}", "LG TV (HDMI)"), "Two identical TVs: no guess");
        Assert.IsNull(MatchPreferredAudio([Speakers], null, null));
    }

    [TestMethod]
    public void EvaluateAudio_AllGood()
    {
        List<KioskSelfTestCheckDTO> checks = EvaluateAudio(new AudioFacts(Tv.Id, Tv.Name, [Speakers, Tv], Tv.Id, SelfTestToneResult.Played, null));

        CollectionAssert.AreEqual(new[] { SelfTestCheckKeys.AudioDevice, SelfTestCheckKeys.AudioDefault, SelfTestCheckKeys.AudioTone }, checks.Select(x => x.Key).ToArray());
        Assert.IsTrue(checks.All(x => x.Outcome == SelfTestOutcome.Pass));
        StringAssert.Contains(Get(checks, SelfTestCheckKeys.AudioTone).Detail, "LG TV");
    }

    [TestMethod]
    public void EvaluateAudio_PreferredMissing_FailsAndListsWhatIsConnected()
    {
        List<KioskSelfTestCheckDTO> checks = EvaluateAudio(new AudioFacts(Tv.Id, Tv.Name, [Speakers], Speakers.Id, SelfTestToneResult.Played, null));

        KioskSelfTestCheckDTO device = Get(checks, SelfTestCheckKeys.AudioDevice);
        Assert.AreEqual(SelfTestOutcome.Fail, device.Outcome);
        Assert.AreEqual(SelfTestCodes.PreferredMissing, device.Code);
        StringAssert.Contains(device.Detail, "Speakers (Realtek)");

        // The default can't be "wrong" when the preferred device isn't there to compare with.
        Assert.AreEqual(SelfTestOutcome.Pass, Get(checks, SelfTestCheckKeys.AudioDefault).Outcome);
    }

    [TestMethod]
    public void EvaluateAudio_DefaultDriftedAway_Warns()
    {
        List<KioskSelfTestCheckDTO> checks = EvaluateAudio(new AudioFacts(Speakers.Id, Speakers.Name, [Speakers, Tv], Tv.Id, SelfTestToneResult.Played, null));

        KioskSelfTestCheckDTO check = Get(checks, SelfTestCheckKeys.AudioDefault);
        Assert.AreEqual(SelfTestOutcome.Warn, check.Outcome);
        Assert.AreEqual(SelfTestCodes.NotDefault, check.Code);
    }

    [TestMethod]
    public void EvaluateAudio_NoPreference_IsNotConfigured_NotAFailure()
    {
        List<KioskSelfTestCheckDTO> checks = EvaluateAudio(new AudioFacts(null, null, [Speakers], Speakers.Id, SelfTestToneResult.Played, null));

        Assert.AreEqual(SelfTestOutcome.NotConfigured, Get(checks, SelfTestCheckKeys.AudioDevice).Outcome);
        Assert.AreEqual(SelfTestOutcome.Pass, Get(checks, SelfTestCheckKeys.AudioDefault).Outcome);
    }

    [TestMethod]
    public void EvaluateAudio_NoOutputsAtAll()
    {
        List<KioskSelfTestCheckDTO> checks = EvaluateAudio(new AudioFacts(Tv.Id, Tv.Name, [], null, SelfTestToneResult.SkippedNoOutput, null));

        Assert.AreEqual(SelfTestCodes.NoOutputDevices, Get(checks, SelfTestCheckKeys.AudioDevice).Code);
        Assert.AreEqual(SelfTestCodes.NoDefault, Get(checks, SelfTestCheckKeys.AudioDefault).Code);
        Assert.AreEqual(SelfTestOutcome.Skipped, Get(checks, SelfTestCheckKeys.AudioTone).Outcome);
    }

    [TestMethod]
    [DataRow(SelfTestToneResult.SkippedMusicPlaying, SelfTestOutcome.Skipped, SelfTestCodes.MusicPlaying)]
    [DataRow(SelfTestToneResult.SkippedClipPlaying, SelfTestOutcome.Skipped, SelfTestCodes.ClipPlaying)]
    [DataRow(SelfTestToneResult.Failed, SelfTestOutcome.Fail, SelfTestCodes.PlaybackFailed)]
    public void EvaluateAudio_ToneOutcomes(SelfTestToneResult tone, SelfTestOutcome expected, string code)
    {
        KioskSelfTestCheckDTO check = Get(EvaluateAudio(new AudioFacts(null, null, [Speakers], Speakers.Id, tone, "device busy")), SelfTestCheckKeys.AudioTone);

        Assert.AreEqual(expected, check.Outcome);
        Assert.AreEqual(code, check.Code);
    }

    // ---- DMX ----

    private static DmxFacts Dmx(
        bool driver = true,
        uint? index = 0,
        int deviceCount = 1,
        bool open = true,
        long framesAfter = 8,
        string? writeError = null,
        SelfTestFlashResult flash = SelfTestFlashResult.NotRequested)
    {
        List<FtdiDevice> devices = Enumerable.Range(0, deviceCount).Select(i => new FtdiDevice(i, "FT232R USB UART", $"A10{i}")).ToList();

        return new DmxFacts(true, driver, index, devices, open, 0, framesAfter, writeError, flash, null);
    }

    [TestMethod]
    public void EvaluateDmx_NotConfigured_IsOneGreyRow()
    {
        List<KioskSelfTestCheckDTO> checks = EvaluateDmx(new DmxFacts(false, false, null, [], false, 0, 0, null, SelfTestFlashResult.NotRequested, null));

        Assert.HasCount(1, checks);
        Assert.AreEqual(SelfTestOutcome.NotConfigured, checks[0].Outcome);
    }

    [TestMethod]
    public void EvaluateDmx_AllGood()
    {
        List<KioskSelfTestCheckDTO> checks = EvaluateDmx(Dmx());

        CollectionAssert.AreEqual(new[] { SelfTestCheckKeys.DmxDriver, SelfTestCheckKeys.DmxDevice, SelfTestCheckKeys.DmxPort, SelfTestCheckKeys.DmxOutput }, checks.Select(x => x.Key).ToArray());
        Assert.IsTrue(checks.All(x => x.Outcome == SelfTestOutcome.Pass));
        StringAssert.Contains(Get(checks, SelfTestCheckKeys.DmxDevice).Detail, "A100");
    }

    [TestMethod]
    public void EvaluateDmx_DriverMissing_StopsThere()
    {
        List<KioskSelfTestCheckDTO> checks = EvaluateDmx(Dmx(driver: false));

        Assert.HasCount(1, checks);
        Assert.AreEqual(SelfTestCodes.DriverMissing, checks[0].Code);
    }

    [TestMethod]
    public void EvaluateDmx_DeviceProblems()
    {
        Assert.AreEqual(SelfTestCodes.NoDevices, Get(EvaluateDmx(Dmx(deviceCount: 0, open: false)), SelfTestCheckKeys.DmxDevice).Code);
        Assert.AreEqual(SelfTestCodes.IndexNotFound, Get(EvaluateDmx(Dmx(index: 2, deviceCount: 1)), SelfTestCheckKeys.DmxDevice).Code);
    }

    [TestMethod]
    public void EvaluateDmx_PortClosed_SkipsTheOutputCheck()
    {
        List<KioskSelfTestCheckDTO> checks = EvaluateDmx(Dmx(open: false));

        Assert.AreEqual(SelfTestCodes.PortNotOpen, Get(checks, SelfTestCheckKeys.DmxPort).Code);
        Assert.IsFalse(checks.Any(x => x.Key == SelfTestCheckKeys.DmxOutput));
    }

    [TestMethod]
    public void EvaluateDmx_OutputStalledOrFailing()
    {
        Assert.AreEqual(SelfTestCodes.NotSending, Get(EvaluateDmx(Dmx(framesAfter: 0)), SelfTestCheckKeys.DmxOutput).Code);

        KioskSelfTestCheckDTO failing = Get(EvaluateDmx(Dmx(writeError: "status FT_IO_ERROR")), SelfTestCheckKeys.DmxOutput);
        Assert.AreEqual(SelfTestCodes.WriteFailed, failing.Code);
        StringAssert.Contains(failing.Detail, "FT_IO_ERROR");
    }

    [TestMethod]
    [DataRow(SelfTestFlashResult.Flashed, SelfTestOutcome.Pass, null)]
    [DataRow(SelfTestFlashResult.SkippedOutputLive, SelfTestOutcome.Skipped, SelfTestCodes.OutputLive)]
    [DataRow(SelfTestFlashResult.SkippedPortNotOpen, SelfTestOutcome.Skipped, SelfTestCodes.PortNotOpen)]
    [DataRow(SelfTestFlashResult.Failed, SelfTestOutcome.Fail, SelfTestCodes.WriteFailed)]
    public void EvaluateDmx_FlashOutcomes(SelfTestFlashResult flash, SelfTestOutcome expected, string? code)
    {
        KioskSelfTestCheckDTO check = Get(EvaluateDmx(Dmx(flash: flash)), SelfTestCheckKeys.DmxFlash);

        Assert.AreEqual(expected, check.Outcome);
        Assert.AreEqual(code, check.Code);
    }

    [TestMethod]
    public void EvaluateDmx_FlashNotRequested_HasNoRow()
    {
        Assert.IsFalse(EvaluateDmx(Dmx()).Any(x => x.Key == SelfTestCheckKeys.DmxFlash));
    }

    // ---- Packet capture ----

    private static readonly CaptureDevice GameNic = new(@"\Device\NPF_{GAME}", "Intel I210", "001122AABBCC");
    private static readonly CaptureDevice OfficeNic = new(@"\Device\NPF_{OFFICE}", "Realtek PCIe", "665544332211");

    private static CaptureFacts Capture(
        bool driver = true,
        string? mac = "00-11-22-AA-BB-CC",
        bool capturing = true,
        string? active = @"\Device\NPF_{GAME}",
        DateTime? started = null,
        DateTime? lastPacket = null,
        DateTime? lastGame = null,
        IReadOnlyList<CaptureDevice>? devices = null)
    {
        return new CaptureFacts(true, driver, mac, devices ?? [GameNic, OfficeNic], capturing, active,
            started ?? Now.AddHours(-1), lastPacket ?? Now.AddSeconds(-1), lastGame ?? Now.AddSeconds(-5));
    }

    [TestMethod]
    public void EvaluateCapture_AllGood_MatchesMacWhateverItsFormat()
    {
        List<KioskSelfTestCheckDTO> checks = EvaluateCapture(Capture(), Now);

        CollectionAssert.AreEqual(new[] { SelfTestCheckKeys.CaptureDriver, SelfTestCheckKeys.CaptureNic, SelfTestCheckKeys.CaptureRunning, SelfTestCheckKeys.CaptureTraffic }, checks.Select(x => x.Key).ToArray());
        Assert.IsTrue(checks.All(x => x.Outcome == SelfTestOutcome.Pass), string.Join(" | ", checks.Select(x => $"{x.Key}:{x.Outcome}")));
    }

    [TestMethod]
    public void EvaluateCapture_NotConfiguredOrNoNpcap()
    {
        List<KioskSelfTestCheckDTO> notConfigured = EvaluateCapture(new CaptureFacts(false, false, null, [], false, null, null, null, null), Now);
        Assert.AreEqual(SelfTestOutcome.NotConfigured, notConfigured.Single().Outcome);

        List<KioskSelfTestCheckDTO> noNpcap = EvaluateCapture(Capture(driver: false), Now);
        Assert.AreEqual(SelfTestCodes.NpcapMissing, noNpcap.Single().Code);
    }

    [TestMethod]
    public void EvaluateCapture_ChosenCardMissing_ListsTheOthers()
    {
        KioskSelfTestCheckDTO nic = Get(EvaluateCapture(Capture(mac: "DE:AD:BE:EF:00:01"), Now), SelfTestCheckKeys.CaptureNic);

        Assert.AreEqual(SelfTestCodes.NicNotFound, nic.Code);
        StringAssert.Contains(nic.Detail, "Intel I210");
    }

    [TestMethod]
    public void EvaluateCapture_ReadingAFallbackCard_Warns()
    {
        KioskSelfTestCheckDTO running = Get(EvaluateCapture(Capture(active: @"\Device\NPF_{OFFICE}"), Now), SelfTestCheckKeys.CaptureRunning);

        Assert.AreEqual(SelfTestOutcome.Warn, running.Outcome);
        Assert.AreEqual(SelfTestCodes.FallbackDevice, running.Code);
        StringAssert.Contains(running.Detail, "Realtek");
    }

    [TestMethod]
    public void EvaluateCapture_NotRunning_StopsBeforeTraffic()
    {
        List<KioskSelfTestCheckDTO> checks = EvaluateCapture(Capture(capturing: false, active: null), Now);

        Assert.AreEqual(SelfTestCodes.NotRunning, Get(checks, SelfTestCheckKeys.CaptureRunning).Code);
        Assert.IsFalse(checks.Any(x => x.Key == SelfTestCheckKeys.CaptureTraffic));
    }

    [TestMethod]
    public void EvaluateCapture_Traffic()
    {
        KioskSelfTestCheckDTO silent = Get(EvaluateCapture(Capture(lastPacket: Now.AddMinutes(-5), lastGame: Now.AddMinutes(-5)), Now), SelfTestCheckKeys.CaptureTraffic);
        Assert.AreEqual(SelfTestOutcome.Fail, silent.Outcome);
        Assert.AreEqual(SelfTestCodes.NoTraffic, silent.Code);

        // Between games: the card sees the network but no game messages. Normal, so only a warning.
        KioskSelfTestCheckDTO betweenGames = Get(EvaluateCapture(Capture(lastGame: Now.AddMinutes(-20)), Now), SelfTestCheckKeys.CaptureTraffic);
        Assert.AreEqual(SelfTestOutcome.Warn, betweenGames.Outcome);
        Assert.AreEqual(SelfTestCodes.NoGamePackets, betweenGames.Code);
        StringAssert.Contains(betweenGames.Detail, "20 min ago");
    }

    [TestMethod]
    public void EvaluateCapture_WithCaptureFilter_SilenceIsOnlyAWarning()
    {
        // The filter drops everything but game messages, so no packets at all between games is normal.
        CaptureFacts facts = Capture(lastPacket: Now.AddMinutes(-20), lastGame: Now.AddMinutes(-20)) with { CaptureFiltered = true };

        KioskSelfTestCheckDTO check = Get(EvaluateCapture(facts, Now), SelfTestCheckKeys.CaptureTraffic);

        Assert.AreEqual(SelfTestOutcome.Warn, check.Outcome);
        Assert.AreEqual(SelfTestCodes.NoGamePackets, check.Code);
        StringAssert.Contains(check.Detail, "capture filter");
    }

    [TestMethod]
    public void EvaluateCapture_WithCaptureFilter_GameMessagesPass()
    {
        CaptureFacts facts = Capture() with { CaptureFiltered = true };

        Assert.AreEqual(SelfTestOutcome.Pass, Get(EvaluateCapture(facts, Now), SelfTestCheckKeys.CaptureTraffic).Outcome);
    }

    [TestMethod]
    public void EvaluateCapture_JustStarted_DoesNotJudgeTraffic()
    {
        DateTime? never = null;
        CaptureFacts facts = Capture(started: Now.AddSeconds(-10)) with { LastPacketUtc = never, LastGamePacketUtc = never };

        Assert.AreEqual(SelfTestOutcome.Skipped, Get(EvaluateCapture(facts, Now), SelfTestCheckKeys.CaptureTraffic).Outcome);
    }

    // ---- WebView2 ----

    [TestMethod]
    public void EvaluateWebView2_Runtime()
    {
        Assert.AreEqual(SelfTestOutcome.Pass, Get(EvaluateWebView2(new WebView2Facts("141.0.3537.71", null, [])), SelfTestCheckKeys.WebView2Runtime).Outcome);
        Assert.AreEqual(SelfTestOutcome.Pass, Get(EvaluateWebView2(new WebView2Facts("141.0.3537.71 canary", null, [])), SelfTestCheckKeys.WebView2Runtime).Outcome);
        Assert.AreEqual(SelfTestCodes.Outdated, Get(EvaluateWebView2(new WebView2Facts("109.0.1518.140", null, [])), SelfTestCheckKeys.WebView2Runtime).Code);
        Assert.AreEqual(SelfTestCodes.Missing, Get(EvaluateWebView2(new WebView2Facts(null, null, [])), SelfTestCheckKeys.WebView2Runtime).Code);
    }

    [TestMethod]
    public void EvaluateWebView2_Windows_NumberScreensFromOne()
    {
        string version = "141.0.0.0";

        Assert.AreEqual(SelfTestOutcome.NotConfigured, Get(EvaluateWebView2(new WebView2Facts(version, null, [])), SelfTestCheckKeys.WebView2Windows).Outcome);

        KioskSelfTestCheckDTO broken = Get(EvaluateWebView2(new WebView2Facts(version, null, [new ProjectionWindow(0, true, false), new ProjectionWindow(1, false, false)])), SelfTestCheckKeys.WebView2Windows);
        Assert.AreEqual(SelfTestCodes.WindowNotLoaded, broken.Code);
        StringAssert.StartsWith(broken.Detail, "Screen 2 ");

        KioskSelfTestCheckDTO offline = Get(EvaluateWebView2(new WebView2Facts(version, null, [new ProjectionWindow(0, true, true)])), SelfTestCheckKeys.WebView2Windows);
        Assert.AreEqual(SelfTestOutcome.Warn, offline.Outcome);

        Assert.AreEqual(SelfTestOutcome.Pass, Get(EvaluateWebView2(new WebView2Facts(version, null, [new ProjectionWindow(0, true, false)])), SelfTestCheckKeys.WebView2Windows).Outcome);
    }

    // ---- Shared bits ----

    [TestMethod]
    public void Truncate_CapsDetailLength()
    {
        string longText = new('x', 1000);

        Assert.AreEqual(MaxDetailLength, Check("k", SelfTestOutcome.Fail, null, longText).Detail!.Length);
        Assert.AreEqual("short", Truncate("short"));
        Assert.IsNull(Truncate(null));
    }

    [TestMethod]
    public void GroupFailed_SaysWhetherItTimedOutOrCrashed()
    {
        StringAssert.Contains(GroupFailed(SelfTestCheckKeys.DmxDriver, SelfTestCodes.TimedOut, null).Detail, "too long");
        StringAssert.Contains(GroupFailed(SelfTestCheckKeys.DmxDriver, SelfTestCodes.Exception, "boom").Detail, "boom");
    }

    [TestMethod]
    public void CheckKeys_AreUniqueAndGrouped()
    {
        Assert.AreEqual(SelfTestCheckKeys.All.Count, SelfTestCheckKeys.All.Distinct().Count());
        Assert.AreEqual(SelfTestCheckKeys.GroupCapture, SelfTestCheckKeys.GroupOf(SelfTestCheckKeys.CaptureTraffic));
        Assert.IsTrue(SelfTestCheckKeys.IsKnown(SelfTestCheckKeys.WebView2Windows));
        Assert.IsFalse(SelfTestCheckKeys.IsKnown("printer.paper"));
    }
}
