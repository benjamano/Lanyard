using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Locations;

public record LoginLocationOption(int LocationId, string DisplayName, int CompanyId, string? ThemeColorHex, Guid? LogoFileId);
public record CompanyBrandingInfo(int CompanyId, string? ThemeColorHex, Guid? LogoFileId);

public interface ICompanyLocationService
{
    Task<Result<List<Company>>> GetCompaniesAsync();
    Task<Result<Company>> SaveCompanyAsync(Company company);
    Task<Result<bool>> DeactivateCompanyAsync(int companyId);

    Task<Result<List<Location>>> GetLocationsAsync(int? companyId = null);
    Task<Result<Location>> SaveLocationAsync(Location location);
    Task<Result<bool>> DeactivateLocationAsync(int locationId);

    Task<Result<List<Location>>> GetLocationsForUserAsync(string userId);
    Task<Result<List<UserProfile>>> GetUsersInLocationAsync(int locationId);
    Task<Result<bool>> AddUserToLocationAsync(string userId, int locationId);
    Task<Result<bool>> RemoveUserFromLocationAsync(string userId, int locationId);

    Task<Result<bool>> IsUserMemberOfLocationAsync(string userId, int locationId);
    Task<Result<List<LoginLocationOption>>> GetLoginLocationOptionsAsync();
    Task<Result<CompanyBrandingInfo>> GetCompanyBrandingAsync(int companyId);
    Task<Result<CompanyBrandingInfo>> GetCompanyBrandingForLocationAsync(int locationId);
}
