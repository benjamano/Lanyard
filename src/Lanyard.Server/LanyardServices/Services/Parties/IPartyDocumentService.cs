using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Parties;

public record PartyDocument(byte[] Pdf, string FileName);

public interface IPartyDocumentService
{
    // Same access rule as viewing the booking. A cancelled party still prints (it might be
    // reinstated), so nothing about its status is checked here.
    Task<Result<PartyDocument>> BuildAsync(LocationScope scope, Guid bookingId, PartyDocumentType type, CancellationToken cancellationToken = default);
}
