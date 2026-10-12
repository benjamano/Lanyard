using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Compliance;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Compliance;

// Who at a location is behind on training or holding an expired/expiring staff document.
//
// Access is decided here rather than trusted to the callers' role gates, because the widget that
// calls this can sit on a kiosk dashboard: a signed-out kiosk runs as the "system" tenant, which
// sees every company, so a system caller is refused before any query runs. Locations and location
// memberships aren't tenant-filtered either, so every query below is pinned to the caller's
// company explicitly instead of leaning on the global filter.
public class ComplianceService(
    IDbContextFactory<ApplicationDbContext> factory,
    ITenantContext tenant,
    TimeProvider timeProvider,
    ILogger<ComplianceService> logger) : IComplianceService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ITenantContext _tenant = tenant;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ComplianceService> _logger = logger;

    private record DocumentFact(string UserId, Guid StaffDocumentTypeId, string TypeName, DateTime? ExpiryDate, DateTime UploadedDate);

    public async Task<Result<ComplianceSummary>> GetSummaryAsync(LocationScope scope, ComplianceQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_tenant.IsSystem)
            {
                _logger.LogWarning("Refused a compliance summary for a signed-out (system) caller");
                return Result<ComplianceSummary>.Fail("Compliance figures are only shown to a signed-in Admin or Manager.");
            }

            if (!scope.IsAdmin && !scope.IsManager)
            {
                return Result<ComplianceSummary>.Fail("Only Admins and Managers can see compliance figures.");
            }

            if (_tenant.CompanyId is not int companyId)
            {
                return Result<ComplianceSummary>.Fail("Your company couldn't be worked out. Please sign in again.");
            }

            if (query.ExpiryWindowDays < ComplianceQuery.MinExpiryWindowDays || query.ExpiryWindowDays > ComplianceQuery.MaxExpiryWindowDays)
            {
                return Result<ComplianceSummary>.Fail(
                    $"The expiry window must be between {ComplianceQuery.MinExpiryWindowDays} and {ComplianceQuery.MaxExpiryWindowDays} days.");
            }

            if (query.MaxRowsPerSection is < 0)
            {
                return Result<ComplianceSummary>.Fail("The number of names to show can't be negative.");
            }

            // A Manager is pinned to the location they signed in to, whatever was asked for. An
            // Admin may name one location, or none for the whole company.
            int? requestedLocationId = scope.IsAdmin ? query.LocationId : scope.LocationId;

            if (!scope.IsAdmin && requestedLocationId is null)
            {
                return Result<ComplianceSummary>.Fail("No location is set for this session. Please sign in again.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            List<Location> companyLocations = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Company)
                .Where(x => x.CompanyId == companyId && x.IsActive)
                .ToListAsync(cancellationToken);

            List<int> locationIds;
            string locationName;

            if (requestedLocationId is int locationId)
            {
                Location? location = companyLocations.FirstOrDefault(x => x.Id == locationId);

                if (location is null)
                {
                    _logger.LogWarning("Compliance summary asked for location {LocationId}, which isn't an active location of company {CompanyId}",
                        locationId, companyId);
                    return Result<ComplianceSummary>.Fail("That location isn't available.");
                }

                locationIds = [location.Id];
                locationName = location.GetDisplayName();
            }
            else
            {
                locationIds = [.. companyLocations.Select(x => x.Id)];
                locationName = "All locations";
            }

            // Only an Admin looking at the whole company sees assignments with no location: an
            // Admin assigning a course with no location of its own leaves the assignment unplaced.
            bool includeUnplacedAssignments = requestedLocationId is null;

            List<string> staffIds = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => locationIds.Contains(x.LocationId) && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
                .Select(x => x.UserId)
                .Distinct()
                .ToListAsync(cancellationToken);

            Dictionary<string, string> namesById = (await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => staffIds.Contains(x.Id))
                .ToListAsync(cancellationToken))
                .ToDictionary(x => x.Id, RotaNames.For);

            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
            DateTime today = now.Date;

            ComplianceSection overdue = await GetOverdueTrainingAsync(ctx, companyId, staffIds, locationIds, includeUnplacedAssignments, namesById, now, query.MaxRowsPerSection, cancellationToken);
            (ComplianceSection expired, ComplianceSection expiring) = await GetDocumentSectionsAsync(ctx, companyId, staffIds, namesById, today, query.ExpiryWindowDays, query.MaxRowsPerSection, cancellationToken);

            return Result<ComplianceSummary>.Ok(new ComplianceSummary(requestedLocationId, locationName, query.ExpiryWindowDays, overdue, expired, expiring));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the compliance summary for location {LocationId}", query.LocationId);
            return Result<ComplianceSummary>.Fail($"Failed to load compliance figures: {ex.Message}");
        }
    }

    private static async Task<ComplianceSection> GetOverdueTrainingAsync(
        ApplicationDbContext ctx,
        int companyId,
        List<string> staffIds,
        List<int> locationIds,
        bool includeUnplaced,
        Dictionary<string, string> namesById,
        DateTime now,
        int? maxRows,
        CancellationToken cancellationToken)
    {
        // Same location rule as the training dashboard (assignment.LocationId), so the two agree.
        List<CourseAssignment> assignments = await ctx.CourseAssignments
            .AsNoTracking()
            .TagWithCallSite()
            .Include(x => x.Course)
            .Where(x => x.IsActive
                && x.CompletedDate == null
                && x.DueDate != null
                && x.DueDate < now
                && x.Course!.IsActive
                && x.Course.CompanyId == companyId
                && staffIds.Contains(x.UserId)
                && ((x.LocationId != null && locationIds.Contains(x.LocationId.Value)) || (includeUnplaced && x.LocationId == null)))
            .ToListAsync(cancellationToken);

        List<ComplianceRow> rows = [.. assignments
            .Select(x => new ComplianceRow(
                x.UserId,
                namesById.GetValueOrDefault(x.UserId, "Unknown"),
                $"{x.Course!.Name} · {DescribeOverdue(now.Date, x.DueDate!.Value.Date)}",
                x.DueDate!.Value))
            .OrderBy(x => x.Date)
            .ThenBy(x => x.DisplayName)];

        return BuildSection(ComplianceSectionKind.OverdueTraining, rows, maxRows);
    }

    private static async Task<(ComplianceSection Expired, ComplianceSection Expiring)> GetDocumentSectionsAsync(
        ApplicationDbContext ctx,
        int companyId,
        List<string> staffIds,
        Dictionary<string, string> namesById,
        DateTime today,
        int windowDays,
        int? maxRows,
        CancellationToken cancellationToken)
    {
        List<DocumentFact> documents = await ctx.StaffDocuments
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsActive
                && x.CompanyId == companyId
                && x.StaffDocumentType!.IsActive
                && staffIds.Contains(x.UserId))
            .Select(x => new DocumentFact(x.UserId, x.StaffDocumentTypeId, x.StaffDocumentType!.Name, x.ExpiryDate, x.UploadedDate))
            .ToListAsync(cancellationToken);

        // Uploading a renewal adds a new row and leaves the old one active, so only the newest
        // document of each type per person says whether they're covered. Grouped in memory: the
        // rows are already narrowed to one company's staff, and GroupBy-then-First doesn't
        // translate on every provider.
        List<DocumentFact> latest = [.. documents
            .GroupBy(x => (x.UserId, x.StaffDocumentTypeId))
            .Select(g => g.OrderByDescending(x => x.UploadedDate).First())
            .Where(x => x.ExpiryDate is not null)];

        DateTime windowEnd = today.AddDays(windowDays);

        List<ComplianceRow> expired = [.. latest
            .Where(x => x.ExpiryDate!.Value.Date < today)
            .Select(x => ToRow(x, namesById, today))
            .OrderBy(x => x.Date)
            .ThenBy(x => x.DisplayName)];

        List<ComplianceRow> expiring = [.. latest
            .Where(x => x.ExpiryDate!.Value.Date >= today && x.ExpiryDate.Value.Date <= windowEnd)
            .Select(x => ToRow(x, namesById, today))
            .OrderBy(x => x.Date)
            .ThenBy(x => x.DisplayName)];

        return (BuildSection(ComplianceSectionKind.ExpiredDocuments, expired, maxRows),
                BuildSection(ComplianceSectionKind.ExpiringDocuments, expiring, maxRows));
    }

    private static ComplianceRow ToRow(DocumentFact document, Dictionary<string, string> namesById, DateTime today) =>
        new(document.UserId,
            namesById.GetValueOrDefault(document.UserId, "Unknown"),
            $"{document.TypeName} · {DescribeExpiry(today, document.ExpiryDate!.Value.Date)}",
            document.ExpiryDate!.Value);

    private static ComplianceSection BuildSection(ComplianceSectionKind kind, List<ComplianceRow> rows, int? maxRows)
    {
        int people = rows.Select(x => x.UserId).Distinct().Count();
        List<ComplianceRow> shown = maxRows is int max ? [.. rows.Take(max)] : rows;

        return new ComplianceSection(kind, people, rows.Count, shown);
    }

    private static string DescribeOverdue(DateTime today, DateTime dueDate)
    {
        int days = (int)(today - dueDate).TotalDays;

        return days switch
        {
            <= 0 => "due today",
            1 => "1 day overdue",
            _ => $"{days} days overdue"
        };
    }

    private static string DescribeExpiry(DateTime today, DateTime expiryDate)
    {
        int days = (int)(expiryDate - today).TotalDays;

        return days switch
        {
            < -1 => $"expired {-days} days ago",
            -1 => "expired yesterday",
            0 => "expires today",
            1 => "expires tomorrow",
            _ => $"expires in {days} days"
        };
    }
}
