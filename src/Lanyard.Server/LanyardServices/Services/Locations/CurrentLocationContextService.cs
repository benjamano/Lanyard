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

            if (authState.User.IsInRole("Admin"))
            {
                return Result<LocationScope>.Ok(new LocationScope(true, null, null, null));
            }

            string? locationIdClaim = authState.User.FindFirst(LocationClaimTypes.LocationId)?.Value;

            if (string.IsNullOrEmpty(locationIdClaim) || !int.TryParse(locationIdClaim, out int locationId))
            {
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
                return Result<LocationScope>.Fail("Your selected location is no longer available. Please log in again.");
            }

            return Result<LocationScope>.Ok(new LocationScope(false, location.Id, location.CompanyId, location.GetDisplayName()));
        }
        catch (Exception ex)
        {
            return Result<LocationScope>.Fail($"Failed to resolve location scope: {ex.Message}");
        }
    }
}
