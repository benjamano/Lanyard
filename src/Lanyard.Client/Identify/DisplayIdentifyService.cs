using Lanyard.Shared.DTO;
using Lanyard.Shared.Kiosk;
using Microsoft.Extensions.Logging;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;

namespace Lanyard.Client.Identify;

public interface IDisplayIdentifyService
{
    /// <summary>
    /// Shows the numbered overlay on every screen for the request's duration and returns at once
    /// with the screens it found. A second call replaces the overlays and restarts the timer.
    /// </summary>
    Task<List<IdentifiedDisplayDTO>> ShowAsync(DisplayIdentifyRequestDTO request);
}

/// <summary>
/// The screens Windows reports, numbered from one read of <see cref="Screen.AllScreens"/>. Every
/// index the kiosk sends the server comes from here, so a list can't mix two orders if the screens
/// change mid-read.
/// </summary>
public static class DisplaySnapshot
{
    public static List<(int Index, Screen Screen)> Take()
    {
        return Screen.AllScreens.Select((screen, index) => (index, screen)).ToList();
    }

    public static List<SelfTestRules.DisplayFacts> TakeFacts()
    {
        return Take()
            .Select(x => new SelfTestRules.DisplayFacts(x.Index, x.Screen.DeviceName, x.Screen.Bounds.Width, x.Screen.Bounds.Height, x.Screen.Primary))
            .ToList();
    }
}

public sealed class DisplayIdentifyService(ILogger<DisplayIdentifyService> logger) : IDisplayIdentifyService
{
    private readonly ILogger<DisplayIdentifyService> _logger = logger;

    // Only touched on the dispatcher.
    private readonly List<DisplayIdentifyOverlayWindow> _open = [];
    private DispatcherTimer? _timer;

    public async Task<List<IdentifiedDisplayDTO>> ShowAsync(DisplayIdentifyRequestDTO request)
    {
        int seconds = DisplayIdentifyRules.ClampDuration(request.DurationSeconds);

        return await System.Windows.Application.Current!.Dispatcher.InvokeAsync(() =>
        {
            CloseAll();

            List<IdentifiedDisplayDTO> displays = [];

            foreach ((int index, Screen screen) in DisplaySnapshot.Take().Take(DisplayIdentifyRules.MaxDisplays))
            {
                IdentifiedDisplayDTO display = new()
                {
                    Index = index,
                    DeviceName = DisplayIdentifyRules.Truncate(screen.DeviceName, DisplayIdentifyRules.MaxDeviceNameLength) ?? string.Empty,
                    Width = screen.Bounds.Width,
                    Height = screen.Bounds.Height,
                    IsPrimary = screen.Primary
                };

                try
                {
                    DisplayIdentifyOverlayWindow window = new();
                    window.ShowScreen(index, screen, DisplayIdentifyRules.ProgramFor(request.Labels, index));
                    window.Show();

                    _open.Add(window);
                    display.Shown = true;
                }
                catch (Exception ex)
                {
                    // One screen failing mustn't stop the others being identified.
                    _logger.LogWarning(ex, "Couldn't show the identify overlay on screen {Index} ({DeviceName})", index, screen.DeviceName);
                    display.Error = DisplayIdentifyRules.Truncate(ex.Message, DisplayIdentifyRules.MaxErrorLength);
                }

                displays.Add(display);
            }

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _timer.Tick += (_, _) => CloseAll();
            _timer.Start();

            _logger.LogInformation("Showing the identify overlay on {Shown} of {Count} screens for {Seconds}s", displays.Count(x => x.Shown), displays.Count, seconds);

            return displays;
        });
    }

    private void CloseAll()
    {
        _timer?.Stop();
        _timer = null;

        foreach (DisplayIdentifyOverlayWindow window in _open)
        {
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't close an identify overlay");
            }
        }

        _open.Clear();
    }
}
