using System.Security.Claims;
using Lanyard.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Locations;

/// <summary>
/// Which companies the signed-in user is allowed to administer.
///
/// Admins administer every company. A Manager administers only the companies they are actually
/// a member of, worked out from their location memberships - so a manager can be given the
/// Companies &amp; Locations page to run their own venue's branding, wording and domains without
/// being able to see, let alone edit, another tenant's.
///
/// Deliberately not part of ISecurityService: that already depends on ICompanyLocationService,
/// and the company services need to consult this one, so putting it there would close a
/// dependency cycle. This has no dependencies beyond auth state and the database.
/// </summary>
public interface ICompanyAccessService
{
    Task<CompanyAccess> GetCurrentAsync();

    Task<bool> CanAdministerCompanyAsync(int companyId);

    Task<bool> CanAdministerLocationAsync(int locationId);

    /// <summary>
    /// Whether the current user may change how one venue runs day to day - its ordering switch,
    /// its opening hours, its fulfilment mode and its receipt printer.
    ///
    /// Wider than <see cref="CanAdministerLocationAsync"/> on purpose. Administering a company is
    /// branding, domains, legal wording; running a venue's service is a kitchen job, and the
    /// CanManageKitchen role exists for people who do exactly that and nothing else. Without this
    /// they were handed a fully populated kitchen admin page on which every control failed.
    /// Still narrower than the page's own role gate: the membership has to be for this venue.
    /// </summary>
    Task<bool> CanManageVenueOperationsAsync(int locationId);
}

/// <summary>
/// What the current user may administer. <see cref="CompanyIds"/> is meaningless when
/// <see cref="IsAdmin"/> is true - an admin is not restricted to a list.
/// </summary>
public record CompanyAccess(bool IsAdmin, IReadOnlyList<int> CompanyIds)
{
    public bool CanAdminister(int companyId) => IsAdmin || CompanyIds.Contains(companyId);

    /// <summary>Creating and deactivating whole companies stays with admins.</summary>
    public bool CanCreateCompanies => IsAdmin;

    public static readonly CompanyAccess None = new(false, []);
}

public class CompanyAccessService(
    AuthenticationStateProvider authStateProvider,
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<CompanyAccessService> logger) : ICompanyAccessService
{
    private readonly AuthenticationStateProvider _authStateProvider = authStateProvider;
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<CompanyAccessService> _logger = logger;

    public async Task<CompanyAccess> GetCurrentAsync()
    {
        try
        {
            AuthenticationState state = await _authStateProvider.GetAuthenticationStateAsync();
            ClaimsPrincipal? user = state.User;

            // Fails closed. Anything that cannot be established - no user, no identity, no
            // membership - grants nothing rather than defaulting to everything.
            if (user?.Identity?.IsAuthenticated != true)
            {
                return CompanyAccess.None;
            }

            if (user.IsInRole("Admin"))
            {
                return new CompanyAccess(true, []);
            }

            if (!user.IsInRole("Manager"))
            {
                return CompanyAccess.None;
            }

            string? userId = user.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrWhiteSpace(userId))
            {
                return CompanyAccess.None;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<int> companyIds = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(m => m.UserId == userId && m.Location!.IsActive && m.Location.Company!.IsActive)
                .Select(m => m.Location!.CompanyId)
                .Distinct()
                .ToListAsync();

            return new CompanyAccess(false, companyIds);
        }
        catch (Exception ex)
        {
            // Also fails closed: an error working out permissions must not become permission.
            _logger.LogError(ex, "Failed to resolve which companies the current user may administer");

            return CompanyAccess.None;
        }
    }

    public async Task<bool> CanAdministerCompanyAsync(int companyId) =>
        (await GetCurrentAsync()).CanAdminister(companyId);

    public async Task<bool> CanManageVenueOperationsAsync(int locationId)
    {
        // Admins and the company's own managers get here on the company-level check.
        if (await CanAdministerLocationAsync(locationId))
        {
            return true;
        }

        try
        {
            AuthenticationState state = await _authStateProvider.GetAuthenticationStateAsync();
            ClaimsPrincipal? user = state.User;

            if (user?.Identity?.IsAuthenticated != true || !user.IsInRole("CanManageKitchen"))
            {
                return false;
            }

            string? userId = user.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrWhiteSpace(userId))
            {
                return false;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Scoped to the company, deliberately, because that is exactly what the venue picker
            // on every kitchen screen shows: VisibleLocations returns the user's whole company.
            // Requiring membership of the one venue instead would put back the fault this method
            // was added to remove - a populated picker where half the entries refuse to save.
            int? targetCompanyId = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Where(l => l.Id == locationId && l.IsActive)
                .Select(l => (int?)l.CompanyId)
                .FirstOrDefaultAsync();

            if (targetCompanyId is not int companyId)
            {
                return false;
            }

            return await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(m => m.UserId == userId
                    && m.Location!.CompanyId == companyId
                    && m.Location.IsActive
                    && m.Location.Company!.IsActive);
        }
        catch (Exception ex)
        {
            // Fails closed, for the same reason GetCurrentAsync does.
            _logger.LogError(ex, "Failed to resolve whether the current user may run venue {LocationId}", locationId);

            return false;
        }
    }

    public async Task<bool> CanAdministerLocationAsync(int locationId)
    {
        CompanyAccess access = await GetCurrentAsync();

        if (access.IsAdmin)
        {
            return true;
        }

        if (access.CompanyIds.Count == 0)
        {
            return false;
        }

        await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

        int? companyId = await ctx.Locations
            .AsNoTracking()
            .TagWithCallSite()
            .Where(l => l.Id == locationId)
            .Select(l => (int?)l.CompanyId)
            .FirstOrDefaultAsync();

        return companyId is not null && access.CanAdminister(companyId.Value);
    }
}
