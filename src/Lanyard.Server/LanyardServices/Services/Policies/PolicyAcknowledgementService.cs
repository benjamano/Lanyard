using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Policies;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Policies;

public class PolicyAcknowledgementService(
    IDbContextFactory<ApplicationDbContext> factory,
    ITenantContext tenant,
    TimeProvider timeProvider,
    ILogger<PolicyAcknowledgementService> logger) : IPolicyAcknowledgementService
{
    private const int MaxIpLength = 64;
    private const int MaxUserAgentLength = 512;
    private const int MaxDeviceLabelLength = 100;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ITenantContext _tenant = tenant;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<PolicyAcknowledgementService> _logger = logger;

    public async Task<Result<List<MyPolicyItem>>> GetMyPoliciesAsync(string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (RefuseCaller<List<MyPolicyItem>>(userId, out int companyId) is { } refused)
            {
                return refused;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            List<MyPolicyItem> items = await LoadMyPoliciesAsync(ctx, companyId, userId, null, cancellationToken);

            return Result<List<MyPolicyItem>>.Ok(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load policies for {UserId}", userId);
            return Result<List<MyPolicyItem>>.Fail($"Failed to load your policies: {ex.Message}");
        }
    }

    public async Task<Result<MyPolicyItem>> GetMyPolicyAsync(string userId, Guid policyId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (RefuseCaller<MyPolicyItem>(userId, out int companyId) is { } refused)
            {
                return refused;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            List<MyPolicyItem> items = await LoadMyPoliciesAsync(ctx, companyId, userId, policyId, cancellationToken);

            return items.Count == 1
                ? Result<MyPolicyItem>.Ok(items[0])
                : Result<MyPolicyItem>.Fail("This policy isn't one you've been asked to read.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load policy {PolicyId} for {UserId}", policyId, userId);
            return Result<MyPolicyItem>.Fail($"Failed to load the policy: {ex.Message}");
        }
    }

    public async Task<Result<PolicyAcknowledgementResult>> AcknowledgeAsync(Guid versionId, string userId, PolicyAcknowledgementEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (RefuseCaller<PolicyAcknowledgementResult>(userId, out int companyId) is { } refused)
            {
                return refused;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            PolicyVersion? version = await ctx.PolicyVersions
                .Include(x => x.Policy)
                .FirstOrDefaultAsync(x => x.Id == versionId && x.CompanyId == companyId, cancellationToken);

            if (version?.Policy is not Policy policy || !policy.IsActive)
            {
                return Result<PolicyAcknowledgementResult>.Fail("This policy isn't one you've been asked to read.");
            }

            PolicyAssignment? assignment = await ctx.PolicyAssignments
                .FirstOrDefaultAsync(x => x.PolicyId == policy.Id && x.UserId == userId && x.IsActive, cancellationToken);

            if (assignment is null)
            {
                return Result<PolicyAcknowledgementResult>.Fail("This policy isn't one you've been asked to read.");
            }

            if (version.VersionNumber != policy.CurrentVersionNumber)
            {
                return Result<PolicyAcknowledgementResult>.Fail("A newer version of this policy has been published. Please read that one.");
            }

            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

            // A double tap, or opening the page again after acknowledging: hand back what's there.
            if (PolicyRequirementCalculator.StatusAt(assignment, now) == PolicyStatus.UpToDate
                && assignment.AcknowledgedVersionNumber >= version.VersionNumber
                && assignment.LastAcknowledgementId is Guid existingId
                && assignment.AcknowledgedAtUtc is DateTime existingAt)
            {
                return Result<PolicyAcknowledgementResult>.Ok(new PolicyAcknowledgementResult(existingId, assignment.AcknowledgedVersionNumber.Value, existingAt, AlreadyAcknowledged: true));
            }

            PolicyAcknowledgement acknowledgement = new()
            {
                Id = Guid.NewGuid(),
                CompanyId = policy.CompanyId,
                PolicyId = policy.Id,
                PolicyVersionId = version.Id,
                UserId = userId,
                AcknowledgedAtUtc = now,
                StatementText = PolicyRequirementCalculator.StatementFor(policy.Name, version.VersionNumber),
                FileSha256 = version.FileSha256,
                IpAddress = Truncate(evidence.IpAddress, MaxIpLength),
                UserAgent = Truncate(evidence.UserAgent, MaxUserAgentLength),
                DeviceLabel = Truncate(evidence.DeviceLabel, MaxDeviceLabelLength)
            };

            ctx.PolicyAcknowledgements.Add(acknowledgement);

            assignment.AcknowledgedVersionNumber = version.VersionNumber;
            assignment.AcknowledgedAtUtc = now;
            assignment.LastAcknowledgementId = acknowledgement.Id;

            Dictionary<Guid, DateTime> requiredPublished = await PolicyAudienceService.RequiredVersionPublishedDatesAsync(ctx, [policy], cancellationToken);
            PolicyRequirementCalculator.Apply(policy, assignment, requiredPublished.TryGetValue(policy.Id, out DateTime published) ? published : null);

            await ctx.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("{UserId} acknowledged policy {PolicyId} version {VersionNumber} from {DeviceLabel}",
                userId, policy.Id, version.VersionNumber, acknowledgement.DeviceLabel);

            return Result<PolicyAcknowledgementResult>.Ok(new PolicyAcknowledgementResult(acknowledgement.Id, version.VersionNumber, now, AlreadyAcknowledged: false));
        }
        catch (DbUpdateConcurrencyException)
        {
            // The reminder sweep claimed this assignment in the same instant; nothing was saved.
            return Result<PolicyAcknowledgementResult>.Fail("That didn't go through. Please try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record {UserId} acknowledging policy version {VersionId}", userId, versionId);
            return Result<PolicyAcknowledgementResult>.Fail($"Failed to record your acknowledgement: {ex.Message}");
        }
    }

    public async Task<Result<List<MyPolicyItem>>> GetPoliciesForPersonAsync(string targetUserId, string requestingUserId, LocationScope scope,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (RefuseCaller<List<MyPolicyItem>>(requestingUserId, out int companyId) is { } refused)
            {
                return refused;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            if (!await CanSeePersonAsync(ctx, targetUserId, requestingUserId, scope, cancellationToken))
            {
                return Result<List<MyPolicyItem>>.Fail("You can't see this person's policies.");
            }

            return Result<List<MyPolicyItem>>.Ok(await LoadMyPoliciesAsync(ctx, companyId, targetUserId, null, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load policies for {UserId}", targetUserId);
            return Result<List<MyPolicyItem>>.Fail($"Failed to load policies: {ex.Message}");
        }
    }

    public async Task<Result<List<PolicyAcknowledgementRecord>>> GetHistoryAsync(string targetUserId, string requestingUserId, LocationScope scope,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (RefuseCaller<List<PolicyAcknowledgementRecord>>(requestingUserId, out int companyId) is { } refused)
            {
                return refused;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            if (!await CanSeePersonAsync(ctx, targetUserId, requestingUserId, scope, cancellationToken))
            {
                return Result<List<PolicyAcknowledgementRecord>>.Fail("You can't see this person's policy history.");
            }

            bool showEvidence = scope.IsAdmin;

            List<PolicyAcknowledgementRecord> records = await ctx.PolicyAcknowledgements
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.UserId == targetUserId && x.CompanyId == companyId)
                .OrderByDescending(x => x.AcknowledgedAtUtc)
                .Select(x => new PolicyAcknowledgementRecord(
                    x.Id,
                    x.PolicyId,
                    x.Policy!.Name,
                    x.PolicyVersion!.VersionNumber,
                    x.AcknowledgedAtUtc,
                    x.StatementText,
                    showEvidence ? x.IpAddress : null,
                    showEvidence ? x.DeviceLabel : null))
                .ToListAsync(cancellationToken);

            return Result<List<PolicyAcknowledgementRecord>>.Ok(records);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load policy history for {UserId}", targetUserId);
            return Result<List<PolicyAcknowledgementRecord>>.Fail($"Failed to load policy history: {ex.Message}");
        }
    }

    private async Task<List<MyPolicyItem>> LoadMyPoliciesAsync(ApplicationDbContext ctx, int companyId, string userId, Guid? policyId,
        CancellationToken cancellationToken)
    {
        List<PolicyAssignment> assignments = await ctx.PolicyAssignments
            .AsNoTracking()
            .TagWithCallSite()
            .Include(x => x.Policy)
            .Where(x => x.UserId == userId && x.IsActive && x.CompanyId == companyId
                && x.Policy!.IsActive && x.Policy.CurrentVersionNumber > 0)
            .Where(x => policyId == null || x.PolicyId == policyId)
            .ToListAsync(cancellationToken);

        List<Guid> policyIds = [.. assignments.Select(x => x.PolicyId)];

        List<PolicyVersion> versions = await ctx.PolicyVersions
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => policyIds.Contains(x.PolicyId))
            .ToListAsync(cancellationToken);

        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        return [.. assignments
            .Select(x =>
            {
                Policy policy = x.Policy!;
                PolicyVersion? current = versions.FirstOrDefault(v => v.PolicyId == policy.Id && v.VersionNumber == policy.CurrentVersionNumber);

                return new MyPolicyItem(
                    policy.Id,
                    policy.Name,
                    policy.Description,
                    PolicyRequirementCalculator.StatusAt(x, now),
                    current?.Id,
                    policy.CurrentVersionNumber,
                    current?.ChangeSummary,
                    current?.PublishedDate,
                    x.AcknowledgedVersionNumber,
                    x.AcknowledgedAtUtc,
                    x.OutstandingFrom,
                    x.DueDate);
            })
            .OrderByDescending(x => x.Status)
            .ThenBy(x => x.Status == PolicyStatus.UpToDate ? DateTime.MaxValue : x.DueDate ?? DateTime.MaxValue)
            .ThenBy(x => x.Name)];
    }

    // Yourself, anyone in your company if you're an Admin, or anyone at your location if you're a Manager.
    private static async Task<bool> CanSeePersonAsync(ApplicationDbContext ctx, string targetUserId, string requestingUserId, LocationScope scope,
        CancellationToken cancellationToken)
    {
        if (targetUserId == requestingUserId || scope.IsAdmin)
        {
            return true;
        }

        return scope.IsManager && scope.LocationId is int locationId
            && await ctx.UserLocationMemberships.AnyAsync(x => x.UserId == targetUserId && x.LocationId == locationId, cancellationToken);
    }

    private Result<T>? RefuseCaller<T>(string userId, out int companyId)
    {
        companyId = 0;

        if (_tenant.IsSystem || string.IsNullOrEmpty(userId))
        {
            return Result<T>.Fail("Policies are only available to signed-in staff.");
        }

        if (_tenant.CompanyId is not int tenantCompanyId)
        {
            return Result<T>.Fail("Your company couldn't be worked out. Please sign in again.");
        }

        companyId = tenantCompanyId;
        return null;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
