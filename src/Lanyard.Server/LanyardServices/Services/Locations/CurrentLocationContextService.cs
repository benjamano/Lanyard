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
            string? locationIdClaim = authState.User.FindFirst(LocationClaimTypes.LocationId)?.Value;

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
            return Result<LocationScope>.Ok(new LocationScope(isAdmin, location.Id, location.CompanyId, location.GetDisplayName()));
        }
        catch (Exception ex)
        {
            return Result<LocationScope>.Fail($"Failed to resolve location scope: {ex.Message}");
        }
    }
}
