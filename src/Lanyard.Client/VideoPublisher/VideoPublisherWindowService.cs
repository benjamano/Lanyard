using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;

namespace Lanyard.Client.VideoPublisher;

/// <summary>
/// Hosts the hidden Edge window running the server's /video-publisher page, which captures
/// this machine's video devices and publishes them over WebRTC to kiosks on other clients.
/// </summary>
public class VideoPublisherWindowService(ILogger<VideoPublisherWindowService> logger) : IVideoPublisherWindowService
{
    private readonly ILogger<VideoPublisherWindowService> _logger = logger;

    private Process? _publisherProcess;
    private readonly object _lock = new();

    public void EnsureRunning(string publisherToken)
    {
        lock (_lock)
        {
            // Kill-then-relaunch: the server only sends StartVideoPublisher when it has no live
            // publisher registration, so an existing process here is a zombie (e.g. its circuit
            // died after a server restart) and must be replaced, never reused.
            StopInternal();

            string clientId = Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!;
            string url = $"{Environment.GetEnvironmentVariable("LANYARD_SERVER_URL")}/video-publisher/{clientId}?token={Uri.EscapeDataString(publisherToken)}";

            string userDataDir = Path.Combine(Path.GetTempPath(), "LanyardVideoPublisher", clientId);

            Directory.CreateDirectory(userDataDir);

            // getUserMedia only exists on secure origins; mirror the kiosk launch recipe.
            string mediaCaptureArgs = "--auto-accept-camera-and-microphone-capture ";

            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? serverUri)
                && serverUri.Scheme == Uri.UriSchemeHttp
                && !serverUri.IsLoopback)
            {
                string origin = serverUri.GetLeftPart(UriPartial.Authority);
                mediaCaptureArgs += $"--unsafely-treat-insecure-origin-as-secure=\"{origin}\" ";
            }

            string args =
                $"--app=\"{url}\" " +
                $"--user-data-dir=\"{userDataDir}\" " +
                // Off-screen: the page has no UI worth showing; it only captures and publishes.
                $"--window-position=-32000,-32000 " +
                $"--window-size=480,360 " +
                $"--no-first-run --disable-session-crashed-bubble " +
                mediaCaptureArgs +
                // Real LAN host ICE candidates instead of mDNS .local hostnames, and no
                // throttling of the hidden window's timers/rendering while it streams.
                $"--disable-features=WebRtcHideLocalIpsWithMdns " +
                $"--autoplay-policy=no-user-gesture-required " +
                $"--disable-background-timer-throttling " +
                $"--disable-backgrounding-occluded-windows " +
                $"--disable-renderer-backgrounding";

            _logger.LogInformation("Starting video publisher window with URL: {Url}", url);

            _publisherProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
                Arguments = args,
                UseShellExecute = false
            });
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopInternal();
        }
    }

    private void StopInternal()
    {
        try
        {
            if (_publisherProcess != null)
            {
                _publisherProcess.Kill();
                _publisherProcess.WaitForExit();

                _logger.LogInformation("Video publisher window stopped.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill video publisher process.");
        }
        finally
        {
            _publisherProcess?.Dispose();
            _publisherProcess = null;
        }
    }
}
