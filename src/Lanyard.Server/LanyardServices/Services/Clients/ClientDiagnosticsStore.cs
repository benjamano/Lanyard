using Lanyard.Shared.DTO;
using System.Collections.Concurrent;

namespace Lanyard.Application.Services;

/// <summary>
/// Holds remote diagnostics results in memory for a short time, and nowhere else: no database,
/// file storage or telemetry. Log tails and screenshots can contain customer or staff details, so
/// each client keeps only its latest capture of each kind and it is dropped after
/// <see cref="Retention"/>, or when the server restarts. A singleton, because the scoped service
/// that requests captures is created per circuit and the results must outlive it.
/// </summary>
public class ClientDiagnosticsStore(TimeProvider timeProvider) : IClientDiagnosticsStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan LogTailCooldown = TimeSpan.FromSeconds(5);

    // Longer than the log tail's: each request captures and re-encodes every screen on the kiosk.
    public static readonly TimeSpan ScreenshotCooldown = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _timeProvider = timeProvider;

    private readonly ConcurrentDictionary<Guid, ClientLogTailCapture> _logTails = new();

    private readonly ConcurrentDictionary<Guid, ClientScreenshotCapture> _screenshots = new();

    private readonly RequestGuard _logTailGuard = new(LogTailCooldown,
        "The log is already being fetched from this kiosk.",
        "The log was fetched a moment ago. Try again in a few seconds.");

    private readonly RequestGuard _screenshotGuard = new(ScreenshotCooldown,
        "The screens are already being captured on this kiosk.",
        "The screens were captured a moment ago. Try again in a few seconds.");

    public bool TryBeginLogTail(Guid clientId, out string? reason)
    {
        return _logTailGuard.TryBegin(clientId, Now(), out reason);
    }

    public void EndLogTail(Guid clientId)
    {
        _logTailGuard.End(clientId);
    }

    public ClientLogTailCapture SaveLogTail(Guid clientId, ClientLogTailDTO logTail, string requestedBy)
    {
        DateTime now = Now();

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
        PruneExpired(Now());

        return _logTails.TryGetValue(clientId, out ClientLogTailCapture? capture) ? capture : null;
    }

    public bool TryBeginScreenshots(Guid clientId, out string? reason)
    {
        return _screenshotGuard.TryBegin(clientId, Now(), out reason);
    }

    public void EndScreenshots(Guid clientId)
    {
        _screenshotGuard.End(clientId);
    }

    public ClientScreenshotCapture SaveScreenshots(Guid clientId, IReadOnlyList<ClientScreenSnapshot> screens, string? clientVersion, string requestedBy)
    {
        DateTime now = Now();

        ClientScreenshotCapture capture = new()
        {
            ClientId = clientId,
            Screens = screens,
            ClientVersion = clientVersion,
            RequestedBy = requestedBy,
            ReceivedAtUtc = now,
            ExpiresAtUtc = now + Retention
        };

        _screenshots[clientId] = capture;

        PruneExpired(now);

        return capture;
    }

    public ClientScreenshotCapture? GetLatestScreenshots(Guid clientId)
    {
        PruneExpired(Now());

        return _screenshots.TryGetValue(clientId, out ClientScreenshotCapture? capture) ? capture : null;
    }

    private DateTime Now()
    {
        return _timeProvider.GetUtcNow().UtcDateTime;
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

        foreach (KeyValuePair<Guid, ClientScreenshotCapture> entry in _screenshots)
        {
            if (entry.Value.ExpiresAtUtc <= now)
            {
                _screenshots.TryRemove(entry);
            }
        }
    }

    /// <summary>One request per client at a time, and none within the cooldown of the last start.</summary>
    private sealed class RequestGuard(TimeSpan cooldown, string busyReason, string cooldownReason)
    {
        private readonly ConcurrentDictionary<Guid, DateTime> _inFlight = new();

        private readonly ConcurrentDictionary<Guid, DateTime> _lastStartedUtc = new();

        public bool TryBegin(Guid clientId, DateTime now, out string? reason)
        {
            lock (_lastStartedUtc)
            {
                if (_inFlight.ContainsKey(clientId))
                {
                    reason = busyReason;
                    return false;
                }

                if (_lastStartedUtc.TryGetValue(clientId, out DateTime lastStarted) && now - lastStarted < cooldown)
                {
                    reason = cooldownReason;
                    return false;
                }

                _inFlight[clientId] = now;
                _lastStartedUtc[clientId] = now;
            }

            reason = null;
            return true;
        }

        public void End(Guid clientId)
        {
            _inFlight.TryRemove(clientId, out _);
        }
    }
}
