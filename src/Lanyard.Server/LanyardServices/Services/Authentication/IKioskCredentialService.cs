using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Authentication;

// Who a valid kiosk credential belongs to.
public sealed record KioskIdentity(Guid ClientId, int CompanyId, Guid CredentialId);

// A newly issued credential. RawToken is the only copy of the secret anywhere: hand it to the kiosk
// and drop it.
public sealed record IssuedKioskCredential(Guid CredentialId, Guid ClientId, string RawToken);

public interface IKioskCredentialService
{
    // Checks a token a kiosk presented. Fails for anything that isn't an active credential of a
    // kiosk in an active company - the reason is for the log only, never for the caller.
    Task<Result<KioskIdentity>> ValidateAsync(string? rawToken, CancellationToken cancellationToken = default);

    // Re-checks a credential already validated once (a media token, a live hub connection), without the secret.
    Task<Result<KioskIdentity>> ValidateByIdAsync(Guid credentialId, CancellationToken cancellationToken = default);

    // The company of an existing kiosk, for a caller that only has the legacy shared secret and the
    // id the kiosk claims. Fails when there's no such kiosk.
    Task<Result<int>> GetClientCompanyIdAsync(Guid clientId, CancellationToken cancellationToken = default);

    // Issues an active credential for the kiosk, revoking any it already had.
    Task<Result<IssuedKioskCredential>> IssueAsync(Guid clientId, ClientCredentialSource source, string? createdByUserId, CancellationToken cancellationToken = default);
}
