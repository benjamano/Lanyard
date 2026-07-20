using Lanyard.Client.SignalR;
using Lanyard.Shared.DTO;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace Lanyard.Client.ProjectionPrograms;

public class ProjectionProgramsService(ILogger<ProjectionProgramsService> logger, ISignalRClient signalRClient) : IProjectionProgramsService
{
    private readonly ILogger<ProjectionProgramsService> _logger = logger;
    private readonly ISignalRClient _signalRClient = signalRClient;

    private List<ClientProjectionSettingsDTO> loadedProjectionPrograms = [];

    // One entry per physical display; triggering/refreshing one display must never touch another's process.
    private readonly Dictionary<int, (Guid ProgramId, Process Process)> _runningByDisplay = [];

    public async Task StartProjectingAsync(IEnumerable<ClientProjectionSettingsDTO> projectionPrograms)
    {
        if (projectionPrograms == null || !projectionPrograms.Any())
        {
            return;
        }

        _logger.LogInformation("Found {programCount} programs to run", projectionPrograms.Count());

        loadedProjectionPrograms = [.. projectionPrograms];

        await StartProjectionLoopAsync();
    }

    private async Task StartProjectionLoopAsync()
    {
        foreach (ClientProjectionSettingsDTO projectionProgram in loadedProjectionPrograms)
        {
            await StartProjectionProgramAsync(projectionProgram);
        }
    }

    private async Task StartProjectionProgramAsync(ClientProjectionSettingsDTO projectionProgram)
    {
        int displayIndex = projectionProgram.DisplayIndex;
        Guid programId = projectionProgram.ProjectionProgram.Id;

        if (_runningByDisplay.TryGetValue(displayIndex, out (Guid ProgramId, Process Process) running)
            && running.ProgramId == programId
            && !running.Process.HasExited)
        {
            // This display is already showing the desired program - leave it running untouched.
            return;
        }

        _logger.LogInformation("Starting projection program: {program}", projectionProgram.ProjectionProgram.Name);
        _logger.LogInformation(" - Fullscreen: {isFullScreen}", projectionProgram.IsFullScreen);
        _logger.LogInformation(" - Borderless: {isBorderless}", projectionProgram.IsBorderless);
        _logger.LogInformation(" - Resolution: {width}x{height}", projectionProgram.Width, projectionProgram.Height);
        _logger.LogInformation(" - Display Index: {displayIndex}", displayIndex);

        HideWindow(displayIndex);

        await ShowWindow(displayIndex, projectionProgram.Width, projectionProgram.Height, projectionProgram.IsFullScreen, programId, Guid.Parse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!));
    }

    private void HideWindow(int displayIndex)
    {
        if (!_runningByDisplay.TryGetValue(displayIndex, out (Guid ProgramId, Process Process) running))
        {
            return;
        }

        try
        {
            running.Process.Kill();
            running.Process.WaitForExit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill kiosk process on display {displayIndex}.", displayIndex);
        }
        finally
        {
            running.Process.Dispose();
            _runningByDisplay.Remove(displayIndex);
        }
    }

    public async Task TriggerTemporaryProjectionProgramAsync(Guid projectionProgramId, int? displayIndex, Func<Task> onCompleted)
    {
        // A button/automation rule with no explicit target screen defaults to display 0,
        // rather than guessing from whichever program happened to load first.
        int resolvedDisplayIndex = displayIndex ?? 0;

        ClientProjectionSettingsDTO? displaySettings = loadedProjectionPrograms
            .FirstOrDefault(x => x.DisplayIndex == resolvedDisplayIndex);

        int width = displaySettings?.Width ?? 1920;
        int height = displaySettings?.Height ?? 1080;
        bool isFullScreen = displaySettings?.IsFullScreen ?? true;

        HideWindow(resolvedDisplayIndex);

        await ShowWindow(resolvedDisplayIndex, width, height, isFullScreen, projectionProgramId, Guid.Parse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!));

        if (_runningByDisplay.TryGetValue(resolvedDisplayIndex, out (Guid ProgramId, Process Process) running))
        {
            await running.Process.WaitForExitAsync();
        }

        await onCompleted();
    }

    async Task ShowWindow(int displayIndex, int width, int height, bool isFullScreen, Guid projectionProgramId, Guid clientId)
    {
        Screen[] screens = Screen.AllScreens;

        int screenIndex = displayIndex;
        if (screenIndex < 0 || screenIndex >= screens.Length)
            screenIndex = 0;

        Screen screen = screens[screenIndex];

        int x = screen.Bounds.Left;
        int y = screen.Bounds.Top;

        // A viewer token authorises this kiosk to open remote (cross-client) video streams.
        // Fetching it over the hub proves the kiosk was launched by this client, not by someone
        // who simply knows the URL. A failure here only disables remote capture, not the kiosk.
        string viewerToken = string.Empty;
        try
        {
            viewerToken = await _signalRClient.IssueKioskTokenAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to obtain kiosk viewer token; remote video capture will be unavailable.");
        }

        string url = $"{Environment.GetEnvironmentVariable("LANYARD_SERVER_URL")}/kiosk/{clientId}/{projectionProgramId}";

        if (!string.IsNullOrEmpty(viewerToken))
        {
            url += $"?token={Uri.EscapeDataString(viewerToken)}";
        }

        string userDataDir = Path.Combine(
            Path.GetTempPath(),
            "LanyardKiosk",
            projectionProgramId.ToString()
        );

        _logger.LogInformation("Opening page with URL: {url}", url);

        Directory.CreateDirectory(userDataDir);

        // Each program gets a fresh user-data-dir, so camera/mic permission grants never persist;
        // auto-accept is required for Live Capture steps to start without a prompt.
        // (Edge < 133 does not support this flag and would need the legacy
        // --use-fake-ui-for-media-stream instead — the two must not be combined.)
        string mediaCaptureArgs = "--auto-accept-camera-and-microphone-capture ";

        // getUserMedia only exists on secure origins; when the server is plain http on the LAN,
        // Edge must be told to treat that origin as secure or navigator.mediaDevices is undefined.
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? serverUri)
            && serverUri.Scheme == Uri.UriSchemeHttp
            && !serverUri.IsLoopback)
        {
            string origin = serverUri.GetLeftPart(UriPartial.Authority);
            mediaCaptureArgs += $"--unsafely-treat-insecure-origin-as-secure=\"{origin}\" ";
        }

        string args =
            $"--kiosk \"{url}\" " +
            $"--user-data-dir=\"{userDataDir}\" " +
            $"--window-position={x},{y} " +
            $"--window-size={width/2},{height/2} " +
            $"--no-first-run --disable-session-crashed-bubble " +
            mediaCaptureArgs +
            // The server uses the .NET dev cert over HTTPS, which is only valid for localhost and
            // untrusted on other machines; accept it so the kiosk loads without a cert interstitial.
            // The origin stays HTTPS (a valid secure context for camera + WebRTC).
            $"--ignore-certificate-errors " +
            // Real LAN host ICE candidates (not mDNS .local names) so cross-client WebRTC
            // streams connect, and no gesture requirement for audible playback.
            $"--disable-features=WebRtcHideLocalIpsWithMdns " +
            $"--autoplay-policy=no-user-gesture-required " +
            (isFullScreen ? "--edge-kiosk-type=fullscreen " : "");

        Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
            Arguments = args,
            UseShellExecute = false
        });

        if (process != null)
        {
            _runningByDisplay[displayIndex] = (projectionProgramId, process);
        }
    }
}