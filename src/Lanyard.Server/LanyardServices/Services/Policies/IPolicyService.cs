using Lanyard.Application.Services;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Policies;
using Microsoft.AspNetCore.Http;

namespace Lanyard.Application.Services.Policies;

// The manager side of policies. Admins manage every policy in their company; a Manager manages
// their own location's policies and can see (not change) company-wide ones, counting only their
// own location's people. Signed-out (system) callers are refused.
public interface IPolicyService
{
    Task<Result<List<PolicySummary>>> GetPoliciesAsync(LocationScope scope, CancellationToken cancellationToken = default);

    Task<Result<PolicyDetail>> GetPolicyAsync(Guid policyId, LocationScope scope, CancellationToken cancellationToken = default);

    // Creates (request.Id null) or edits a policy's settings. Returns the policy id.
    Task<Result<Guid>> SavePolicyAsync(PolicySaveRequest request, LocationScope scope, string userId, CancellationToken cancellationToken = default);

    // Uploads a PDF as the next version. requiresReacknowledgement false keeps existing
    // acknowledgements counting (a typo fix); the first version always requires it.
    Task<Result<PolicyVersionInfo>> PublishVersionAsync(Guid policyId, IFormFile file, string? changeSummary, bool requiresReacknowledgement,
        LocationScope scope, string userId, CancellationToken cancellationToken = default);

    Task<Result<bool>> DeactivatePolicyAsync(Guid policyId, LocationScope scope, CancellationToken cancellationToken = default);

    // Everyone currently asked to read it, overdue first.
    Task<Result<List<PolicyPersonStatus>>> GetPolicyStatusAsync(Guid policyId, LocationScope scope, CancellationToken cancellationToken = default);

    // The PDF behind a version, for someone it is assigned to or a manager who can see the policy.
    // Every refusal reads "not found" so the endpoint gives nothing away.
    Task<Result<FileContent>> OpenVersionFileAsync(Guid versionId, string userId, LocationScope scope, FileByteRange? range,
        CancellationToken cancellationToken = default);
}
