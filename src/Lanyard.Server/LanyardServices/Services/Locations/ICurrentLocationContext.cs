using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Locations;

// IsManager is carried so services can enforce "Admin, or a Manager at this location" themselves
// rather than trusting that only role-gated pages ever call them.
public record LocationScope(bool IsAdmin, int? LocationId, int? CompanyId, string? LocationDisplayName, bool IsManager = false);

public interface ICurrentLocationContext
{
    Task<Result<LocationScope>> GetScopeAsync();
}
