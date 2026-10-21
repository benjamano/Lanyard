using Lanyard.Application.Services.Clients;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Shared.Enum;

namespace Lanyard.App.Components.Clients;

/// <summary>
/// Everything the Clients page shows about one kiosk, as a value. The page compares the rows from
/// a refresh with the last ones and skips the render when nothing visible changed, so a page left
/// open sends nothing to the browser while the fleet is quiet.
/// </summary>
public sealed record ClientRowSignature(
    Guid Id,
    string? Name,
    string? IpAddress,
    string? ReportedVersion,
    DateTime? VersionReportedAt,
    DateTime? LastLogin,
    ClientAuthMethod? AuthMethod,
    bool ProjectionEnabled,
    bool DmxEnabled,
    bool IsConnected,
    KioskHealthStatus? HealthStatus,
    string HealthDetails,
    ClientUpdateRing? Ring,
    string? PinnedVersion,
    string? TargetVersion,
    string? TargetProblem);

public static class ClientsPageRows
{
    public static IReadOnlyList<ClientRowSignature> Build(
        IEnumerable<ClientConnectedWithCapabilitiesDTO> clients,
        IReadOnlyDictionary<Guid, KioskHealthView> health,
        IReadOnlyDictionary<Guid, ClientUpdateTargetDTO> targets)
    {
        return clients
            .OrderBy(x => x.Id)
            .Select(x =>
            {
                KioskHealthView? view = health.GetValueOrDefault(x.Id);
                ClientUpdateTargetDTO? target = targets.GetValueOrDefault(x.Id);

                return new ClientRowSignature(
                    x.Id,
                    x.Name,
                    x.MostRecentIpAddress,
                    x.ReportedClientVersion,
                    x.ClientVersionReportedAt,
                    x.LastLogin,
                    x.LastAuthMethod,
                    x.ProjectionEnabled,
                    x.DmxEnabled,
                    x.IsCurrentlyConnected,
                    view?.Status,
                    // The badge's tooltip text, so "Last heartbeat 2 minutes ago" only counts as a
                    // change when the words change, not on every refresh.
                    string.Join("\n", KioskHealthDisplay.Details(view)),
                    target?.Ring,
                    target?.PinnedVersion,
                    target?.TargetVersion,
                    target?.Problem);
            })
            .ToList();
    }

    /// <summary>
    /// What decides a kiosk's update target, so the page only asks for targets again (a release
    /// catalogue read) when one of these moved.
    /// </summary>
    public static IReadOnlyList<(Guid Id, string? ReportedVersion, ClientUpdateRing Ring, string? PinnedVersion)> TargetInputs(
        IEnumerable<ClientConnectedWithCapabilitiesDTO> clients)
    {
        return clients
            .OrderBy(x => x.Id)
            .Select(x => (x.Id, x.ReportedClientVersion, x.UpdateRing, x.PinnedClientVersion))
            .ToList();
    }
}
