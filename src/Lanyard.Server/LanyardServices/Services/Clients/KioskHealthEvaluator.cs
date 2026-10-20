using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Clients;

public enum KioskHealthStatus
{
    /// <summary>Never sent a heartbeat: the kiosk runs a client older than heartbeats.</summary>
    NoTelemetry,
    Healthy,
    Degraded,
    /// <summary>Heartbeats stopped recently without a clean disconnect (hung, crashed, or network down).</summary>
    Unresponsive,
    /// <summary>Unresponsive for long enough that it is treated as down.</summary>
    Offline,
    /// <summary>The kiosk disconnected cleanly after its last heartbeat (shut down or closed).</summary>
    Disconnected
}

public class KioskHealthView
{
    public KioskHealthStatus Status { get; init; }

    /// <summary>Why the kiosk is degraded. Empty unless Status is Degraded.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>Set when another kiosk in the company runs a newer client. Information only.</summary>
    public string? NewerVersionSeen { get; init; }

    public ClientHealthSnapshot? Snapshot { get; init; }

    public TimeSpan? SinceLastHeartbeat { get; init; }
}

/// <summary>
/// Works out a kiosk's health from its latest heartbeat. Kept pure (time and thresholds passed
/// in) so the boundaries can be unit tested; KioskHealthService feeds it.
/// </summary>
public static class KioskHealthEvaluator
{
    private const double BytesPerGb = 1024d * 1024 * 1024;

    public static KioskHealthView Evaluate(
        ClientHealthSnapshot? snapshot,
        bool isConnected,
        DateTime? lastDisconnectUtc,
        DateTime nowUtc,
        KioskHealthOptions options,
        string? newestCompanyVersion)
    {
        if (snapshot == null)
        {
            return new KioskHealthView { Status = KioskHealthStatus.NoTelemetry };
        }

        TimeSpan silence = nowUtc - snapshot.LastHeartbeatUtc;

        if (silence < TimeSpan.Zero)
        {
            silence = TimeSpan.Zero;
        }

        string? newerVersion = IsOlder(snapshot.ClientVersion, newestCompanyVersion) ? newestCompanyVersion : null;

        KioskHealthStatus status;
        List<string> reasons = [];

        if (!isConnected && lastDisconnectUtc is DateTime disconnectedAt && disconnectedAt > snapshot.LastHeartbeatUtc)
        {
            status = KioskHealthStatus.Disconnected;
        }
        else if (silence >= options.OfflineAfter)
        {
            status = KioskHealthStatus.Offline;
        }
        else if (silence >= options.UnresponsiveAfter)
        {
            status = KioskHealthStatus.Unresponsive;
        }
        else
        {
            reasons = DegradedReasons(snapshot, options);
            status = reasons.Count > 0 ? KioskHealthStatus.Degraded : KioskHealthStatus.Healthy;
        }

        return new KioskHealthView
        {
            Status = status,
            Reasons = reasons,
            NewerVersionSeen = newerVersion,
            Snapshot = snapshot,
            SinceLastHeartbeat = silence
        };
    }

    public static List<string> DegradedReasons(ClientHealthSnapshot snapshot, KioskHealthOptions options)
    {
        List<string> reasons = [];

        if (snapshot.SystemDriveFreeBytes is long free)
        {
            double freeGb = free / BytesPerGb;
            double? freePercent = snapshot.SystemDriveTotalBytes is long total && total > 0
                ? free * 100d / total
                : null;

            if (freeGb < options.MinFreeDiskGb || freePercent < options.MinFreeDiskPercent)
            {
                reasons.Add($"Low disk space ({freeGb:0.#} GB free)");
            }
        }

        if (snapshot.DmxConfigured && !snapshot.DmxOpen)
        {
            reasons.Add("DMX device not open");
        }

        if (snapshot.PreferredAudioDevicePresent == false)
        {
            reasons.Add("Preferred audio device missing");
        }

        if (snapshot.PacketCaptureConfigured && !snapshot.PacketCaptureRunning)
        {
            reasons.Add("Zone scoreboard capture stopped");
        }

        if (snapshot.OfflineFallbackReady == false)
        {
            reasons.Add(snapshot.OfflineVideosNotCached is int missing and > 0
                ? $"Offline fallback not ready ({missing} {(missing == 1 ? "video doesn't" : "videos don't")} fit the cache)"
                : "Offline fallback not ready");
        }

        if (snapshot.CpuPercent >= options.HighCpuPercent)
        {
            reasons.Add($"High CPU ({snapshot.CpuPercent:0}%)");
        }

        if (snapshot.MemoryPercent >= options.HighMemoryPercent)
        {
            reasons.Add($"High memory ({snapshot.MemoryPercent:0}%)");
        }

        return reasons;
    }

    /// <summary>The highest parseable version among the given ones, as reported.</summary>
    public static string? Newest(IEnumerable<string?> versions)
    {
        return versions
            .Select(v => (Text: v, Parsed: Version.TryParse(v, out Version? parsed) ? parsed : null))
            .Where(x => x.Parsed != null)
            .OrderByDescending(x => x.Parsed)
            .Select(x => x.Text)
            .FirstOrDefault();
    }

    private static bool IsOlder(string? version, string? than)
    {
        return Version.TryParse(version, out Version? mine)
            && Version.TryParse(than, out Version? newest)
            && mine < newest;
    }
}
