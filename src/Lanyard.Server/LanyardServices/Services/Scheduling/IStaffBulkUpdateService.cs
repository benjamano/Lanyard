using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;

namespace Lanyard.Application.Services.Scheduling;

// Manage > Rota > Staff: one location's people in one list, and the same position, contract or
// allowance change applied to many of them at once. Positions, contracts and allowances are
// company-level settings, so companyId says whose they are; locationId says whose staff are
// listed and may be changed. Every Apply* is all-or-nothing - if one person can't take the
// change (say it would leave their minimum hours above their maximum), nobody is changed and
// the error names who. Each returns how many people actually changed.
public interface IStaffBulkUpdateService
{
    Task<Result<List<StaffRotaSummary>>> GetStaffAsync(LocationScope scope, int companyId, int locationId);

    Task<Result<int>> ApplyPositionsAsync(LocationScope scope, int companyId, int locationId, List<string> userIds, BulkPositionChange change);

    Task<Result<int>> ApplyContractAsync(LocationScope scope, int companyId, int locationId, List<string> userIds, BulkContractChange change, string? updatedByUserId);

    Task<Result<int>> ApplyAllowancesAsync(LocationScope scope, int companyId, int locationId, List<string> userIds, List<BulkAllowanceChange> changes, string? updatedByUserId);
}
