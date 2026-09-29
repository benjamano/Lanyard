using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// The company's leave policy: which kinds of time off exist and how much of each people get.
// Requests and balances live in ITimeOffService.
public interface ITimeOffPolicyService
{
    // A company that has never had any types gets the three defaults (Paid holiday, Unpaid leave,
    // Sickness) created on first read, so the request dialog is never empty.
    Task<Result<List<TimeOffType>>> GetTypesAsync(int companyId, bool includeInactive = false);

    // Creates (Id empty) or updates. Names are unique per company, ignoring case.
    Task<Result<TimeOffType>> SaveTypeAsync(LocationScope scope, TimeOffType type);

    // Archives the type: no new requests, existing ones keep their type.
    Task<Result<bool>> DeactivateTypeAsync(LocationScope scope, Guid typeId);

    // The rows saved at exactly one tier (company default when both keys are null), one per type
    // that has a value there. For the allowance editor.
    Task<Result<List<TimeOffAllowance>>> GetTierAsync(int companyId, Guid? positionId, string? userId);

    // Upserts the row for (type, tier).
    Task<Result<TimeOffAllowance>> SaveAllowanceAsync(LocationScope scope, TimeOffAllowance row);

    // Removes the row for (type, tier): an override goes back to inheriting; the company default
    // goes back to "no allowance".
    Task<Result<bool>> RemoveAllowanceAsync(LocationScope scope, int companyId, Guid typeId, Guid? positionId, string? userId);

    // The coalesced allowance per active type for the given keys, e.g. (company, position, null)
    // is what a person with that primary position and no personal override would get.
    Task<Result<Dictionary<Guid, ResolvedAllowance>>> ResolveAsync(int companyId, Guid? positionId, string? userId);

    // For a real person: their user row over their primary position over the company default.
    Task<Result<Dictionary<Guid, ResolvedAllowance>>> ResolveForUserAsync(string userId, int companyId);
}
