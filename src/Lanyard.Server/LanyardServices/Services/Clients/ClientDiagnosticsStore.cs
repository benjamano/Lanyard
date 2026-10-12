using Lanyard.Shared.DTO;
using System.Collections.Concurrent;

namespace Lanyard.Application.Services;

/// <summary>
/// Holds remote diagnostics results in memory for a short time, and nowhere else: no database,
/// file storage or telemetry. Log tails can contain customer or staff details, so each client
/// keeps only its latest capture and it is dropped after <see cref="Retention"/>, or when the
/// server restarts. A singleton, because the scoped service that requests captures is created
/// per circuit and the results must outlive it.
/// </summary>
public class ClientDiagnosticsStore(TimeProvider timeProvider) : IClientDiagnosticsStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan LogTailCooldown = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _timeProvider = timeProvider;

    private readonly ConcurrentDictionary<Guid, ClientLogTailCapture> _logTails = new();

    private readonly ConcurrentDictionary<Guid, DateTime> _logTailsInFlight = new();

    private readonly ConcurrentDictionary<Guid, DateTime> _lastLogTailStartedUtc = new();

    public bool TryBeginLogTail(Guid clientId, out string? reason)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        lock (_lastLogTailStartedUtc)
        {
            if (_logTailsInFlight.ContainsKey(clientId))
            {
                reason = "The log is already being fetched from this kiosk.";
                return false;
            }

            if (_lastLogTailStartedUtc.TryGetValue(clientId, out DateTime lastStarted) && now - lastStarted < LogTailCooldown)
            {
                reason = "The log was fetched a moment ago. Try again in a few seconds.";
                return false;
            }

            _logTailsInFlight[clientId] = now;
            _lastLogTailStartedUtc[clientId] = now;
        }

        reason = null;
        return true;
    }

    public void EndLogTail(Guid clientId)
    {
        _logTailsInFlight.TryRemove(clientId, out _);
    }

    public ClientLogTailCapture SaveLogTail(Guid clientId, ClientLogTailDTO logTail, string requestedBy)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        ClientLogTailCapture capture = new()
        {
            ClientId = clientId,
            LogTail = logTail,
            RequestedBy = requestedBy,
            ReceivedAtUtc = now,
            ExpiresAtUtc = now + Retention
        };

        _logTails[clientId] = capture;

        PruneExpired(now);

        return capture;
    }

    public ClientLogTailCapture? GetLatestLogTail(Guid clientId)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        PruneExpired(now);

        return _logTails.TryGetValue(clientId, out ClientLogTailCapture? capture) ? capture : null;
    }

    private void PruneExpired(DateTime now)
    {
        foreach (KeyValuePair<Guid, ClientLogTailCapture> entry in _logTails)
        {
            if (entry.Value.ExpiresAtUtc <= now)
            {
                _logTails.TryRemove(entry);
            }
        }
    }
}
