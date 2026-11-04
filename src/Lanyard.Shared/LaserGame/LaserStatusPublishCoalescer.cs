using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;

namespace Lanyard.Shared.LaserGame;

/// <summary>
/// Decides when a kiosk sends its laser game status to the server. A busy game changes state on
/// every packet, and sending each one is a full hub round trip, so ordinary changes are merged and
/// only the latest is sent, at most once per <c>minInterval</c>.
///
/// A change of <see cref="GameStatus"/> is never merged. The server records a finished game only
/// when it sees InGame then NotStarted, and automation fires on each status it sees, so every
/// status change is snapshotted the moment it happens and sent in order. Snapshotting there also
/// keeps the final scores and game length, which the kiosk clears straight after the game ends.
///
/// Pure apart from the snapshot callback: the caller owns the clock and the send loop.
/// </summary>
public sealed class LaserStatusPublishCoalescer(Func<LaserGameStatusDTO> snapshot, TimeSpan minInterval)
{
    private readonly Func<LaserGameStatusDTO> _snapshot = snapshot;
    private readonly TimeSpan _minInterval = minInterval;

    private readonly object _lock = new();
    private readonly Queue<LaserGameStatusDTO> _ready = new();

    private GameStatus? _lastStatus;
    private bool _dirty;
    private DateTime _lastSentUtc = DateTime.MinValue;

    private int _changes;
    private int _sent;

    /// <summary>
    /// Call on every state change, with the status as it is now. True when the send loop has new
    /// work, so it only needs waking then.
    /// </summary>
    public bool Notify(GameStatus status)
    {
        lock (_lock)
        {
            _changes++;

            if (status != _lastStatus)
            {
                Enqueue(_snapshot());
                return true;
            }

            bool wasDirty = _dirty;
            _dirty = true;

            return !wasDirty;
        }
    }

    /// <summary>Queues the current state to go straight away, e.g. once the hub has connected.</summary>
    public void SendNow()
    {
        lock (_lock)
        {
            Enqueue(_snapshot());
        }
    }

    /// <summary>
    /// The next status to send, or null. When null and a merged change is waiting for its slot,
    /// <paramref name="retryAfter"/> says how long until it can go; otherwise there is nothing to send.
    /// </summary>
    public LaserGameStatusDTO? TakeNext(DateTime nowUtc, out TimeSpan? retryAfter)
    {
        lock (_lock)
        {
            retryAfter = null;

            if (_ready.TryDequeue(out LaserGameStatusDTO? queued))
            {
                return MarkSent(queued, nowUtc);
            }

            if (!_dirty)
            {
                return null;
            }

            TimeSpan wait = _lastSentUtc + _minInterval - nowUtc;

            if (wait > TimeSpan.Zero)
            {
                retryAfter = wait;
                return null;
            }

            // Taken now rather than when the change happened, so whatever came since is included.
            LaserGameStatusDTO latest = _snapshot();
            _lastStatus = latest.Status;
            _dirty = false;

            return MarkSent(latest, nowUtc);
        }
    }

    /// <summary>State changes seen and statuses handed out since the last call, for a per-game log line.</summary>
    public (int Changes, int Sent) TakeCounters()
    {
        lock (_lock)
        {
            (int, int) counters = (_changes, _sent);
            _changes = 0;
            _sent = 0;

            return counters;
        }
    }

    private void Enqueue(LaserGameStatusDTO status)
    {
        _ready.Enqueue(status);
        _lastStatus = status.Status;

        // The snapshot already holds every change so far.
        _dirty = false;
    }

    private LaserGameStatusDTO MarkSent(LaserGameStatusDTO status, DateTime nowUtc)
    {
        _lastSentUtc = nowUtc;
        _sent++;

        return status;
    }
}
