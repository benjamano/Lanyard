using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Policies;

// What happens to someone's policy acknowledgements when their account is deleted. They're
// evidence (who agreed to which version, when) and are kept for employment + 6 years
// (docs/DATA_RETENTION.md), so they stay, re-pointed to the placeholder account - which is what
// lets PolicyAcknowledgement.UserId keep a Restrict FK. A GDPR erasure also clears the IP address,
// browser and device, leaving only "this version was acknowledged at this time". Their name comes
// off policies they created and versions they published. Their PolicyAssignment rows are working
// state and go with the account (cascading key; GdprService removes them explicitly too).
// Run by ScheduleRetention.DetachUserAsync; the snapshot lets a failed account delete be undone.
public static class PolicyRetention
{
    public record AcknowledgementFields(Guid Id, string UserId, string? IpAddress, string? UserAgent, string? DeviceLabel);

    public record Snapshot(List<AcknowledgementFields> Acknowledgements, List<Guid> CreatedPolicyIds, List<Guid> PublishedVersionIds, string UserId)
    {
        public static readonly Snapshot Empty = new([], [], [], string.Empty);

        public int Count => Acknowledgements.Count + CreatedPolicyIds.Count + PublishedVersionIds.Count;
    }

    private const string Placeholder = ApplicationDbContext.SystemDeletedUserPlaceholderId;

    public static async Task<Snapshot> DetachUserAsync(ApplicationDbContext ctx, string userId, bool gdprErasure)
    {
        List<PolicyAcknowledgement> acknowledgements = await ctx.PolicyAcknowledgements
            .Where(x => x.UserId == userId)
            .ToListAsync();

        List<Policy> createdPolicies = await ctx.Policies
            .Where(x => x.CreatedByUserId == userId)
            .ToListAsync();

        List<PolicyVersion> publishedVersions = await ctx.PolicyVersions
            .Where(x => x.PublishedByUserId == userId)
            .ToListAsync();

        Snapshot snapshot = new(
            [.. acknowledgements.Select(x => new AcknowledgementFields(x.Id, x.UserId, x.IpAddress, x.UserAgent, x.DeviceLabel))],
            [.. createdPolicies.Select(x => x.Id)],
            [.. publishedVersions.Select(x => x.Id)],
            userId);

        foreach (PolicyAcknowledgement acknowledgement in acknowledgements)
        {
            acknowledgement.UserId = Placeholder;

            if (gdprErasure)
            {
                acknowledgement.IpAddress = null;
                acknowledgement.UserAgent = null;
                acknowledgement.DeviceLabel = null;
            }
        }

        foreach (Policy policy in createdPolicies)
        {
            policy.CreatedByUserId = null;
        }

        foreach (PolicyVersion version in publishedVersions)
        {
            version.PublishedByUserId = null;
        }

        return snapshot;
    }

    public static async Task RestoreAsync(ApplicationDbContext ctx, Snapshot snapshot)
    {
        if (snapshot.Count == 0)
        {
            return;
        }

        List<Guid> acknowledgementIds = [.. snapshot.Acknowledgements.Select(x => x.Id)];
        Dictionary<Guid, PolicyAcknowledgement> acknowledgements = await ctx.PolicyAcknowledgements
            .Where(x => acknowledgementIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);

        foreach (AcknowledgementFields original in snapshot.Acknowledgements)
        {
            if (acknowledgements.TryGetValue(original.Id, out PolicyAcknowledgement? acknowledgement))
            {
                acknowledgement.UserId = original.UserId;
                acknowledgement.IpAddress = original.IpAddress;
                acknowledgement.UserAgent = original.UserAgent;
                acknowledgement.DeviceLabel = original.DeviceLabel;
            }
        }

        foreach (Policy policy in await ctx.Policies.Where(x => snapshot.CreatedPolicyIds.Contains(x.Id)).ToListAsync())
        {
            policy.CreatedByUserId = snapshot.UserId;
        }

        foreach (PolicyVersion version in await ctx.PolicyVersions.Where(x => snapshot.PublishedVersionIds.Contains(x.Id)).ToListAsync())
        {
            version.PublishedByUserId = snapshot.UserId;
        }
    }
}
