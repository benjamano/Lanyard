namespace Lanyard.Shared.Kiosk;

public enum KioskConnectivityState
{
    /// <summary>Connected; screens show the server's pages.</summary>
    Online,

    /// <summary>Disconnected, but not for long enough to switch over yet.</summary>
    Grace,

    /// <summary>Screens are playing the cached program.</summary>
    Offline,

    /// <summary>Reconnected while offline; waiting for the connection to stay up before handing back.</summary>
    Recovering,
}

public enum KioskConnectivityTransition
{
    None,
    EnteredOffline,
    ReturnedOnline,
}

/// <summary>
/// Decides when a kiosk's screens switch to their cached program and when they hand back to the
/// server. Pure and clock-driven (the caller passes the time in), so it can be unit tested. The
/// kiosk is treated as disconnected from the moment it starts, so a kiosk that boots while the
/// server is down goes offline after the same delay.
/// </summary>
public class KioskConnectivityStateMachine(DateTime startedUtc)
{
    /// <summary>
    /// How long a connection must stay up before offline screens hand back to the server, so a
    /// connection that flaps doesn't make the screens jump back and forth.
    /// </summary>
    public static readonly TimeSpan RecoveryStability = TimeSpan.FromSeconds(10);

    private DateTime _since = startedUtc;
    private bool _connected;

    public KioskConnectivityState State { get; private set; } = KioskConnectivityState.Grace;

    public bool Enabled { get; private set; } = true;
    public TimeSpan OfflineAfter { get; private set; } = TimeSpan.FromSeconds(Contracts.KioskOfflineLimits.DefaultOfflineAfterSeconds);

    /// <summary>
    /// Applies new settings. Turning the fallback off while offline hands straight back, so the
    /// kiosk doesn't keep playing its cache against the admin's wishes.
    /// </summary>
    public KioskConnectivityTransition Configure(bool enabled, TimeSpan offlineAfter, DateTime nowUtc)
    {
        Enabled = enabled;
        OfflineAfter = offlineAfter;

        if (!enabled && State is KioskConnectivityState.Offline or KioskConnectivityState.Recovering)
        {
            State = _connected ? KioskConnectivityState.Online : KioskConnectivityState.Grace;
            _since = nowUtc;
            return KioskConnectivityTransition.ReturnedOnline;
        }

        return KioskConnectivityTransition.None;
    }

    /// <summary>
    /// Records a connection change. Switching over only ever happens in <see cref="Tick"/>, once
    /// the relevant delay has passed.
    /// </summary>
    public void SetConnected(bool connected, DateTime nowUtc)
    {
        _connected = connected;

        switch (State)
        {
            case KioskConnectivityState.Online when !connected:
                State = KioskConnectivityState.Grace;
                _since = nowUtc;
                break;

            case KioskConnectivityState.Grace when connected:
                State = KioskConnectivityState.Online;
                break;

            case KioskConnectivityState.Offline when connected:
                State = KioskConnectivityState.Recovering;
                _since = nowUtc;
                break;

            case KioskConnectivityState.Recovering when !connected:
                // Dropped again before it was stable: keep playing offline, no new grace period.
                State = KioskConnectivityState.Offline;
                break;
        }
    }

    /// <summary>
    /// Call regularly (about once a second). <paramref name="canPlayOffline"/> is false when the
    /// kiosk has nothing cached to play, in which case it stays in Grace and keeps waiting.
    /// </summary>
    public KioskConnectivityTransition Tick(DateTime nowUtc, bool canPlayOffline)
    {
        if (State == KioskConnectivityState.Grace && Enabled && canPlayOffline && nowUtc - _since >= OfflineAfter)
        {
            State = KioskConnectivityState.Offline;
            return KioskConnectivityTransition.EnteredOffline;
        }

        if (State == KioskConnectivityState.Recovering && nowUtc - _since >= RecoveryStability)
        {
            State = KioskConnectivityState.Online;
            return KioskConnectivityTransition.ReturnedOnline;
        }

        return KioskConnectivityTransition.None;
    }
}
