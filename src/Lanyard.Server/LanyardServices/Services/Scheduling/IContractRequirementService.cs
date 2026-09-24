using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

public interface IContractRequirementService
{
    // The raw row at exactly one tier (company default when both keys are null), for an editor.
    // Null when nothing has been saved at that tier yet.
    Task<Result<ContractRequirement?>> GetTierAsync(int companyId, Guid? positionId, string? userId);

    // Upserts by tier keys. An override-tier row (position or user) whose fields are all null is
    // deleted rather than stored - "inherit everything" and "no row" mean the same thing, and
    // keeping empty rows would make "does this position have an override?" ambiguous.
    // Returns the saved row, or null when the row was removed.
    Task<Result<ContractRequirement?>> SaveTierAsync(LocationScope scope, ContractRequirement row);

    // Per-field coalesce along the chain company -> position -> user, for the given keys. Pass
    // only what is known: (companyId, null, null) is the company default; (companyId, positionId,
    // null) is what a person with that primary position and no personal override would get.
    Task<Result<ResolvedContract>> ResolveAsync(int companyId, Guid? positionId, string? userId);

    // Resolves for a real person: their user-tier row over their primary position over the
    // company default.
    Task<Result<ResolvedContract>> ResolveForUserAsync(string userId, int companyId);

    Task<Result<Dictionary<string, ResolvedContract>>> ResolveForUsersAsync(IEnumerable<string> userIds, int companyId);
}
