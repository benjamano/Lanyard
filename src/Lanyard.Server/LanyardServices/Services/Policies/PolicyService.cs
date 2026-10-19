using System.Security.Cryptography;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Policies;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Policies;

public class PolicyService(
    IDbContextFactory<ApplicationDbContext> factory,
    ITenantContext tenant,
    IFileService fileService,
    IPolicyAudienceService audience,
    IPolicyReminderService reminders,
    TimeProvider timeProvider,
    ILogger<PolicyService> logger) : IPolicyService
{
    public const long MaxFileBytes = 20 * 1024 * 1024;
    public const int MaxNameLength = 200;
    public const int MaxChangeSummaryLength = 1000;

    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ITenantContext _tenant = tenant;
    private readonly IFileService _fileService = fileService;
    private readonly IPolicyAudienceService _audience = audience;
    private readonly IPolicyReminderService _reminders = reminders;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<PolicyService> _logger = logger;

    // A Manager can see their own location's policies and the company-wide ones.
    internal static bool CanView(Policy policy, LocationScope scope) =>
        scope.IsAdmin || (scope.IsManager && (policy.LocationId is null || policy.LocationId == scope.LocationId));

    // ...but only change their own location's. Company-wide policies are an Admin's.
    internal static bool CanManage(Policy policy, LocationScope scope) =>
        scope.IsAdmin || (scope.IsManager && policy.LocationId is not null && policy.LocationId == scope.LocationId);

    public async Task<Result<List<PolicySummary>>> GetPoliciesAsync(LocationScope scope, CancellationToken cancellationToken = default)
    {
        try
        {
            if (RefuseCaller<List<PolicySummary>>(scope, out int companyId) is { } refused)
            {
                return refused;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            List<Policy> policies = await ctx.Policies
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                .Include(x => x.Positions).ThenInclude(x => x.StaffPosition)
                .Where(x => x.CompanyId == companyId && x.IsActive)
                .OrderBy(x => x.Name)
                .ToListAsync(cancellationToken);

            policies = [.. policies.Where(x => CanView(x, scope))];

            List<Guid> policyIds = [.. policies.Select(x => x.Id)];

            var versions = await ctx.PolicyVersions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => policyIds.Contains(x.PolicyId))
                .Select(x => new { x.PolicyId, x.VersionNumber, x.PublishedDate })
                .ToListAsync(cancellationToken);

            var assignments = await ctx.PolicyAssignments
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => policyIds.Contains(x.PolicyId) && x.IsActive)
                .Select(x => new { x.PolicyId, x.UserId, x.OutstandingFrom, x.DueDate })
                .ToListAsync(cancellationToken);

            HashSet<string>? visiblePeople = await VisiblePeopleAsync(ctx, scope, cancellationToken);
            DateTime now = Now;

            List<PolicySummary> summaries = [];

            foreach (Policy policy in policies)
            {
                List<PolicyStatus> statuses = [.. assignments
                    .Where(x => x.PolicyId == policy.Id && (visiblePeople is null || visiblePeople.Contains(x.UserId)))
                    .Select(x => PolicyRequirementCalculator.StatusAt(x.OutstandingFrom, x.DueDate, now))];

                DateTime? publishedDate = versions
                    .FirstOrDefault(x => x.PolicyId == policy.Id && x.VersionNumber == policy.CurrentVersionNumber)?.PublishedDate;

                summaries.Add(new PolicySummary(
                    policy.Id,
                    policy.Name,
                    policy.Description,
                    policy.LocationId,
                    ScopeName(policy),
                    PositionNames(policy),
                    policy.CurrentVersionNumber,
                    publishedDate,
                    statuses.Count,
                    statuses.Count(x => x == PolicyStatus.UpToDate),
                    statuses.Count(x => x == PolicyStatus.Outstanding),
                    statuses.Count(x => x == PolicyStatus.Overdue),
                    CanManage(policy, scope)));
            }

            return Result<List<PolicySummary>>.Ok(summaries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load policies for location {LocationId}", scope.LocationId);
            return Result<List<PolicySummary>>.Fail($"Failed to load policies: {ex.Message}");
        }
    }

    public async Task<Result<PolicyEditorOptions>> GetEditorOptionsAsync(LocationScope scope, CancellationToken cancellationToken = default)
    {
        try
        {
            if (RefuseCaller<PolicyEditorOptions>(scope, out int companyId) is { } refused)
            {
                return refused;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            List<PolicyLocationOption> locations = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId && x.IsActive && (scope.IsAdmin || x.Id == scope.LocationId))
                .OrderBy(x => x.Name)
                .Select(x => new PolicyLocationOption(x.Id, x.Name))
                .ToListAsync(cancellationToken);

            List<PolicyPositionOption> positions = await ctx.StaffPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId && x.IsActive)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                .Select(x => new PolicyPositionOption(x.Id, x.Name))
                .ToListAsync(cancellationToken);

            int? defaultLocationId = scope.LocationId is int signedIn && locations.Any(x => x.Id == signedIn)
                ? signedIn
                : locations.FirstOrDefault()?.Id;

            return Result<PolicyEditorOptions>.Ok(new PolicyEditorOptions(locations, positions, scope.IsAdmin, defaultLocationId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load policy form options");
            return Result<PolicyEditorOptions>.Fail($"Failed to load locations and positions: {ex.Message}");
        }
    }

    public async Task<Result<PolicyDetail>> GetPolicyAsync(Guid policyId, LocationScope scope, CancellationToken cancellationToken = default)
    {
        try
        {
            if (RefuseCaller<PolicyDetail>(scope, out int companyId) is { } refused)
            {
                return refused;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            Policy? policy = await ctx.Policies
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                .Include(x => x.Positions).ThenInclude(x => x.StaffPosition)
                .Include(x => x.Versions)
                .FirstOrDefaultAsync(x => x.Id == policyId && x.CompanyId == companyId && x.IsActive, cancellationToken);

            if (policy is null || !CanView(policy, scope))
            {
                return Result<PolicyDetail>.Fail("Policy not found.");
            }

            List<string> publisherIds = [.. policy.Versions.Where(x => x.PublishedByUserId != null).Select(x => x.PublishedByUserId!).Distinct()];

            Dictionary<string, string> publisherNames = (await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => publisherIds.Contains(x.Id))
                .ToListAsync(cancellationToken))
                .ToDictionary(x => x.Id, RotaNames.For);

            List<PolicyVersionInfo> versions = [.. policy.Versions
                .OrderByDescending(x => x.VersionNumber)
                .Select(x => ToInfo(x, x.PublishedByUserId is not null && publisherNames.TryGetValue(x.PublishedByUserId, out string? name) ? name : null))];

            return Result<PolicyDetail>.Ok(new PolicyDetail(
                policy.Id,
                policy.Name,
                policy.Description,
                policy.LocationId,
                ScopeName(policy),
                [.. policy.Positions.Select(x => x.StaffPositionId)],
                PositionNames(policy),
                policy.AcknowledgeWithinDays,
                policy.ReacknowledgeEveryMonths,
                policy.CurrentVersionNumber,
                policy.RequiredVersionNumber,
                versions,
                CanManage(policy, scope)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load policy {PolicyId}", policyId);
            return Result<PolicyDetail>.Fail($"Failed to load the policy: {ex.Message}");
        }
    }

    public async Task<Result<Guid>> SavePolicyAsync(PolicySaveRequest request, LocationScope scope, string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (RefuseCaller<Guid>(scope, out int companyId) is { } refused)
            {
                return refused;
            }

            string name = request.Name?.Trim() ?? string.Empty;

            if (name.Length == 0)
            {
                return Result<Guid>.Fail("Give the policy a name.");
            }

            if (name.Length > MaxNameLength)
            {
                return Result<Guid>.Fail($"The name can be at most {MaxNameLength} characters.");
            }

            if (request.AcknowledgeWithinDays < Policy.MinAcknowledgeWithinDays || request.AcknowledgeWithinDays > Policy.MaxAcknowledgeWithinDays)
            {
                return Result<Guid>.Fail($"Staff must have between {Policy.MinAcknowledgeWithinDays} and {Policy.MaxAcknowledgeWithinDays} days to acknowledge it.");
            }

            if (request.ReacknowledgeEveryMonths is int months
                && (months < Policy.MinReacknowledgeEveryMonths || months > Policy.MaxReacknowledgeEveryMonths))
            {
                return Result<Guid>.Fail($"Re-reading can be asked for every {Policy.MinReacknowledgeEveryMonths} to {Policy.MaxReacknowledgeEveryMonths} months.");
            }

            if (request.LocationId is null && !scope.IsAdmin)
            {
                return Result<Guid>.Fail("Only an Admin can set a policy for the whole company.");
            }

            if (request.LocationId is int locationId && !scope.IsAdmin && locationId != scope.LocationId)
            {
                return Result<Guid>.Fail("You can only set policies for your own location.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            // Locations aren't tenant-filtered, so an Admin naming one is checked against their company.
            if (request.LocationId is int requestedLocationId
                && !await ctx.Locations.AnyAsync(x => x.Id == requestedLocationId && x.CompanyId == companyId && x.IsActive, cancellationToken))
            {
                return Result<Guid>.Fail("That location isn't available.");
            }

            HashSet<Guid> positionIds = [.. request.PositionIds ?? []];

            if (positionIds.Count > 0)
            {
                int known = await ctx.StaffPositions
                    .CountAsync(x => positionIds.Contains(x.Id) && x.CompanyId == companyId && x.IsActive, cancellationToken);

                if (known != positionIds.Count)
                {
                    return Result<Guid>.Fail("One of the chosen positions isn't available.");
                }
            }

            Policy? policy;

            if (request.Id is Guid id)
            {
                policy = await ctx.Policies
                    .Include(x => x.Positions)
                    .FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId && x.IsActive, cancellationToken);

                if (policy is null || !CanView(policy, scope))
                {
                    return Result<Guid>.Fail("Policy not found.");
                }

                if (!CanManage(policy, scope))
                {
                    return Result<Guid>.Fail("Only an Admin can change a company-wide policy.");
                }
            }
            else
            {
                policy = new Policy
                {
                    Id = Guid.NewGuid(),
                    CompanyId = companyId,
                    Name = name,
                    CreatedDate = Now,
                    CreatedByUserId = userId
                };

                ctx.Policies.Add(policy);
            }

            policy.Name = name;
            policy.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            policy.LocationId = request.LocationId;
            policy.AcknowledgeWithinDays = request.AcknowledgeWithinDays;
            policy.ReacknowledgeEveryMonths = request.ReacknowledgeEveryMonths;

            foreach (PolicyAudiencePosition removed in policy.Positions.Where(x => !positionIds.Contains(x.StaffPositionId)).ToList())
            {
                ctx.PolicyAudiencePositions.Remove(removed);
            }

            foreach (Guid positionId in positionIds.Where(x => policy.Positions.All(p => p.StaffPositionId != x)))
            {
                ctx.PolicyAudiencePositions.Add(new PolicyAudiencePosition { Id = Guid.NewGuid(), PolicyId = policy.Id, StaffPositionId = positionId });
            }

            await ctx.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Policy {PolicyId} ({PolicyName}) saved by {UserId}", policy.Id, policy.Name, userId);

            // The audience or the deadline may have changed; bring everyone's dates in line now
            // rather than waiting for the next sweep.
            await SyncAudienceAsync(companyId, policy.Id, cancellationToken);

            return Result<Guid>.Ok(policy.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save policy {PolicyId}", request.Id);
            return Result<Guid>.Fail($"Failed to save the policy: {ex.Message}");
        }
    }

    public async Task<Result<PolicyVersionInfo>> PublishVersionAsync(Guid policyId, IFormFile file, string? changeSummary, bool requiresReacknowledgement,
        LocationScope scope, string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (RefuseCaller<PolicyVersionInfo>(scope, out int companyId) is { } refused)
            {
                return refused;
            }

            string? summary = string.IsNullOrWhiteSpace(changeSummary) ? null : changeSummary.Trim();

            if (summary?.Length > MaxChangeSummaryLength)
            {
                return Result<PolicyVersionInfo>.Fail($"The summary of changes can be at most {MaxChangeSummaryLength} characters.");
            }

            if (await ValidatePdfAsync(file, cancellationToken) is string fileError)
            {
                return Result<PolicyVersionInfo>.Fail(fileError);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            Policy? policy = await ctx.Policies
                .FirstOrDefaultAsync(x => x.Id == policyId && x.CompanyId == companyId && x.IsActive, cancellationToken);

            if (policy is null || !CanView(policy, scope))
            {
                return Result<PolicyVersionInfo>.Fail("Policy not found.");
            }

            if (!CanManage(policy, scope))
            {
                return Result<PolicyVersionInfo>.Fail("Only an Admin can publish a company-wide policy.");
            }

            string sha256 = await HashAsync(file, cancellationToken);

            Result<FileMetadata> upload = await _fileService.UploadFileAsync(file, null, userId,
                new FileUploadOptions(AddAudioToMusicLibrary: false, IsRestricted: true), cancellationToken);

            if (!upload.IsSuccess || upload.Data is null)
            {
                return Result<PolicyVersionInfo>.Fail(upload.Error ?? "The file couldn't be uploaded.");
            }

            int versionNumber = policy.CurrentVersionNumber + 1;

            // The first version is always one everyone has to read.
            bool requiresReading = requiresReacknowledgement || versionNumber == 1;

            PolicyVersion version = new()
            {
                Id = Guid.NewGuid(),
                CompanyId = policy.CompanyId,
                PolicyId = policy.Id,
                VersionNumber = versionNumber,
                FileMetadataId = upload.Data.Id,
                FileSha256 = sha256,
                FileName = upload.Data.FileName,
                ChangeSummary = summary,
                RequiresReacknowledgement = requiresReading,
                PublishedDate = Now,
                PublishedByUserId = userId
            };

            ctx.PolicyVersions.Add(version);
            policy.CurrentVersionNumber = versionNumber;

            if (requiresReading)
            {
                policy.RequiredVersionNumber = versionNumber;
            }

            try
            {
                await ctx.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                // Most likely someone else published the same version number at the same moment.
                // The upload is orphaned, so take it back out.
                _logger.LogWarning(ex, "Publishing version {VersionNumber} of policy {PolicyId} failed; removing the uploaded file", versionNumber, policyId);
                await _fileService.DeleteRestrictedFileAsync(upload.Data.Id, CancellationToken.None);
                return Result<PolicyVersionInfo>.Fail("Another version was published at the same time. Please refresh and try again.");
            }

            _logger.LogInformation(
                "Policy {PolicyId} version {VersionNumber} published by {UserId} (re-acknowledgement required: {RequiresReacknowledgement})",
                policy.Id, versionNumber, userId, requiresReading);

            await SyncAudienceAsync(companyId, policy.Id, cancellationToken);

            // Tell everyone who now has it to read straight away, rather than at the next sweep.
            Result<int> reminded = await _reminders.SendDueRemindersAsync(policy.Id, cancellationToken);

            if (!reminded.IsSuccess)
            {
                _logger.LogWarning("Policy {PolicyId} published but its notifications couldn't be sent yet: {Error}", policy.Id, reminded.Error);
            }

            return Result<PolicyVersionInfo>.Ok(ToInfo(version, null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish a new version of policy {PolicyId}", policyId);
            return Result<PolicyVersionInfo>.Fail($"Failed to publish the policy: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeactivatePolicyAsync(Guid policyId, LocationScope scope, CancellationToken cancellationToken = default)
    {
        try
        {
            if (RefuseCaller<bool>(scope, out int companyId) is { } refused)
            {
                return refused;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            Policy? policy = await ctx.Policies
                .FirstOrDefaultAsync(x => x.Id == policyId && x.CompanyId == companyId && x.IsActive, cancellationToken);

            if (policy is null || !CanView(policy, scope))
            {
                return Result<bool>.Fail("Policy not found.");
            }

            if (!CanManage(policy, scope))
            {
                return Result<bool>.Fail("Only an Admin can remove a company-wide policy.");
            }

            policy.IsActive = false;

            // Nobody is asked to read it any more. The acknowledgements themselves are kept.
            List<PolicyAssignment> assignments = await ctx.PolicyAssignments
                .Where(x => x.PolicyId == policyId && x.IsActive)
                .ToListAsync(cancellationToken);

            foreach (PolicyAssignment assignment in assignments)
            {
                assignment.IsActive = false;
            }

            await ctx.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Policy {PolicyId} ({PolicyName}) removed; {Count} people no longer asked to read it", policy.Id, policy.Name, assignments.Count);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove policy {PolicyId}", policyId);
            return Result<bool>.Fail($"Failed to remove the policy: {ex.Message}");
        }
    }

    public async Task<Result<List<PolicyPersonStatus>>> GetPolicyStatusAsync(Guid policyId, LocationScope scope, CancellationToken cancellationToken = default)
    {
        try
        {
            if (RefuseCaller<List<PolicyPersonStatus>>(scope, out int companyId) is { } refused)
            {
                return refused;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            Policy? policy = await ctx.Policies
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == policyId && x.CompanyId == companyId && x.IsActive, cancellationToken);

            if (policy is null || !CanView(policy, scope))
            {
                return Result<List<PolicyPersonStatus>>.Fail("Policy not found.");
            }

            HashSet<string>? visiblePeople = await VisiblePeopleAsync(ctx, scope, cancellationToken);

            List<PolicyAssignment> assignments = await ctx.PolicyAssignments
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.PolicyId == policyId && x.IsActive)
                .ToListAsync(cancellationToken);

            assignments = [.. assignments.Where(x => visiblePeople is null || visiblePeople.Contains(x.UserId))];

            List<string> userIds = [.. assignments.Select(x => x.UserId)];

            Dictionary<string, string> names = (await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => userIds.Contains(x.Id))
                .ToListAsync(cancellationToken))
                .ToDictionary(x => x.Id, RotaNames.For);

            DateTime now = Now;

            List<PolicyPersonStatus> rows = [.. assignments
                .Select(x => new PolicyPersonStatus(
                    x.UserId,
                    names.TryGetValue(x.UserId, out string? name) ? name : "Unknown",
                    PolicyRequirementCalculator.StatusAt(x, now),
                    x.AcknowledgedVersionNumber,
                    x.AcknowledgedAtUtc,
                    x.DueDate))
                .OrderByDescending(x => x.Status)
                .ThenBy(x => x.DueDate ?? DateTime.MaxValue)
                .ThenBy(x => x.DisplayName)];

            return Result<List<PolicyPersonStatus>>.Ok(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load who has read policy {PolicyId}", policyId);
            return Result<List<PolicyPersonStatus>>.Fail($"Failed to load who has read the policy: {ex.Message}");
        }
    }

    public async Task<Result<FileContent>> OpenVersionFileAsync(Guid versionId, string userId, LocationScope scope, FileByteRange? range,
        CancellationToken cancellationToken = default)
    {
        const string notFound = "Policy not found.";

        try
        {
            if (_tenant.IsSystem || _tenant.CompanyId is not int companyId || string.IsNullOrEmpty(userId))
            {
                return Result<FileContent>.Fail(notFound);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            PolicyVersion? version = await ctx.PolicyVersions
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Policy)
                .FirstOrDefaultAsync(x => x.Id == versionId && x.CompanyId == companyId, cancellationToken);

            if (version?.Policy is not Policy policy)
            {
                return Result<FileContent>.Fail(notFound);
            }

            bool isManager = CanView(policy, scope);

            bool isReader = policy.IsActive && await ctx.PolicyAssignments
                .AnyAsync(x => x.PolicyId == policy.Id && x.UserId == userId && x.IsActive, cancellationToken);

            if (!isManager && !isReader)
            {
                _logger.LogWarning("Refused policy file {VersionId} to {UserId}: not assigned and not a manager of it", versionId, userId);
                return Result<FileContent>.Fail(notFound);
            }

            return await _fileService.OpenRestrictedFileContentAsync(version.FileMetadataId, range, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open policy file {VersionId}", versionId);
            return Result<FileContent>.Fail(notFound);
        }
    }

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    // Managers' pages are Admin/Manager-only, and a signed-out kiosk (system tenant) sees every
    // company, so both are checked here rather than trusted to the page.
    private Result<T>? RefuseCaller<T>(LocationScope scope, out int companyId)
    {
        companyId = 0;

        if (_tenant.IsSystem)
        {
            return Result<T>.Fail("Policies are only available to signed-in staff.");
        }

        if (!scope.IsAdmin && !scope.IsManager)
        {
            return Result<T>.Fail("Only Admins and Managers can manage policies.");
        }

        if (_tenant.CompanyId is not int tenantCompanyId)
        {
            return Result<T>.Fail("Your company couldn't be worked out. Please sign in again.");
        }

        if (!scope.IsAdmin && scope.LocationId is null)
        {
            return Result<T>.Fail("No location is set for this session. Please sign in again.");
        }

        companyId = tenantCompanyId;
        return null;
    }

    // An Admin sees everyone; a Manager only the people at their own location, even on a
    // company-wide policy.
    private static async Task<HashSet<string>?> VisiblePeopleAsync(ApplicationDbContext ctx, LocationScope scope, CancellationToken cancellationToken)
    {
        if (scope.IsAdmin)
        {
            return null;
        }

        List<string> members = await ctx.UserLocationMemberships
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.LocationId == scope.LocationId)
            .Select(x => x.UserId)
            .ToListAsync(cancellationToken);

        return [.. members];
    }

    private async Task SyncAudienceAsync(int companyId, Guid policyId, CancellationToken cancellationToken)
    {
        Result<PolicySyncResult> sync = await _audience.SyncAsync(companyId, policyId, cancellationToken);

        if (!sync.IsSuccess)
        {
            // The hourly sweep will catch up; the save itself has already gone through.
            _logger.LogWarning("Policy {PolicyId} saved but its audience couldn't be updated yet: {Error}", policyId, sync.Error);
        }
    }

    private static async Task<string?> ValidatePdfAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return "Choose a PDF to publish.";
        }

        if (file.Length > MaxFileBytes)
        {
            return $"The PDF can be at most {MaxFileBytes / (1024 * 1024)} MB.";
        }

        if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "Policies must be uploaded as a PDF.";
        }

        // The extension and content type are whatever the browser says; the first bytes aren't.
        await using Stream stream = file.OpenReadStream();
        byte[] header = new byte[PdfSignature.Length];
        int read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);

        return read == header.Length && header.AsSpan().SequenceEqual(PdfSignature) ? null : "That file isn't a valid PDF.";
    }

    private static async Task<string> HashAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using Stream stream = file.OpenReadStream();
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static string ScopeName(Policy policy) => policy.Location?.Name ?? "Whole company";

    private static List<string> PositionNames(Policy policy) =>
        [.. policy.Positions.Where(x => x.StaffPosition is not null).Select(x => x.StaffPosition!.Name).Order()];

    private static PolicyVersionInfo ToInfo(PolicyVersion version, string? publishedByName) =>
        new(version.Id, version.VersionNumber, version.FileName, version.ChangeSummary, version.RequiresReacknowledgement, version.PublishedDate, publishedByName);
}
