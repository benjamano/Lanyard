using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.Contracts;

namespace Lanyard.Application.Services.Authentication;

// What an Admin sees after typing a pairing code, before approving it.
public sealed record PendingKioskEnrolment(
    Guid ClientId,
    string? MachineName,
    string? ReportedVersion,
    string? RequestIp,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    // Set when this PC is already one of the company's kiosks (re-paired after a reset or a
    // revoke): approving it keeps all its settings.
    string? ExistingName,
    int? ExistingLocationId);

public interface IKioskEnrolmentService
{
    // --- Kiosk side (anonymous) ---------------------------------------------------------------

    Task<Result<KioskEnrolmentStartedDTO>> StartAsync(KioskEnrolmentRequestDTO request, string? requestIp, CancellationToken cancellationToken = default);

    // Fails ("not found") for an unknown request or the wrong poll token - deliberately the same.
    Task<Result<KioskEnrolmentStatusDTO>> PollAsync(Guid requestId, string? pollToken, CancellationToken cancellationToken = default);

    // --- Admin side (the signed-in Admin's own company) ---------------------------------------

    Task<Result<PendingKioskEnrolment>> FindPendingAsync(string pairingCode, CancellationToken cancellationToken = default);

    Task<Result<Guid>> ApproveAsync(string pairingCode, string name, int? locationId, CancellationToken cancellationToken = default);

    Task<Result<bool>> RejectAsync(string pairingCode, CancellationToken cancellationToken = default);
}
