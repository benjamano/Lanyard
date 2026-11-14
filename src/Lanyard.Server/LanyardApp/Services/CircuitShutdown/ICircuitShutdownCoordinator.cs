namespace Lanyard.App.Services.CircuitShutdown;

// Tracks open Blazor circuits so that, when the server is stopping (a deploy or restart), it can
// ask each browser to pause its circuit gracefully. A paused browser keeps its session state and
// resumes onto the new server instead of dropping to "Connection lost" and reloading.
public interface ICircuitShutdownCoordinator
{
    bool IsStopping { get; }

    int OpenCircuitCount { get; }

    // requestPause wraps Circuit.RequestCircuitPauseAsync; it's a delegate so tests don't need a
    // real Circuit (its constructor is internal).
    void Register(string circuitId, Func<CancellationToken, Task<bool>> requestPause);

    void Unregister(string circuitId);

    Task DrainAsync(CancellationToken cancellationToken);
}
