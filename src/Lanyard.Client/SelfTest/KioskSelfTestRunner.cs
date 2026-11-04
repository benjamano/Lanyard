using Lanyard.Client.AudioDevices;
using Lanyard.Client.PacketSniffing;
using Lanyard.Client.ProjectionPrograms;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Lanyard.Shared.Kiosk;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Lanyard.Client.SelfTest;

/// <summary>
/// Runs the hardware self-test an Admin starts from the Clients page. Each group of checks runs on
/// its own with a time limit, so a hung driver or missing device only fails its own rows. Nothing
/// here opens hardware the kiosk is already using: DMX and packet capture are read from the
/// services that own them, and the test tone and DMX flash only run when nothing else is playing.
/// </summary>
public sealed class KioskSelfTestRunner(
    IAudioDeviceService audioDeviceService,
    IAudioEndpointProvider audioEndpoints,
    ITonePlayer tonePlayer,
    IMusicPlayer musicPlayer,
    IAudioClipPlayer audioClipPlayer,
    DmxController dmxController,
    IPacketSniffer packetSniffer,
    ICaptureDeviceCatalog captureDevices,
    IWebView2RuntimeProbe webView2,
    IProjectionProgramsService projectionPrograms,
    TimeProvider timeProvider,
    ILogger<KioskSelfTestRunner> logger)
{
    private static readonly TimeSpan ToneLength = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FlashLength = TimeSpan.FromSeconds(1);

    // Long enough for several ~25 ms DMX refresh frames.
    private static readonly TimeSpan FrameSampleWindow = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan AudioTimeout = TimeSpan.FromSeconds(6);

    // Constructing the FTDI driver can itself take up to 5s on a bad machine.
    private static readonly TimeSpan DmxTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WebView2Timeout = TimeSpan.FromSeconds(5);

    private readonly IAudioDeviceService _audioDeviceService = audioDeviceService;
    private readonly IAudioEndpointProvider _audioEndpoints = audioEndpoints;
    private readonly ITonePlayer _tonePlayer = tonePlayer;
    private readonly IMusicPlayer _musicPlayer = musicPlayer;
    private readonly IAudioClipPlayer _audioClipPlayer = audioClipPlayer;
    private readonly DmxController _dmxController = dmxController;
    private readonly IPacketSniffer _packetSniffer = packetSniffer;
    private readonly ICaptureDeviceCatalog _captureDevices = captureDevices;
    private readonly IWebView2RuntimeProbe _webView2 = webView2;
    private readonly IProjectionProgramsService _projectionPrograms = projectionPrograms;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<KioskSelfTestRunner> _logger = logger;

    // One run at a time: two tones or two flashes at once would only confuse whoever is listening.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<KioskSelfTestResultDTO> RunAsync(KioskSelfTestRequestDTO request, string? clientVersion)
    {
        await _gate.WaitAsync();

        try
        {
            KioskSelfTestResultDTO result = new()
            {
                RunId = request.RunId,
                ClientVersion = clientVersion,
                StartedUtc = Now()
            };

            // The groups touch different hardware, so they run side by side to keep the whole test short.
            List<KioskSelfTestCheckDTO>[] groups = await Task.WhenAll(
                RunGroupAsync(SelfTestCheckKeys.AudioDevice, AudioTimeout, CheckAudioAsync),
                RunGroupAsync(SelfTestCheckKeys.DmxDriver, DmxTimeout, () => CheckDmxAsync(request.FlashDmx)),
                RunGroupAsync(SelfTestCheckKeys.CaptureDriver, CaptureTimeout, CheckCaptureAsync),
                RunGroupAsync(SelfTestCheckKeys.WebView2Runtime, WebView2Timeout, CheckWebView2Async));

            result.Checks = groups.SelectMany(x => x).Take(SelfTestRules.MaxChecks).ToList();
            result.FinishedUtc = Now();

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<KioskSelfTestCheckDTO>> RunGroupAsync(string firstKey, TimeSpan timeout, Func<Task<List<KioskSelfTestCheckDTO>>> run)
    {
        try
        {
            return await Task.Run(run).WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Self-test checks for {Group} took longer than {Timeout}s and were abandoned", SelfTestCheckKeys.GroupOf(firstKey), timeout.TotalSeconds);
            return [SelfTestRules.GroupFailed(firstKey, SelfTestCodes.TimedOut, null)];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Self-test checks for {Group} failed", SelfTestCheckKeys.GroupOf(firstKey));
            return [SelfTestRules.GroupFailed(firstKey, SelfTestCodes.Exception, ex.Message)];
        }
    }

    private async Task<List<KioskSelfTestCheckDTO>> CheckAudioAsync()
    {
        (List<SelfTestRules.AudioEndpoint> devices, string? defaultId) = _audioEndpoints.GetRenderEndpoints();

        Result<PlaybackState> music = _musicPlayer.GetPlaybackStatus();
        bool musicPlaying = music.IsSuccess && music.Data == PlaybackState.Playing;

        SelfTestToneResult tone;
        string? toneError = null;

        if (SelfTestRules.ToneBlockedBy(musicPlaying, _audioClipPlayer.IsPlaying) is SelfTestToneResult blocked)
        {
            tone = blocked;
        }
        else if (defaultId == null)
        {
            tone = SelfTestToneResult.SkippedNoOutput;
        }
        else
        {
            try
            {
                _logger.LogInformation("Self-test: playing a {Seconds}s test tone", ToneLength.TotalSeconds);
                await _tonePlayer.PlayAsync(ToneLength);
                tone = SelfTestToneResult.Played;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Self-test: the test tone failed to play");
                tone = SelfTestToneResult.Failed;
                toneError = ex.Message;
            }
        }

        return SelfTestRules.EvaluateAudio(new SelfTestRules.AudioFacts(
            _audioDeviceService.PreferredDeviceId,
            _audioDeviceService.PreferredDeviceName,
            devices,
            defaultId,
            tone,
            toneError));
    }

    private async Task<List<KioskSelfTestCheckDTO>> CheckDmxAsync(bool flashRequested)
    {
        // Don't load the FTDI driver on a kiosk that has never been asked to drive lights.
        if (!_dmxController.IsConfigured)
        {
            return SelfTestRules.EvaluateDmx(new SelfTestRules.DmxFacts(false, false, null, [], false, 0, 0, null, SelfTestFlashResult.NotRequested, null));
        }

        bool driverAvailable = _dmxController.TryListDevices(out List<(int Index, string? Description, string? SerialNumber)> found);
        List<SelfTestRules.FtdiDevice> devices = found.Select(d => new SelfTestRules.FtdiDevice(d.Index, d.Description, d.SerialNumber)).ToList();

        bool open = _dmxController.IsDeviceOpen;
        long framesBefore = _dmxController.FramesWritten;

        if (open)
        {
            await Task.Delay(FrameSampleWindow);
        }

        long framesAfter = _dmxController.FramesWritten;
        string? writeError = _dmxController.LastWriteError;

        SelfTestFlashResult flash = SelfTestFlashResult.NotRequested;
        string? flashError = null;

        if (flashRequested)
        {
            if (!open)
            {
                flash = SelfTestFlashResult.SkippedPortNotOpen;
            }
            else if (!SelfTestRules.CanFlashDmx(_dmxController.LastNonZeroFrameUtc, Now()))
            {
                flash = SelfTestFlashResult.SkippedOutputLive;
            }
            else
            {
                try
                {
                    _logger.LogInformation("Self-test: flashing every DMX channel for {Seconds}s", FlashLength.TotalSeconds);
                    await _dmxController.FlashAsync(FlashLength);
                    flash = SelfTestFlashResult.Flashed;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Self-test: the DMX flash failed");
                    flash = SelfTestFlashResult.Failed;
                    flashError = ex.Message;
                }
            }
        }

        return SelfTestRules.EvaluateDmx(new SelfTestRules.DmxFacts(
            true, driverAvailable, _dmxController.ConfiguredIndex, devices, open, framesBefore, framesAfter, writeError, flash, flashError));
    }

    private Task<List<KioskSelfTestCheckDTO>> CheckCaptureAsync()
    {
        if (!_packetSniffer.IsConfigured)
        {
            return Task.FromResult(SelfTestRules.EvaluateCapture(
                new SelfTestRules.CaptureFacts(false, false, null, [], false, null, null, null, null), Now()));
        }

        bool driverAvailable = _captureDevices.TryListDevices(out List<SelfTestRules.CaptureDevice> devices, out string? error);

        if (!driverAvailable)
        {
            _logger.LogWarning("Self-test: Npcap couldn't be loaded: {Error}", error);
        }

        return Task.FromResult(SelfTestRules.EvaluateCapture(new SelfTestRules.CaptureFacts(
            true,
            driverAvailable,
            _packetSniffer.PreferredMacAddress,
            devices,
            _packetSniffer.IsCapturing,
            _packetSniffer.ActiveDeviceName,
            _packetSniffer.CaptureStartedUtc,
            _packetSniffer.LastPacketUtc,
            _packetSniffer.LastGamePacketUtc,
            _packetSniffer.CaptureFilter != null), Now()));
    }

    private async Task<List<KioskSelfTestCheckDTO>> CheckWebView2Async()
    {
        string? version = _webView2.GetRuntimeVersion(out string? error);

        List<ClientProjectionWindowDTO> windows = await _projectionPrograms.ListWindowsAsync();

        return SelfTestRules.EvaluateWebView2(new SelfTestRules.WebView2Facts(
            version,
            error,
            windows.Select(w => new SelfTestRules.ProjectionWindow(w.DisplayIndex, w.IsLoaded, w.IsOffline)).ToList()));
    }

    private DateTime Now()
    {
        return _timeProvider.GetUtcNow().UtcDateTime;
    }
}
