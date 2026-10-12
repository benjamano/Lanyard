using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Parties;

public record PartyDocument(byte[] Pdf, string FileName);

public interface IPartyDocumentService
{
    // Same access rule as viewing the booking. A cancelled party still prints (it might be
    // reinstated), so nothing about its status is checked here. authorisedBy is the name signed
    // on the voucher: whoever is printing it, never stored.
    Task<Result<PartyDocument>> BuildAsync(LocationScope scope, Guid bookingId, PartyDocumentType type, string? authorisedBy, CancellationToken cancellationToken = default);

    // The company's saved voucher or feedback form filled in with a made-up party, so the box
    // positions can be checked before any real party is printed.
    Task<Result<PartyDocument>> BuildPreviewAsync(LocationScope scope, int companyId, PartyArtworkDocument document, string? authorisedBy, CancellationToken cancellationToken = default);
}
