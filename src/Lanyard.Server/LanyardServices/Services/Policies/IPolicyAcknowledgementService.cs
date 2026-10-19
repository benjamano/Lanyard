using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Policies;

namespace Lanyard.Application.Services.Policies;

// The staff side: what someone has to read, and recording that they've read it.
public interface IPolicyAcknowledgementService
{
    // Every published policy the person is currently asked to read, overdue first.
    Task<Result<List<MyPolicyItem>>> GetMyPoliciesAsync(string userId, CancellationToken cancellationToken = default);

    Task<Result<MyPolicyItem>> GetMyPolicyAsync(string userId, Guid policyId, CancellationToken cancellationToken = default);

    // Records that userId has read the policy's current version. Only the current version can be
    // acknowledged, only by someone it is assigned to; doing it again while up to date returns
    // the existing record rather than adding another.
    Task<Result<PolicyAcknowledgementResult>> AcknowledgeAsync(Guid versionId, string userId, PolicyAcknowledgementEvidence evidence,
        CancellationToken cancellationToken = default);

    // A person's acknowledgements, newest first. Anyone can see their own; an Admin anyone's in
    // their company; a Manager anyone at their location. IP address and device only for Admins.
    Task<Result<List<PolicyAcknowledgementRecord>>> GetHistoryAsync(string targetUserId, string requestingUserId, LocationScope scope,
        CancellationToken cancellationToken = default);
}
