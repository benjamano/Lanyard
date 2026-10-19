using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Policies;

public record PolicySyncResult(int Added, int Reactivated, int Deactivated, int Updated)
{
    public static readonly PolicySyncResult Empty = new(0, 0, 0, 0);
}

public interface IPolicyAudienceService
{
    // Brings each published policy's assignments in line with who is in its audience right now:
    // adds people who joined the location (or position), deactivates people who left, and
    // recomputes everyone's due dates. One policy, or every policy in the company when policyId
    // is null. Does nothing for a company with Policies switched off. Safe to call from a system
    // (signed-out) context: every query is pinned to companyId.
    Task<Result<PolicySyncResult>> SyncAsync(int companyId, Guid? policyId = null, CancellationToken cancellationToken = default);
}
