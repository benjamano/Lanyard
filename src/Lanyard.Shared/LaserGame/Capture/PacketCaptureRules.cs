using Lanyard.Shared.Contracts;

namespace Lanyard.Shared.LaserGame.Capture;

/// <summary>The limits on recording and replaying laser games, shared by the kiosk and the server.</summary>
public static class PacketCaptureRules
{
    /// <summary>The first kiosk version that can record and replay.</summary>
    public const string MinimumClientVersion = "1.0.50";

    public const int DefaultMaxMinutes = 30;
    public const int LongestMaxMinutes = 120;

    /// <summary>A whole game is about 1 MB, so this is many games' worth; recording stops here.</summary>
    public const long MaxRecordingBytes = 20L * 1024 * 1024;

    /// <summary>How long "Record next game" keeps going after the game ends, to catch the final scores.</summary>
    public static readonly TimeSpan NextGameGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>The speeds a replay can run at, as multiples of real time.</summary>
    public static IReadOnlyList<double> ReplaySpeeds { get; } = [1, 2, 5, 10];

    public static bool IsAllowedSpeed(double speed) => ReplaySpeeds.Contains(speed);

    public static int ClampMaxMinutes(int requested)
        => requested <= 0 ? DefaultMaxMinutes : Math.Min(requested, LongestMaxMinutes);

    public static bool IsSupportedBy(string? reportedClientVersion)
        => ClientVersionFormat.TryNormalize(reportedClientVersion, out string normalized)
            && ClientVersionFormat.Compare(normalized, MinimumClientVersion) >= 0;
}

/// <summary>Where a kiosk sends a finished recording.</summary>
public static class PacketCaptureRoutes
{
    public const string UploadTemplate = "api/kiosk/{clientId:guid}/packet-captures";

    public static string Upload(Guid clientId) => $"api/kiosk/{clientId}/packet-captures";

    /// <summary>
    /// The form field the recording file travels in. Who asked for the recording isn't sent: the
    /// server remembers that itself, rather than trusting the kiosk to say.
    /// </summary>
    public const string FileField = "file";
}
