using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

public interface ISchedulingSettingsService
{
    // Always returns something usable: an unsaved default-valued instance when the company has
    // no row yet, so callers never special-case "not configured".
    Task<Result<CompanySchedulingSettings>> GetSettingsAsync(int companyId);
    Task<Result<CompanySchedulingSettings>> SaveSettingsAsync(LocationScope scope, CompanySchedulingSettings settings);

    // Contracts, PINs and allowances all hang off "the user's company". Fails when the user is
    // in no active company or more than one - same rule as company branding, because there is
    // no right answer to pick.
    Task<Result<int>> ResolveCompanyIdForUserAsync(string userId);
}
