using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Locations;

public record LocationScope(bool IsAdmin, int? LocationId, int? CompanyId, string? LocationDisplayName);

public interface ICurrentLocationContext
{
    Task<Result<LocationScope>> GetScopeAsync();
}
