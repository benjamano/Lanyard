using Lanyard.Client.SignalR;
using Lanyard.Shared.DTO;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Windows.Forms;

namespace Lanyard.Client.ProjectionPrograms;

public class ProjectionProgramsService(ILogger<ProjectionProgramsService> logger, ISignalRClient signalRClient) : IProjectionProgramsService
{
    private readonly ILogger<ProjectionProgramsService> _logger = logger;
    private readonly ISignalRClient _signalRClient = signalRClient;

    private List<ClientProjectionSettingsDTO> loadedProjectionPrograms = [];

    // One entry per physical display, keyed by the monitor's device name (resolved fresh from
    // DisplayIndex on every call) rather than the raw ordinal index - so tracking is based on
    // which physical monitor is actually being targeted right now, not a possibly-stale index
    // from before a replug/reorder. Entries remove themselves when their window closes.
    private readonly Dictionary<string, (Guid ProgramId, ProjectionWindow Window, TaskCompletionSource ClosedTcs)> _runningByDisplayKey = [];

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
        Screen screen = ResolveScreen(projectionProgram.DisplayIndex);
        string displayKey = screen.DeviceName;
        Guid programId = projectionProgram.ProjectionProgram.Id;

        if (_runningByDisplayKey.TryGetValue(displayKey, out (Guid ProgramId, ProjectionWindow Window, TaskCompletionSource ClosedTcs) running)
            && running.ProgramId == programId)
        {
            // This display is already showing the desired program - leave it running untouched.
            return;
        }

        _logger.LogInformation("Starting projection program: {program}", projectionProgram.ProjectionProgram.Name);
        _logger.LogInformation(" - Fullscreen: {isFullScreen}", projectionProgram.IsFullScreen);
        _logger.LogInformation(" - Borderless: {isBorderless}", projectionProgram.IsBorderless);
        _logger.LogInformation(" - Resolution: {width}x{height}", projectionProgram.Width, projectionProgram.Height);
        _logger.LogInformation(" - Display Index: {displayIndex}", projectionProgram.DisplayIndex);

        await HideWindowAsync(displayKey);

        await ShowWindowAsync(
            displayKey,
            screen,
            projectionProgram.Width,
            projectionProgram.Height,
            projectionProgram.IsFullScreen,
            projectionProgram.IsBorderless,
            programId,
            Guid.Parse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!),
            projectionProgram.DisplayIndex,
            isTemporaryTrigger: false);
    }

    /// <summary>
    /// Closes whatever projection window is open on a display. Called when the server's
    /// projection program runner reports that a triggered program finished its repeats -
    /// closing the window is what completes the ClosedTcs that
    /// <see cref="TriggerTemporaryProjectionProgramAsync"/> is awaiting, which in turn
    /// reports ProjectionProgramCompleted and restores this client's ambient projection.
    /// </summary>
    public async Task CloseWindowForDisplayAsync(int displayIndex)
    {
        Screen screen = ResolveScreen(displayIndex);

        _logger.LogInformation("Closing projection window on display {displayIndex} ({displayKey})", displayIndex, screen.DeviceName);

        await HideWindowAsync(screen.DeviceName);
    }

    private async Task HideWindowAsync(string displayKey)
    {
        await System.Windows.Application.Current!.Dispatcher.InvokeAsync(() =>
        {
            if (!_runningByDisplayKey.TryGetValue(displayKey, out (Guid ProgramId, ProjectionWindow Window, TaskCompletionSource ClosedTcs) running))
            {
                return;
            }

            try
            {
                // The Closed handler registered in ShowWindowAsync removes the dictionary entry
                // and completes ClosedTcs - no need to do either here.
                running.Window.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close projection window on display {displayKey}.", displayKey);
            }
        });
    }

    public async Task TriggerTemporaryProjectionProgramAsync(Guid projectionProgramId, int? displayIndex, Func<Task> onCompleted)
    {
        // A button/automation rule with no explicit target screen defaults to display 0,
        // rather than guessing from whichever program happened to load first.
        int resolvedDisplayIndex = displayIndex ?? 0;
        Screen screen = ResolveScreen(resolvedDisplayIndex);
        string displayKey = screen.DeviceName;

        ClientProjectionSettingsDTO? displaySettings = loadedProjectionPrograms
            .FirstOrDefault(x => x.DisplayIndex == resolvedDisplayIndex);

        int width = displaySettings?.Width ?? 1920;
        int height = displaySettings?.Height ?? 1080;
        bool isFullScreen = displaySettings?.IsFullScreen ?? true;
        bool isBorderless = displaySettings?.IsBorderless ?? true;

        await HideWindowAsync(displayKey);

        TaskCompletionSource closedTcs = await ShowWindowAsync(
            displayKey, screen, width, height, isFullScreen, isBorderless, projectionProgramId,
            Guid.Parse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!),
            resolvedDisplayIndex, isTemporaryTrigger: true);

        await closedTcs.Task;

        await onCompleted();
    }

    private static Screen ResolveScreen(int displayIndex)
    {
        Screen[] screens = Screen.AllScreens;

        int screenIndex = displayIndex;
        if (screenIndex < 0 || screenIndex >= screens.Length)
        {
            screenIndex = 0;
        }

        return screens[screenIndex];
    }

    private async Task<TaskCompletionSource> ShowWindowAsync(string displayKey, Screen screen, int width, int height, bool isFullScreen, bool isBorderless, Guid projectionProgramId, Guid clientId, int displayIndex, bool isTemporaryTrigger)
    {
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

        // display tells the server which run this page owns - the same client can project a
        // different program on each display, and the runner keys runs by (client, display).
        // temporary tells it this is the one-shot trigger path, so it closes this window again
        // once the program's repeats are done.
        string url = $"{Environment.GetEnvironmentVariable("LANYARD_SERVER_URL")}/kiosk/{clientId}/{projectionProgramId}"
            + $"?display={displayIndex}"
            + $"&temporary={(isTemporaryTrigger ? "true" : "false")}";

        if (!string.IsNullOrEmpty(viewerToken))
        {
            url += $"&token={Uri.EscapeDataString(viewerToken)}";
        }

        string userDataDir = Path.Combine(
            Path.GetTempPath(),
            "LanyardKiosk",
            projectionProgramId.ToString()
        );

        Directory.CreateDirectory(userDataDir);

        string additionalBrowserArguments = "--no-first-run --disable-session-crashed-bubble ";

        // getUserMedia only exists on secure origins; when the server is plain http on the LAN,
        // WebView2 must be told to treat that origin as secure or navigator.mediaDevices is undefined.
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? serverUri)
            && serverUri.Scheme == Uri.UriSchemeHttp
            && !serverUri.IsLoopback)
        {
            string origin = serverUri.GetLeftPart(UriPartial.Authority);
            additionalBrowserArguments += $"--unsafely-treat-insecure-origin-as-secure=\"{origin}\" ";
        }

        additionalBrowserArguments +=
            // The server uses the .NET dev cert over HTTPS, which is only valid for localhost and
            // untrusted on other machines; accept it so the kiosk loads without a cert interstitial.
            // The origin stays HTTPS (a valid secure context for camera + WebRTC).
            "--ignore-certificate-errors " +
            // Real LAN host ICE candidates (not mDNS .local names) so cross-client WebRTC
            // streams connect, and no gesture requirement for audible playback.
            "--disable-features=WebRtcHideLocalIpsWithMdns " +
            "--autoplay-policy=no-user-gesture-required";

        _logger.LogInformation("Opening page with URL: {url}", url);

        TaskCompletionSource closedTcs = new();

        await await System.Windows.Application.Current!.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                ProjectionWindow window = new();
                window.ApplyLayout(screen, width, height, isFullScreen, isBorderless);

                window.Closed += (_, _) =>
                {
                    _runningByDisplayKey.Remove(displayKey);
                    closedTcs.TrySetResult();
                };

                _runningByDisplayKey[displayKey] = (projectionProgramId, window, closedTcs);

                CoreWebView2EnvironmentOptions options = new(additionalBrowserArguments);
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);

                // Show() must happen before EnsureCoreWebView2Async (called inside InitializeAsync) -
                // the WebView2 control needs a realized parent HWND to complete its Chromium child-window
                // hosting setup, otherwise EnsureCoreWebView2Async hangs indefinitely.
                window.Show();

                await window.InitializeAsync(environment, url);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open projection window for program {ProgramId} on display {DisplayKey}.", projectionProgramId, displayKey);
            }
        });

        return closedTcs;
    }
}
