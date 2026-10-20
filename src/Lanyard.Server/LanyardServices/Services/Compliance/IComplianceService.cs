using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Compliance;

namespace Lanyard.Application.Services.Compliance;

public interface IComplianceService
{
    // Overdue training and expired/expiring staff documents for one location (or, for an Admin,
    // the whole company). Admins and Managers only; refuses anonymous/system callers outright.
    Task<Result<ComplianceSummary>> GetSummaryAsync(LocationScope scope, ComplianceQuery query, CancellationToken cancellationToken = default);
}
