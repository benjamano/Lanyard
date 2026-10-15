using Lanyard.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;

namespace Lanyard.Client.ProjectionPrograms;

public partial class ProjectionWindow : Window
{
    private string _url = string.Empty;
    private ILogger? _logger;
    private int _failedNavigations;
    private bool _isClosed;

    // Offline player: the page may not have loaded when the runner posts its first step, so the
    // latest theme and content message are held and re-sent once it has.
    public const string OfflinePlayerHost = "lanyard-offline.local";
    public const string OfflineMediaHost = "lanyard-media.local";
    private static readonly JsonSerializerOptions MessageJsonOptions = new(JsonSerializerDefaults.Web);
    private bool _pageLoaded;
    private string? _pendingTheme;
    private string? _pendingContent;

    /// <summary>True when this window shows the offline player rather than the server's kiosk page.</summary>
    public bool IsOffline { get; private set; }

    /// <summary>
    /// True when this window closed itself because the WebView2 browser process died, which
    /// leaves the control unusable. The owner reopens the program instead of treating the
    /// close as the program finishing.
    /// </summary>
    public bool ClosedBecauseBrowserProcessFailed { get; private set; }

    public ProjectionWindow()
    {
        InitializeComponent();

        Closed += (_, _) =>
        {
            _isClosed = true;
            WebView.Dispose();
        };
    }

    // Mirrors the previous Edge-process window placement: positioned at the target monitor's
    // top-left corner; fullscreen takes over the full monitor bounds regardless of the
    // configured width/height (matching --edge-kiosk-type=fullscreen), otherwise the window is
    // sized/positioned in the corner of the target screen at the configured (halved) size.
    public void ApplyLayout(Screen screen, int width, int height, bool isFullScreen, bool isBorderless)
    {
        WindowStyle = isBorderless ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = isBorderless ? ResizeMode.NoResize : ResizeMode.CanResize;

        Left = screen.Bounds.Left;
        Top = screen.Bounds.Top;

        if (isFullScreen)
        {
            Width = screen.Bounds.Width;
            Height = screen.Bounds.Height;
        }
        else
        {
            // Preserved as-is from the previous Edge --window-size flag - the halving looks like
            // it might be unintentional, but changing it wasn't part of this change's scope.
            Width = width / 2;
            Height = height / 2;
        }
    }

    public async Task InitializeAsync(CoreWebView2Environment environment, string url, ILogger logger)
    {
        _url = url;
        _logger = logger;

        await WebView.EnsureCoreWebView2Async(environment);

        WebView.CoreWebView2.PermissionRequested += OnPermissionRequested;
        WebView.CoreWebView2.WindowCloseRequested += OnWindowCloseRequested;
        WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        WebView.CoreWebView2.ProcessFailed += OnProcessFailed;

        WebView.CoreWebView2.Navigate(url);
    }

    /// <summary>
    /// Shows the offline player (bundled with the client) instead of the server's page. Videos are
    /// served from the offline cache folder through a second virtual host, so they play from disk
    /// with seeking, and nothing on the kiosk listens on a port.
    /// </summary>
    public async Task InitializeOfflineAsync(CoreWebView2Environment environment, string mediaFolder, ILogger logger)
    {
        IsOffline = true;
        _logger = logger;
        _url = $"https://{OfflinePlayerHost}/index.html";

        await WebView.EnsureCoreWebView2Async(environment);

        string playerFolder = Path.Combine(AppContext.BaseDirectory, "ProjectionPrograms", "Offline", "Player");

        WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(OfflinePlayerHost, playerFolder, CoreWebView2HostResourceAccessKind.Allow);
        WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(OfflineMediaHost, mediaFolder, CoreWebView2HostResourceAccessKind.Allow);

        WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        WebView.CoreWebView2.ProcessFailed += OnProcessFailed;

        WebView.CoreWebView2.Navigate(_url);
    }

    /// <summary>Sends a message to the offline player. Safe to call from any thread.</summary>
    public void PostOfflineMessage(object message, bool isTheme = false)
    {
        string json = JsonSerializer.Serialize(message, MessageJsonOptions);

        Dispatcher.InvokeAsync(() =>
        {
            if (isTheme)
            {
                _pendingTheme = json;
            }
            else
            {
                _pendingContent = json;
            }

            if (_pageLoaded && !_isClosed)
            {
                WebView.CoreWebView2?.PostWebMessageAsJson(json);
            }
        });
    }

    private void SendPendingOfflineMessages()
    {
        foreach (string? json in new[] { _pendingTheme, _pendingContent })
        {
            if (json != null)
            {
                WebView.CoreWebView2?.PostWebMessageAsJson(json);
            }
        }
    }

    // Without this, a kiosk that opens its window while the server is unreachable sits on
    // Chromium's "can't reach this page" until someone restarts it. Retries with the same
    // backoff as the SignalR connect loop. Runs on the dispatcher, so the await resumes there too.
    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _failedNavigations = 0;

            if (IsOffline)
            {
                // Also covers a reload after a renderer crash, which starts the page blank again.
                _pageLoaded = true;
                SendPendingOfflineMessages();
            }

            return;
        }

        // Cancelled means something else (a newer Navigate, or the window closing) replaced
        // this navigation, so there's nothing to retry.
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
        {
            return;
        }

        _failedNavigations++;
        TimeSpan delay = ConnectRetryDelay.For(_failedNavigations, unauthorized: false);

        _logger?.LogWarning("Projection page failed to load ({WebErrorStatus}); retrying in {DelaySeconds} seconds (attempt {Attempt})",
            e.WebErrorStatus, delay.TotalSeconds, _failedNavigations);

        await Task.Delay(delay);

        if (_isClosed)
        {
            return;
        }

        try
        {
            WebView.CoreWebView2?.Navigate(_url);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Retrying the projection page failed");
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (_isClosed)
        {
            return;
        }

        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            // The whole WebView2 is dead and can't be reused; close so the owner reopens it.
            _logger?.LogError("WebView2 browser process exited ({Reason}); reopening the projection window", e.Reason);

            ClosedBecauseBrowserProcessFailed = true;
            Close();
            return;
        }

        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            // The browser survives a renderer crash or hang; reloading starts a fresh renderer.
            _logger?.LogWarning("WebView2 render process failed ({Kind}, {Reason}); reloading the projection page", e.ProcessFailedKind, e.Reason);

            try
            {
                WebView.CoreWebView2?.Reload();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Reloading the projection page failed");
            }
        }
    }

    // Each program gets a fresh user-data-dir (see CoreWebView2Environment creation), so
    // camera/mic permission grants never persist across programs; auto-accept is required for
    // Live Capture steps to start without a prompt.
    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone)
        {
            e.State = CoreWebView2PermissionState.Allow;
        }
    }

    // Fires when the kiosk page calls window.close() - closing the window here is what lets
    // TriggerTemporaryProjectionProgramAsync's completion signal fire, same as the previous
    // "await process exit" contract.
    private void OnWindowCloseRequested(object? sender, object e)
    {
        Close();
    }
}
