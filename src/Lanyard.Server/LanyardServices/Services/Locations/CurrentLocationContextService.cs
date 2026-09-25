using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Locations;

public class CurrentLocationContextService(
    AuthenticationStateProvider authStateProvider,
    IDbContextFactory<ApplicationDbContext> factory) : ICurrentLocationContext
{
    private readonly AuthenticationStateProvider _authStateProvider = authStateProvider;
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    // Scoped service, so this memo lives for one circuit. The scope is requested by the layout,
    // the footer, the home page and a dozen other components on every page, and it only ever
    // changes when the claims change (re-login) or the location row is edited - the key covers
    // the first and the short TTL the second.
    private static readonly TimeSpan ScopeCacheTtl = TimeSpan.FromMinutes(1);
    private string? _cachedScopeKey;
    private Result<LocationScope>? _cachedScope;
    private DateTime _cachedScopeAtUtc;

    public async Task<Result<LocationScope>> GetScopeAsync()
    {
        try
        {
            AuthenticationState authState = await _authStateProvider.GetAuthenticationStateAsync();

            if (authState.User?.Identity?.IsAuthenticated != true)
            {
                return Result<LocationScope>.Fail("User is not authenticated.");
            }

            bool isAdmin = authState.User.IsInRole("Admin");
            bool isManager = authState.User.IsInRole("Manager");
            string? locationIdClaim = authState.User.FindFirst(LocationClaimTypes.LocationId)?.Value;

            string cacheKey = $"{isAdmin}|{isManager}|{locationIdClaim}";

            if (_cachedScope is not null
                && _cachedScopeKey == cacheKey
                && DateTime.UtcNow - _cachedScopeAtUtc < ScopeCacheTtl)
            {
                return _cachedScope;
            }

            Result<LocationScope> scope = await ResolveScopeAsync(isAdmin, isManager, locationIdClaim);

            _cachedScopeKey = cacheKey;
            _cachedScope = scope;
            _cachedScopeAtUtc = DateTime.UtcNow;

            return scope;
        }
        catch (Exception ex)
        {
            return Result<LocationScope>.Fail($"Failed to resolve location scope: {ex.Message}");
        }
    }

    private async Task<Result<LocationScope>> ResolveScopeAsync(bool isAdmin, bool isManager, string? locationIdClaim)
    {
        {

            if (string.IsNullOrEmpty(locationIdClaim) || !int.TryParse(locationIdClaim, out int locationId))
            {
                // Admins don't have to pick a location at login (unlike everyone else) - no
                // claim just means they get the default Lanyard branding, not a hard failure.
                if (isAdmin)
                {
                    return Result<LocationScope>.Ok(new LocationScope(true, null, null, null));
                }

                return Result<LocationScope>.Fail("No location is set for this session. Please log in again.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Company)
                .FirstOrDefaultAsync(x => x.Id == locationId);

            if (location is null || !location.IsActive)
            {
                if (isAdmin)
                {
                    return Result<LocationScope>.Ok(new LocationScope(true, null, null, null));
                }

                return Result<LocationScope>.Fail("Your selected location is no longer available. Please log in again.");
            }

            // IsAdmin stays true here even though a location is attached. Permission checks still
            // short-circuit on IsAdmin before looking at LocationId, so an admin can still read and
            // write any location's data. An admin's LocationId is no longer branding-only though:
            // CourseService.GetCoursesAsync uses it to decide which location's courses to list
            // unless allLocations is set, so treat it as a real value when adding scoped reads.
            return Result<LocationScope>.Ok(new LocationScope(isAdmin, location.Id, location.CompanyId, location.GetDisplayName(), isManager));
        }
    }
}
