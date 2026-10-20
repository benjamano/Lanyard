using Lanyard.API.Extensions;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.StaffDocuments;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Lanyard.API.Controllers
{
    // Serves staff documents (right-to-work, ID, certificates). Cookie auth only, deliberately not
    // KioskRequestAuthorization: a kiosk has no business reading someone's HR file, and
    // api/files/download no longer reaches these files at all (Security Issue 9.2). Who may read
    // which document is decided in StaffDocumentService, and every refusal is a bare 404 so
    // "not yours" and "doesn't exist" look the same.
    [ApiController]
    [Route("api/staff-documents")]
    [Authorize]
    public class StaffDocumentsController : ControllerBase
    {
        private readonly IStaffDocumentService _staffDocumentService;
        private readonly UserManager<UserProfile> _userManager;

        public StaffDocumentsController(IStaffDocumentService staffDocumentService, UserManager<UserProfile> userManager)
        {
            _staffDocumentService = staffDocumentService;
            _userManager = userManager;
        }

        [HttpGet("{staffDocumentId:guid}/file")]
        public async Task<IActionResult> GetFile(Guid staffDocumentId, CancellationToken cancellationToken)
        {
            string? userId = _userManager.GetUserId(User);

            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            Result<FileContent> result = await _staffDocumentService.OpenStaffDocumentFileAsync(
                staffDocumentId, userId, ScopeFromClaims(), FileContentStreamingExtensions.ParseRequestedRange(Request), cancellationToken);

            if (!result.IsSuccess || result.Data is null)
            {
                return this.FileContentFailure(result.Error);
            }

            Response.Headers.CacheControl = "private, no-store";
            Response.Headers["X-Content-Type-Options"] = "nosniff";

            return await this.StreamFileContentAsync(result.Data, downloadFileName: result.Data.Metadata.FileName ?? "document.bin");
        }

        // The Blazor ICurrentLocationContext reads AuthenticationStateProvider, which isn't set up
        // for a plain HTTP request, so the scope comes straight from the sign-in cookie's claims.
        // The company claim matters: a Manager may only open documents for members of a location
        // in the company they signed in under.
        private LocationScope ScopeFromClaims()
        {
            int? locationId = int.TryParse(User.FindFirst(LocationClaimTypes.LocationId)?.Value, out int parsedLocation) ? parsedLocation : null;
            int? companyId = int.TryParse(User.FindFirst(LocationClaimTypes.CompanyId)?.Value, out int parsedCompany) ? parsedCompany : null;

            return new LocationScope(User.IsInRole("Admin"), locationId, companyId, null, User.IsInRole("Manager"));
        }
    }
}
