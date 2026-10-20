using Lanyard.Shared.Kiosk;
using Lanyard.Client.ProjectionPrograms.Offline;
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

    // The dictionary is only changed on the dispatcher thread; the heartbeat reads its count from
    // a timer thread, where a value one window stale is fine.
    public int RunningCount => _runningByDisplayKey.Count;

    // Offline playback: one runner per display key, only touched on the dispatcher thread.
    private readonly Dictionary<string, OfflineProjectionRunner> _offlineRunners = [];
    private volatile bool _isOffline;

    public bool IsOffline => _isOffline;

    public async Task StartProjectingAsync(IEnumerable<ClientProjectionSettingsDTO> projectionPrograms)
    {
        if (projectionPrograms == null || !projectionPrograms.Any())
        {
            return;
        }

        _logger.LogInformation("Found {programCount} programs to run", projectionPrograms.Count());

        loadedProjectionPrograms = [.. projectionPrograms];

        if (_isOffline)
        {
            // The server pushes programs as soon as it reconnects, before the connection has proved
            // stable. Keep them for the hand-back instead of replacing the offline screens now.
            _logger.LogInformation("Offline: keeping the server's programs for when the screens hand back");
            return;
        }

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
            && running.ProgramId == programId
            && !running.Window.IsOffline)
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

    public async Task<List<ClientProjectionWindowDTO>> ListWindowsAsync()
    {
        // Read on the dispatcher: the dictionary is only changed there, so this is a consistent snapshot.
        return await System.Windows.Application.Current!.Dispatcher.InvokeAsync(() =>
        {
            Screen[] screens = Screen.AllScreens;

            return _runningByDisplayKey
                .Select(x => new ClientProjectionWindowDTO
                {
                    DisplayKey = x.Key,
                    DisplayIndex = Array.FindIndex(screens, s => s.DeviceName == x.Key),
                    ProgramId = x.Value.ProgramId,
                    IsOffline = x.Value.Window.IsOffline,
                    IsLoaded = x.Value.Window.CanCapture
                })
                .OrderBy(x => x.DisplayIndex)
                .ToList();
        });
    }

    public async Task<byte[]?> CaptureWindowPngAsync(string displayKey)
    {
        // CapturePreviewAsync has to start on the UI thread, and the await resumes there too.
        return await await System.Windows.Application.Current!.Dispatcher.InvokeAsync(async () =>
        {
            if (!_runningByDisplayKey.TryGetValue(displayKey, out (Guid ProgramId, ProjectionWindow Window, TaskCompletionSource ClosedTcs) running))
            {
                return null;
            }

            return await running.Window.CapturePngAsync();
        });
    }

    public async Task TriggerTemporaryProjectionProgramAsync(Guid projectionProgramId, int? displayIndex, Func<Task> onCompleted)
    {
        if (_isOffline)
        {
            // Only reachable while reconnecting: the screens hand back to the server shortly, and
            // reporting completion lets it restore the ambient program as usual.
            _logger.LogInformation("Offline: ignoring triggered program {ProgramId} until the screens hand back to the server", projectionProgramId);
            await onCompleted();
            return;
        }

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

    private async Task ReopenAfterCrashAsync(Guid projectionProgramId, int displayIndex)
    {
        try
        {
            // A short pause stops a browser that dies on launch from turning into a tight loop.
            await Task.Delay(TimeSpan.FromSeconds(5));

            if (_isOffline)
            {
                // The offline player has taken the screen over; the hand-back reopens it.
                return;
            }

            ClientProjectionSettingsDTO? settings = loadedProjectionPrograms
                .FirstOrDefault(x => x.DisplayIndex == displayIndex && x.ProjectionProgram.Id == projectionProgramId);

            if (settings is null)
            {
                // The server has since assigned something else; its own push will open that.
                return;
            }

            await StartProjectionProgramAsync(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reopen projection program {ProgramId} on display {DisplayIndex} after a crash", projectionProgramId, displayIndex);
        }
    }

    public async Task EnterOfflineModeAsync(KioskOfflineManifestDTO manifest, IReadOnlySet<Guid> cachedVideoIds, string mediaFolder)
    {
        if (_isOffline)
        {
            return;
        }

        _isOffline = true;

        _logger.LogWarning("Offline: the server is unreachable, switching {DisplayCount} screens to their cached programs", manifest.Displays.Count);

        // Server pages (and any triggered program) can't run without the server, so every window
        // is replaced, not just those with something cached.
        foreach (string displayKey in _runningByDisplayKey.Keys.ToList())
        {
            await HideWindowAsync(displayKey);
        }

        foreach (KioskOfflineDisplayDTO display in manifest.Displays)
        {
            await ShowOfflineDisplayAsync(display, cachedVideoIds, mediaFolder);
        }
    }

    public async Task ExitOfflineModeAsync()
    {
        if (!_isOffline)
        {
            return;
        }

        _isOffline = false;

        _logger.LogInformation("Offline: the server is back, handing the screens back to it");

        await System.Windows.Application.Current!.Dispatcher.InvokeAsync(() =>
        {
            foreach (OfflineProjectionRunner runner in _offlineRunners.Values)
            {
                runner.Dispose();
            }

            _offlineRunners.Clear();
        });

        foreach (string displayKey in _runningByDisplayKey.Keys.ToList())
        {
            await HideWindowAsync(displayKey);
        }

        // The server starts its own run from the first step when each page loads.
        await StartProjectionLoopAsync();
    }

    private async Task ShowOfflineDisplayAsync(KioskOfflineDisplayDTO display, IReadOnlySet<Guid> cachedVideoIds, string mediaFolder)
    {
        Screen screen = ResolveScreen(display.DisplayIndex);
        string displayKey = screen.DeviceName;

        // Its own browser profile, so it never shares a cache with the server's pages.
        string userDataDir = Path.Combine(Path.GetTempPath(), "LanyardKiosk", $"offline-{display.DisplayIndex}");
        Directory.CreateDirectory(userDataDir);

        await await System.Windows.Application.Current!.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                ProjectionWindow window = new();
                window.ApplyLayout(screen, display.Width, display.Height, display.IsFullScreen, display.IsBorderless);

                TaskCompletionSource closedTcs = new();

                window.Closed += (_, _) =>
                {
                    _runningByDisplayKey.Remove(displayKey);

                    if (_offlineRunners.Remove(displayKey, out OfflineProjectionRunner? runner))
                    {
                        runner.Dispose();
                    }

                    closedTcs.TrySetResult();

                    if (window.ClosedBecauseBrowserProcessFailed && _isOffline)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5));

                            if (_isOffline)
                            {
                                await ShowOfflineDisplayAsync(display, cachedVideoIds, mediaFolder);
                            }
                        });
                    }
                };

                _runningByDisplayKey[displayKey] = (display.ProgramId, window, closedTcs);

                CoreWebView2EnvironmentOptions options = new("--no-first-run --disable-session-crashed-bubble --autoplay-policy=no-user-gesture-required");
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);

                // Show() before initialising, for the same reason as the online window.
                window.Show();

                await window.InitializeOfflineAsync(environment, mediaFolder, _logger);

                OfflineProjectionRunner offlineRunner = new(window, display, cachedVideoIds, _logger);
                _offlineRunners[displayKey] = offlineRunner;
                offlineRunner.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Offline: failed to open the offline player on display {DisplayIndex}", display.DisplayIndex);
            }
        });
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

        // Only on explicit opt-in, the same switch the hub connection uses (Security Issue 9.10):
        // for a dev server on the .NET dev cert, which is untrusted on other machines. A real
        // server has a valid certificate, and accepting any certificate would let anyone on the
        // venue network stand in for it.
        if (KioskUrls.AllowInsecureSsl())
        {
            additionalBrowserArguments += "--ignore-certificate-errors ";
        }

        additionalBrowserArguments +=
            // Real LAN host ICE candidates (not mDNS .local names) so cross-client WebRTC
            // streams connect, and no gesture requirement for audible playback.
            "--disable-features=WebRtcHideLocalIpsWithMdns " +
            "--autoplay-policy=no-user-gesture-required";

        // Without the query string: it holds the kiosk's viewer token.
        _logger.LogInformation("Opening page with URL: {url}", KioskUrls.WithoutQuery(url));

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

                    if (window.ClosedBecauseBrowserProcessFailed && !isTemporaryTrigger)
                    {
                        // An ambient program never ends by itself, so reopen it rather than leave
                        // the screen dark until the server next sends settings. A crashed temporary
                        // window is allowed to complete, which hands back to the ambient program.
                        _ = Task.Run(() => ReopenAfterCrashAsync(projectionProgramId, displayIndex));
                        return;
                    }

                    closedTcs.TrySetResult();
                };

                _runningByDisplayKey[displayKey] = (projectionProgramId, window, closedTcs);

                CoreWebView2EnvironmentOptions options = new(additionalBrowserArguments);
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);

                // Show() must happen before EnsureCoreWebView2Async (called inside InitializeAsync) -
                // the WebView2 control needs a realized parent HWND to complete its Chromium child-window
                // hosting setup, otherwise EnsureCoreWebView2Async hangs indefinitely.
                window.Show();

                await window.InitializeAsync(environment, url, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open projection window for program {ProgramId} on display {DisplayKey}.", projectionProgramId, displayKey);
            }
        });

        return closedTcs;
    }
}
