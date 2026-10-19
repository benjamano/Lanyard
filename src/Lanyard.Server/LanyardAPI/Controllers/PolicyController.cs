using Lanyard.API.Extensions;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Notifications;
using Lanyard.Application.Services.Policies;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Policies;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Antiforgery;
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
        private readonly IPolicyAcknowledgementService _acknowledgementService;
        private readonly IAntiforgery _antiforgery;
        private readonly UserManager<UserProfile> _userManager;
        private readonly ILogger<PolicyController> _logger;

        public PolicyController(
            IPolicyService policyService,
            IPolicyAcknowledgementService acknowledgementService,
            IAntiforgery antiforgery,
            UserManager<UserProfile> userManager,
            ILogger<PolicyController> logger)
        {
            _policyService = policyService;
            _acknowledgementService = acknowledgementService;
            _antiforgery = antiforgery;
            _userManager = userManager;
            _logger = logger;
        }

        // A request token for the acknowledge POST below (the matching cookie is set here too).
        // The page runs as an interactive Blazor circuit, which has no form to carry one.
        [HttpGet("antiforgery")]
        public IActionResult GetAntiforgeryToken()
        {
            AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
            Response.Headers.CacheControl = "no-store";

            return Ok(new { headerName = tokens.HeaderName, token = tokens.RequestToken });
        }

        // Records that the signed-in person has read a version. Done as a plain HTTP request rather
        // than from the Blazor circuit so the IP address and browser are the ones on this request -
        // a circuit has no request of its own once the page is running. Antiforgery is validated
        // through IAntiforgery directly, for the reason given on AuthController.Logout.
        [HttpPost("versions/{versionId:guid}/acknowledge")]
        public async Task<IActionResult> Acknowledge(Guid versionId, CancellationToken cancellationToken)
        {
            try
            {
                await _antiforgery.ValidateRequestAsync(HttpContext);
            }
            catch (AntiforgeryValidationException ex)
            {
                _logger.LogWarning("Rejected a policy acknowledgement with an invalid antiforgery token: {Error}", ex.Message);
                return BadRequest(new { error = "Your session has expired. Please reload the page and try again." });
            }

            string? userId = _userManager.GetUserId(User);

            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            string? userAgent = Request.Headers.UserAgent.ToString();
            string deviceLabel = DeviceDescriptor.Describe(new DeviceReport(userAgent, 0, false)).Label;

            PolicyAcknowledgementEvidence evidence = new(HttpContext.Connection.RemoteIpAddress?.ToString(), userAgent, deviceLabel);

            Result<PolicyAcknowledgementResult> result = await _acknowledgementService.AcknowledgeAsync(versionId, userId, evidence, cancellationToken);

            if (!result.IsSuccess || result.Data is null)
            {
                return BadRequest(new { error = result.Error ?? "Your acknowledgement couldn't be recorded." });
            }

            return Ok(new
            {
                versionNumber = result.Data.VersionNumber,
                acknowledgedAtUtc = result.Data.AcknowledgedAtUtc,
                alreadyAcknowledged = result.Data.AlreadyAcknowledged
            });
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
