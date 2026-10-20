using Humanizer;
using Humanizer.Localisation;
using Lanyard.Application.Services.Clients;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Lanyard.App.Components.Clients;

/// <summary>Shared wording and colours for kiosk health, used by the dashboard widget and the Clients page.</summary>
public static class KioskHealthDisplay
{
    public static string Label(KioskHealthStatus status) => status switch
    {
        KioskHealthStatus.Healthy => "Healthy",
        KioskHealthStatus.Degraded => "Degraded",
        KioskHealthStatus.Unresponsive => "Unresponsive",
        KioskHealthStatus.Offline => "Offline",
        KioskHealthStatus.Disconnected => "Disconnected",
        _ => "No data"
    };

    public static BadgeColor Color(KioskHealthStatus status) => status switch
    {
        KioskHealthStatus.Healthy => BadgeColor.Success,
        KioskHealthStatus.Degraded => BadgeColor.Warning,
        KioskHealthStatus.Unresponsive => BadgeColor.Warning,
        KioskHealthStatus.Offline => BadgeColor.Danger,
        KioskHealthStatus.Disconnected => BadgeColor.Informative,
        _ => BadgeColor.Subtle
    };

    // A filled subtle badge has no visible background; the neutral states read better tinted.
    public static BadgeAppearance Appearance(KioskHealthStatus status) => status switch
    {
        KioskHealthStatus.Disconnected or KioskHealthStatus.NoTelemetry => BadgeAppearance.Tint,
        _ => BadgeAppearance.Filled
    };

    /// <summary>
    /// Whether to count a kiosk as up. Kiosks without heartbeats fall back to whether the hub
    /// has a connection for them, which is all that was known before heartbeats.
    /// </summary>
    public static bool IsUp(KioskHealthView? health, bool isCurrentlyConnected) => health?.Status switch
    {
        KioskHealthStatus.Healthy or KioskHealthStatus.Degraded => true,
        null or KioskHealthStatus.NoTelemetry => isCurrentlyConnected,
        _ => false
    };

    /// <summary>Lower sorts first: problems at the top, healthy kiosks last.</summary>
    public static int SortOrder(KioskHealthView? health, bool isCurrentlyConnected) => health?.Status switch
    {
        KioskHealthStatus.Offline => 0,
        KioskHealthStatus.Unresponsive => 1,
        KioskHealthStatus.Disconnected => 2,
        KioskHealthStatus.Degraded => 3,
        KioskHealthStatus.Healthy => 5,
        _ => isCurrentlyConnected ? 4 : 2
    };

    /// <summary>The reasons a kiosk needs attention, one line each, for a tooltip or a list.</summary>
    public static IEnumerable<string> Details(KioskHealthView? health, bool includeLastHeartbeat = true)
    {
        if (health == null)
        {
            yield break;
        }

        if (health.Status == KioskHealthStatus.NoTelemetry)
        {
            yield return "This kiosk's client is too old to report health. It updates the next time it restarts.";
            yield break;
        }

        if (includeLastHeartbeat
            && health.SinceLastHeartbeat is TimeSpan silence
            && health.Status is KioskHealthStatus.Unresponsive or KioskHealthStatus.Offline or KioskHealthStatus.Disconnected)
        {
            yield return $"Last heartbeat {Ago(silence)}";
        }

        foreach (string reason in health.Reasons)
        {
            yield return reason;
        }

        if (ShouldBePlayingOffline(health))
        {
            yield return "Its screens should be playing their offline copy (it was ready at its last report)";
        }

        if (health.NewerVersionSeen != null)
        {
            yield return $"Runs v{health.Snapshot?.ClientVersion}; another kiosk runs v{health.NewerVersionSeen}";
        }
    }

    public static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return $"{elapsed.Humanize(precision: 1, minUnit: TimeUnit.Second)} ago";
    }

    /// <summary>Whether the kiosk's screens can play their offline copy, as of its last heartbeat.</summary>
    public static string OfflineFallback(Lanyard.Infrastructure.Models.ClientHealthSnapshot snapshot) => snapshot.OfflineFallbackReady switch
    {
        true => $"Ready ({Megabytes(snapshot.OfflineCacheBytes)} of video cached)",
        false when snapshot.OfflineVideosNotCached is int missing and > 0 => $"Not ready: {missing} {(missing == 1 ? "video doesn't" : "videos don't")} fit the cache",
        false => "Not ready",
        null => "Off, or nothing to play offline",
    };

    /// <summary>The kiosk's most recent stretch of playing offline, or null if it hasn't had one.</summary>
    public static string? LastOutage(Lanyard.Infrastructure.Models.ClientHealthSnapshot snapshot, DateTime nowUtc)
    {
        if (snapshot.LastOfflineStartedUtc is not DateTime started)
        {
            return null;
        }

        if (snapshot.LastOfflineEndedUtc is DateTime ended && ended >= started)
        {
            return $"{Ago(nowUtc - started)}, for {(ended - started).Humanize(precision: 1, minUnit: TimeUnit.Minute)}";
        }

        return $"{Ago(nowUtc - started)}, still going at its last report";
    }

    /// <summary>
    /// For a kiosk the server can't hear from: whether its screens should still be showing
    /// something. The heartbeat is old, so it's what the kiosk could do, not a live report.
    /// </summary>
    public static bool ShouldBePlayingOffline(KioskHealthView? health) =>
        health?.Status is KioskHealthStatus.Unresponsive or KioskHealthStatus.Offline
        && health.Snapshot?.OfflineFallbackReady == true;

    private static string Megabytes(long? bytes) => bytes is long value
        ? value >= 1024L * 1024 * 1024 ? $"{value / (1024d * 1024 * 1024):0.#} GB" : $"{value / (1024d * 1024):0} MB"
        : "unknown";

    public static string Bytes(long? bytes)
    {
        return bytes is long value ? $"{value / (1024d * 1024 * 1024):0.#} GB" : "unknown";
    }
}
