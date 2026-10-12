using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Parties;

// A company's voucher and feedback form artwork. Company-wide: an Admin, or a Manager signed in
// at one of the company's locations, may change it.
public interface IPartyTemplateService
{
    // The saved template, or the default (unsaved, Id empty, no artwork) when there's none.
    Task<Result<PartyDocumentTemplate>> GetTemplateAsync(LocationScope scope, int companyId, PartyArtworkDocument document);

    Task<Result<PartyDocumentTemplate>> SaveTemplateAsync(LocationScope scope, PartyDocumentTemplate template, string actingUserId);
}
