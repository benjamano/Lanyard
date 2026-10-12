using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.Enum;

namespace Lanyard.Application.Services.ClientReleases;

public sealed record ClientTargetResolution(string? TargetVersion, string? Problem);

// Turns a kiosk's ring (and pin) into one concrete version. Pure, so every rule is unit-tested
// without a database. A withdrawn release is never a target; that is what makes withdrawing a bad
// release move canaries (or everyone, for a bad stable) back to the release before it.
public static class ClientTargetVersionResolver
{
    public static ClientTargetResolution Resolve(ClientUpdateRing ring, string? pinnedVersion, IEnumerable<ClientRelease> releases)
    {
        List<ClientRelease> eligible = releases
            .Where(x => x.WithdrawnAt == null)
            .ToList();

        switch (ring)
        {
            case ClientUpdateRing.Pinned:
                if (string.IsNullOrWhiteSpace(pinnedVersion))
                {
                    return new ClientTargetResolution(null, "Pinned, but no version is chosen.");
                }

                ClientRelease? pinned = releases.FirstOrDefault(x => x.Version == pinnedVersion);

                if (pinned is null)
                {
                    return new ClientTargetResolution(null, $"Pinned to v{pinnedVersion}, which isn't a known release.");
                }

                if (pinned.WithdrawnAt != null)
                {
                    return new ClientTargetResolution(null, $"Pinned to v{pinnedVersion}, which has been withdrawn. Choose another version.");
                }

                return new ClientTargetResolution(pinned.Version, null);

            case ClientUpdateRing.Canary:
                return Newest(eligible, "There are no releases to update to.");

            default:
                return Newest(
                    eligible.Where(IsStable),
                    "There's no stable release yet. Promote one on the Kiosk Releases page.");
        }
    }

    // A normal GitHub release is stable as soon as it's published (every release before this
    // feature existed was one); a prerelease becomes stable only when promoted.
    public static bool IsStable(ClientRelease release) => !release.IsPrerelease || release.PromotedToStableAt != null;

    private static ClientTargetResolution Newest(IEnumerable<ClientRelease> candidates, string noneProblem)
    {
        ClientRelease? newest = null;

        foreach (ClientRelease candidate in candidates)
        {
            if (newest is null || ClientVersionFormat.Compare(candidate.Version, newest.Version) > 0)
            {
                newest = candidate;
            }
        }

        return newest is null
            ? new ClientTargetResolution(null, noneProblem)
            : new ClientTargetResolution(newest.Version, null);
    }
}
