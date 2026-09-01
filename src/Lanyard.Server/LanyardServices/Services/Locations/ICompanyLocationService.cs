using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Locations;

public record LoginLocationOption(int LocationId, string DisplayName, int CompanyId, string? ThemeColorHex, Guid? LogoFileId);
public record LoginCompanyOption(int CompanyId, string Name, string? ThemeColorHex, Guid? LogoFileId, Guid? BackgroundImageFileId);
public record CompanyBrandingInfo(int CompanyId, string? ThemeColorHex, Guid? LogoFileId, Guid? BackgroundImageFileId);

public interface ICompanyLocationService
{
    Task<Result<List<Company>>> GetCompaniesAsync();
    Task<Result<Company>> SaveCompanyAsync(Company company);
    Task<Result<bool>> DeactivateCompanyAsync(int companyId);

    Task<Result<List<Location>>> GetLocationsAsync(int? companyId = null);
    Task<Result<Location>> SaveLocationAsync(Location location);
    Task<Result<bool>> DeactivateLocationAsync(int locationId);

    /// <summary>
    /// Turns QR food ordering on or off for one venue.
    /// </summary>
    /// <remarks>
    /// Its own method rather than a field on <see cref="SaveLocationAsync"/> deliberately. That
    /// method only ever writes the name, so every existing caller constructs a partial Location;
    /// were the flag added there, any of those callers would silently switch ordering off for a
    /// venue as a side effect of renaming it.
    /// </remarks>
    Task<Result<bool>> SetLocationOrderingEnabledAsync(int locationId, bool orderingEnabled);

    Task<Result<List<Location>>> GetLocationsForUserAsync(string userId);
    Task<Result<List<UserProfile>>> GetUsersInLocationAsync(int locationId);
    Task<Result<bool>> AddUserToLocationAsync(string userId, int locationId);
    Task<Result<bool>> RemoveUserFromLocationAsync(string userId, int locationId);

    Task<Result<bool>> IsUserMemberOfLocationAsync(string userId, int locationId);
    Task<Result<List<LoginLocationOption>>> GetLoginLocationOptionsAsync(int? companyId = null);
    Task<Result<List<LoginCompanyOption>>> GetLoginCompanyOptionsAsync();
    Task<Result<CompanyBrandingInfo>> GetCompanyBrandingAsync(int companyId);
    Task<Result<CompanyBrandingInfo>> GetCompanyBrandingForLocationAsync(int locationId);

    /// <summary>
    /// Branding for the company a user belongs to, resolved from their location memberships.
    /// </summary>
    /// <remarks>
    /// Branding is per-company, so which of a user's locations we pick is irrelevant as long
    /// as they all belong to one company - which is the normal case. Fails when the user
    /// belongs to no company, or to more than one, since neither has a single right answer.
    /// </remarks>
    Task<Result<CompanyBrandingInfo>> GetCompanyBrandingForUserAsync(string userId);
}
