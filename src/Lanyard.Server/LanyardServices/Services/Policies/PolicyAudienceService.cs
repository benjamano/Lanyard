using Lanyard.Application.Services.Features;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Policies;

// Who has to read a policy is decided live from location memberships and positions, not stored
// when the policy is made: location membership changes in several places (user editor, company
// admin, onboarding) and none of them need to know policies exist. This reconciles the stored
// PolicyAssignment rows with that live audience; it runs on publish, on settings changes, when a
// user is created, and from PolicyReminderHostedService for everything else.
public class PolicyAudienceService(
    IDbContextFactory<ApplicationDbContext> factory,
    TimeProvider timeProvider,
    ILogger<PolicyAudienceService> logger) : IPolicyAudienceService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<PolicyAudienceService> _logger = logger;

    public async Task<Result<PolicySyncResult>> SyncAsync(int companyId, Guid? policyId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            if (!await CompanyFeatureQueries.IsEnabledForCompanyAsync(ctx, companyId, CompanyFeature.Policies, cancellationToken))
            {
                return Result<PolicySyncResult>.Ok(PolicySyncResult.Empty);
            }

            List<Policy> policies = await ctx.Policies
                .Include(x => x.Positions)
                .Where(x => x.CompanyId == companyId && x.IsActive && x.CurrentVersionNumber > 0)
                .Where(x => policyId == null || x.Id == policyId)
                .ToListAsync(cancellationToken);

            if (policies.Count == 0)
            {
                return Result<PolicySyncResult>.Ok(PolicySyncResult.Empty);
            }

            List<Guid> policyIds = [.. policies.Select(x => x.Id)];

            Dictionary<Guid, DateTime> requiredPublished = await RequiredVersionPublishedDatesAsync(ctx, policies, cancellationToken);

            // Locations and memberships aren't tenant-filtered, so the company is pinned here.
            List<int> locationIds = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId && x.IsActive)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            var memberships = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => locationIds.Contains(x.LocationId) && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
                .Select(x => new { x.UserId, x.LocationId })
                .ToListAsync(cancellationToken);

            ILookup<string, Guid> positionsByUser = Enumerable.Empty<(string, Guid)>().ToLookup(x => x.Item1, x => x.Item2);

            if (policies.Any(x => x.Positions.Count > 0))
            {
                var userPositions = await ctx.UserPositions
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(x => x.StaffPosition!.CompanyId == companyId)
                    .Select(x => new { x.UserId, x.StaffPositionId })
                    .ToListAsync(cancellationToken);

                positionsByUser = userPositions.ToLookup(x => x.UserId, x => x.StaffPositionId);
            }

            List<PolicyAssignment> existing = await ctx.PolicyAssignments
                .Where(x => policyIds.Contains(x.PolicyId))
                .ToListAsync(cancellationToken);

            ILookup<Guid, PolicyAssignment> existingByPolicy = existing.ToLookup(x => x.PolicyId);

            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
            int added = 0, reactivated = 0, deactivated = 0, updated = 0;

            foreach (Policy policy in policies)
            {
                HashSet<Guid> positionIds = [.. policy.Positions.Select(x => x.StaffPositionId)];

                HashSet<string> audience = [.. memberships
                    .Where(x => policy.LocationId is null || x.LocationId == policy.LocationId)
                    .Select(x => x.UserId)
                    .Where(userId => positionIds.Count == 0 || positionsByUser[userId].Any(positionIds.Contains))];

                DateTime? published = requiredPublished.TryGetValue(policy.Id, out DateTime date) ? date : null;
                Dictionary<string, PolicyAssignment> current = existingByPolicy[policy.Id].ToDictionary(x => x.UserId);

                foreach (PolicyAssignment assignment in current.Values)
                {
                    bool inAudience = audience.Contains(assignment.UserId);

                    if (!inAudience)
                    {
                        if (assignment.IsActive)
                        {
                            assignment.IsActive = false;
                            deactivated++;
                        }

                        continue;
                    }

                    if (!assignment.IsActive)
                    {
                        // Back in the audience (moved back, given the position again): their
                        // clock restarts from today, but anything they'd already acknowledged counts.
                        assignment.IsActive = true;
                        assignment.AssignedDate = now;
                        reactivated++;
                    }

                    if (PolicyRequirementCalculator.Apply(policy, assignment, published))
                    {
                        updated++;
                    }
                }

                foreach (string userId in audience.Where(x => !current.ContainsKey(x)))
                {
                    PolicyAssignment assignment = new()
                    {
                        Id = Guid.NewGuid(),
                        // Explicit: a system (sweep) context would otherwise stamp the default company.
                        CompanyId = policy.CompanyId,
                        PolicyId = policy.Id,
                        UserId = userId,
                        AssignedDate = now,
                        IsActive = true
                    };

                    PolicyRequirementCalculator.Apply(policy, assignment, published);
                    ctx.PolicyAssignments.Add(assignment);
                    added++;
                }
            }

            await ctx.SaveChangesAsync(cancellationToken);

            if (added + reactivated + deactivated > 0)
            {
                _logger.LogInformation(
                    "Policy audience sync for company {CompanyId}: {Added} added, {Reactivated} back, {Deactivated} left, {Updated} re-dated",
                    companyId, added, reactivated, deactivated, updated);
            }

            return Result<PolicySyncResult>.Ok(new PolicySyncResult(added, reactivated, deactivated, updated));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Policy audience sync failed for company {CompanyId}, policy {PolicyId}", companyId, policyId);
            return Result<PolicySyncResult>.Fail($"Failed to update who needs to read the policy: {ex.Message}");
        }
    }

    // When each policy's required version came out: the start of everyone's clock for it.
    internal static async Task<Dictionary<Guid, DateTime>> RequiredVersionPublishedDatesAsync(
        ApplicationDbContext ctx, IReadOnlyCollection<Policy> policies, CancellationToken cancellationToken)
    {
        List<Guid> policyIds = [.. policies.Select(x => x.Id)];

        var versions = await ctx.PolicyVersions
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => policyIds.Contains(x.PolicyId))
            .Select(x => new { x.PolicyId, x.VersionNumber, x.PublishedDate })
            .ToListAsync(cancellationToken);

        Dictionary<Guid, DateTime> result = [];

        foreach (Policy policy in policies)
        {
            var required = versions.FirstOrDefault(x => x.PolicyId == policy.Id && x.VersionNumber == policy.RequiredVersionNumber);

            if (required is not null)
            {
                result[policy.Id] = required.PublishedDate;
            }
        }

        return result;
    }
}
