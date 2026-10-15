namespace Lanyard.Shared.Contracts;

/// <summary>
/// How long a kiosk waits between attempts to make its first connection to the server. Lives in
/// Shared (not the WPF client) so it can be unit tested.
/// </summary>
public static class ConnectRetryDelay
{
    public static readonly TimeSpan Initial = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A rejected shared secret can't fix itself, but an admin can fix the server's
    /// Clients:SharedSecret. Retry slowly so the kiosk recovers without a reboot and
    /// without flooding the server's log.
    /// </summary>
    public static readonly TimeSpan Unauthorized = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The delay before retry number <paramref name="failedAttempts"/> (1 = after the first failure).
    /// Doubles from <see cref="Initial"/> up to <see cref="Max"/>.
    /// </summary>
    public static TimeSpan For(int failedAttempts, bool unauthorized)
    {
        if (unauthorized)
        {
            return Unauthorized;
        }

        if (failedAttempts <= 1)
        {
            return Initial;
        }

        // Capped exponent keeps the shift from overflowing on a kiosk that's been offline for days.
        int exponent = Math.Min(failedAttempts - 1, 10);
        double seconds = Initial.TotalSeconds * (1 << exponent);

        return seconds >= Max.TotalSeconds ? Max : TimeSpan.FromSeconds(seconds);
    }
}
