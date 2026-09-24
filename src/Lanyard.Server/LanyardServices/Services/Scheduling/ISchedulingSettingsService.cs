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
}
