namespace Lanyard.App.Components;

/// <summary>
/// Collapses a burst of "please re-render" requests into one render after a short window.
/// For components that subscribe to high-frequency server events (laser-game hits arrive per
/// packet, DMX values per channel) a render per event is what makes a dashboard crawl; the
/// values are applied immediately, only the paint is deferred.
/// </summary>
public sealed class RenderCoalescer : IDisposable
{
    private readonly Func<Task> _render;
    private readonly int _windowMs;
    private readonly Timer _timer;
    private int _pending;

    public RenderCoalescer(Func<Task> render, int windowMs = 100)
    {
        _render = render;
        _windowMs = windowMs;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Schedules a render if one is not already pending.</summary>
    public void Request()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 0)
        {
            _timer.Change(_windowMs, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _pending, 0);
        _ = _render();
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
