using Lanyard.API.Extensions;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Policies;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Lanyard.API.Controllers
{
    // Serves policy PDFs. Cookie auth only, deliberately not ClientRequestAuthorization: that helper
    // lets kiosks in on a shared secret as the unfiltered system tenant, which must never reach a
    // policy (Security Issue 9.1). Who may read which version is decided in PolicyService, and every
    // refusal is a bare 404 so "not yours" and "doesn't exist" look the same.
    [ApiController]
    [Route("api/policies")]
    [Authorize]
    public class PolicyController : ControllerBase
    {
        private readonly IPolicyService _policyService;
        private readonly UserManager<UserProfile> _userManager;

        public PolicyController(IPolicyService policyService, UserManager<UserProfile> userManager)
        {
            _policyService = policyService;
            _userManager = userManager;
        }

        [HttpGet("versions/{versionId:guid}/file")]
        public async Task<IActionResult> GetVersionFile(Guid versionId, CancellationToken cancellationToken)
        {
            string? userId = _userManager.GetUserId(User);

            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            Result<FileContent> result = await _policyService.OpenVersionFileAsync(
                versionId, userId, ScopeFromClaims(), FileContentStreamingExtensions.ParseRequestedRange(Request), cancellationToken);

            if (!result.IsSuccess || result.Data is null)
            {
                return this.FileContentFailure(result.Error);
            }

            // Shown in the browser's own PDF viewer, never kept in a shared or disk cache: the
            // file is only for the people the policy is assigned to.
            ContentDispositionHeaderValue disposition = new("inline");
            disposition.SetHttpFileName(result.Data.Metadata.FileName ?? "policy.pdf");
            Response.Headers.ContentDisposition = disposition.ToString();
            Response.Headers.CacheControl = "private, no-store";
            Response.Headers["X-Content-Type-Options"] = "nosniff";

            return await this.StreamFileContentAsync(result.Data, contentType: "application/pdf");
        }

        // The Blazor ICurrentLocationContext reads AuthenticationStateProvider, which isn't set up
        // for a plain HTTP request, so the scope comes straight from the sign-in cookie's claims.
        // Company isolation doesn't rest on this: the tenant filter pins every query to the
        // caller's company.
        private LocationScope ScopeFromClaims()
        {
            int? locationId = int.TryParse(User.FindFirst(LocationClaimTypes.LocationId)?.Value, out int parsed) ? parsed : null;

            return new LocationScope(User.IsInRole("Admin"), locationId, null, null, User.IsInRole("Manager"));
        }
    }
}
