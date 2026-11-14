using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Lanyard.App.Services.CircuitShutdown;

// Drains from an ApplicationStopping callback rather than IHostedLifecycleService.StoppingAsync:
// on SIGTERM the host fires ApplicationStopping first, and the circuit connections start closing
// from it, so by StoppingAsync every circuit already reports "not connected" and refuses to pause.
// Token callbacks run newest-first, so registering in StartedAsync (after the framework's own
// registrations) puts ours ahead of them, and blocking in it holds them back until browsers pause.
public sealed class CircuitShutdownCoordinator(
    IHostApplicationLifetime lifetime,
    IOptions<CircuitShutdownOptions> options,
    ILogger<CircuitShutdownCoordinator> logger) : ICircuitShutdownCoordinator, IHostedLifecycleService, IDisposable
{
    private readonly IHostApplicationLifetime _lifetime = lifetime;
    private CancellationTokenRegistration _stoppingRegistration;
    private int _drainStarted;
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<bool>>> _circuits = new();
    private readonly TaskCompletionSource _allPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CircuitShutdownOptions _options = options.Value;
    private readonly ILogger<CircuitShutdownCoordinator> _logger = logger;
    private volatile bool _isStopping;

    public bool IsStopping => _isStopping;

    public int OpenCircuitCount => _circuits.Count;

    public void Register(string circuitId, Func<CancellationToken, Task<bool>> requestPause)
    {
        _circuits[circuitId] = requestPause;

        // A browser that resumes or connects while we're draining has landed on the old server
        // (the new one wasn't taking traffic yet). Pause it again so it retries elsewhere rather
        // than being cut off when this process exits.
        if (_isStopping)
        {
            _ = RequestPauseAsync(circuitId, requestPause, CancellationToken.None);
        }
    }

    public void Unregister(string circuitId)
    {
        _circuits.TryRemove(circuitId, out _);

        if (_isStopping && _circuits.IsEmpty)
        {
            _allPaused.TrySetResult();
        }
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        _isStopping = true;
        if (Interlocked.Exchange(ref _drainStarted, 1) == 1)
        {
            return;
        }

        KeyValuePair<string, Func<CancellationToken, Task<bool>>>[] open = _circuits.ToArray();
        if (open.Length == 0)
        {
            _logger.LogInformation("Server stopping with no open circuits to pause");
            return;
        }

        _logger.LogInformation("Server stopping: asking {CircuitCount} open circuits to pause", open.Length);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.DrainTimeout);

        await Task.WhenAll(open.Select(c => RequestPauseAsync(c.Key, c.Value, timeout.Token)));

        if (_circuits.IsEmpty)
        {
            _allPaused.TrySetResult();
        }

        try
        {
            await _allPaused.Task.WaitAsync(timeout.Token);
            _logger.LogInformation("All {CircuitCount} circuits paused before shutdown", open.Length);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Stopping with {RemainingCount} of {CircuitCount} circuits still open after {DrainTimeout}; those browsers will see a normal reconnect",
                _circuits.Count, open.Length, _options.DrainTimeout);
        }
    }

    private async Task RequestPauseAsync(string circuitId, Func<CancellationToken, Task<bool>> requestPause, CancellationToken cancellationToken)
    {
        try
        {
            bool accepted = await requestPause(cancellationToken);
            if (accepted == false)
            {
                // Already disconnected or already pausing; nothing will unregister it, so stop waiting on it.
                _logger.LogInformation("Circuit {CircuitId} did not accept the pause request", circuitId);
                Unregister(circuitId);
            }
        }
        catch (OperationCanceledException)
        {
            // Drain timed out; the warning in DrainAsync covers it.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ask circuit {CircuitId} to pause", circuitId);
            Unregister(circuitId);
        }
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        // Blocking is deliberate (see the class comment). DrainAsync bounds itself by DrainTimeout.
        _stoppingRegistration = _lifetime.ApplicationStopping.Register(() => DrainAsync(CancellationToken.None).GetAwaiter().GetResult());
        return Task.CompletedTask;
    }

    // Covers a stop that didn't go through ApplicationStopping first; a no-op if the drain already ran.
    public Task StoppingAsync(CancellationToken cancellationToken) => DrainAsync(cancellationToken);

    public void Dispose() => _stoppingRegistration.Dispose();

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
