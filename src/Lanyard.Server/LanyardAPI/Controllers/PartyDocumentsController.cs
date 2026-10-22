using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Lanyard.API.Controllers
{
    [ApiController]
    [Route("api/parties")]
    public class PartyDocumentsController(
        IPartyDocumentService documentService,
        ICurrentLocationContext locationContext,
        ICompanyFeatureService companyFeatures,
        UserManager<UserProfile> userManager) : ControllerBase
    {
        private readonly IPartyDocumentService _documentService = documentService;
        private readonly ICurrentLocationContext _locationContext = locationContext;
        private readonly ICompanyFeatureService _companyFeatures = companyFeatures;
        private readonly UserManager<UserProfile> _userManager = userManager;

        // Cookie auth, managers and admins only, like the party pages. The location check is the
        // service's, against the caller's own session; a party in another company isn't visible
        // at all through the tenant filter. Every failure is a plain 404 - "not found", "not
        // yours" and "feature off" are deliberately indistinguishable, as on the certificate
        // endpoint.
        //
        // Served inline rather than as a download, so the PDF opens in a browser tab where the
        // print button is one click away.
        [HttpGet("{bookingId:guid}/documents/{document}")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> GetDocument(Guid bookingId, string document, CancellationToken cancellationToken)
        {
            PartyDocumentType? type = document.ToLowerInvariant() switch
            {
                "reserved-card" => PartyDocumentType.ReservedCard,
                "food-sheet" => PartyDocumentType.FoodSheet,
                "voucher" => PartyDocumentType.Voucher,
                "feedback-form" => PartyDocumentType.FeedbackForm,
                "pack" => PartyDocumentType.Pack,
                _ => null
            };

            if (type is null)
            {
                return NotFound();
            }

            Result<LocationScope> scope = await _locationContext.GetScopeAsync(User);

            if (!scope.IsSuccess || scope.Data is null || !await HasPartiesAsync(scope.Data))
            {
                return NotFound();
            }

            Result<PartyDocument> result = await _documentService.BuildAsync(scope.Data, bookingId, type.Value, await AuthorisedByAsync(), cancellationToken);

            return Inline(result);
        }

        // The company's saved voucher or feedback form filled in with a made-up party, for checking
        // the box positions on the Printouts page.
        [HttpGet("printouts/{document}/preview")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> GetPreview(string document, CancellationToken cancellationToken)
        {
            PartyArtworkDocument? which = document.ToLowerInvariant() switch
            {
                "voucher" => PartyArtworkDocument.Voucher,
                "feedback-form" => PartyArtworkDocument.FeedbackForm,
                _ => null
            };

            Result<LocationScope> scope = await _locationContext.GetScopeAsync(User);

            if (which is null || !scope.IsSuccess || scope.Data?.CompanyId is not int companyId || !await HasPartiesAsync(scope.Data))
            {
                return NotFound();
            }

            Result<PartyDocument> result = await _documentService.BuildPreviewAsync(scope.Data, companyId, which.Value, await AuthorisedByAsync(), cancellationToken);

            return Inline(result);
        }

        // The voucher is signed by whoever prints it.
        private async Task<string?> AuthorisedByAsync()
        {
            UserProfile? user = await _userManager.GetUserAsync(User);

            return user?.GetName() is { Length: > 0 } name ? name : user?.UserName;
        }

        private IActionResult Inline(Result<PartyDocument> result)
        {
            if (!result.IsSuccess || result.Data is null)
            {
                return NotFound();
            }

            ContentDispositionHeaderValue disposition = new("inline");
            disposition.SetHttpFileName(result.Data.FileName);
            Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();

            return File(result.Data.Pdf, "application/pdf");
        }

        // The pages' RequiresFeature gate, for this endpoint. ICompanyFeatureService's
        // current-user checks read the Blazor authentication state, which a plain HTTP request
        // doesn't have, so the company comes from the scope instead. Admins keep every module,
        // as on the pages.
        private async Task<bool> HasPartiesAsync(LocationScope scope)
        {
            if (scope.IsAdmin)
            {
                return true;
            }

            if (scope.CompanyId is not int companyId)
            {
                return false;
            }

            Result<List<CompanyFeatureState>> features = await _companyFeatures.GetFeaturesAsync(companyId);

            return features.IsSuccess
                && features.Data?.Any(x => x.Info.Feature == CompanyFeature.Parties && x.IsEnabled) == true;
        }
    }
}
