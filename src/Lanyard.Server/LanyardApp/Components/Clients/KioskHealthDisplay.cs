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

    public static string Bytes(long? bytes)
    {
        return bytes is long value ? $"{value / (1024d * 1024 * 1024):0.#} GB" : "unknown";
    }
}
