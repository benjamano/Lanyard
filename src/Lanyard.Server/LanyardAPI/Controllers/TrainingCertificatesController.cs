using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Lanyard.API.Controllers
{
    [ApiController]
    [Route("api/training")]
    [EnableRateLimiting("ip-fixed")]
    public class TrainingCertificatesController : ControllerBase
    {
        private readonly ICertificateService _certificateService;
        private readonly UserManager<UserProfile> _userManager;

        public TrainingCertificatesController(ICertificateService certificateService, UserManager<UserProfile> userManager)
        {
            _certificateService = certificateService;
            _userManager = userManager;
        }

        // Cookie auth only, deliberately not ClientRequestAuthorization: that helper exists
        // to let kiosk clients through on a shared secret, which has no business fetching
        // somebody's personal training record. The ownership check itself lives in the
        // service, so this endpoint can only ever return the caller's own certificate.
        [HttpGet("assignments/{assignmentId:guid}/certificate")]
        [Authorize]
        public async Task<IActionResult> GetCertificate(Guid assignmentId, CancellationToken cancellationToken)
        {
            string? userId = _userManager.GetUserId(User);

            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            Result<byte[]> result = await _certificateService.GenerateCertificatePdfAsync(assignmentId, userId, cancellationToken);

            // NotFound rather than the Result's error text for every failure - "not found",
            // "not yours", and "not finished yet" are deliberately indistinguishable here,
            // matching the don't-leak-details posture of the company branding endpoints.
            if (!result.IsSuccess || result.Data is null)
            {
                return NotFound();
            }

            return File(result.Data, "application/pdf", "Training Certificate.pdf");
        }
    }
}
