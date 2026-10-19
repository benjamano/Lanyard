using Lanyard.API.Extensions;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Lanyard.API.Controllers;

// Kiosk-facing: how a kiosk gets its own credential. See KioskEnrolmentRoutes for the flow. The
// shared-secret exchange is gone along with the shared secret: kiosks that still call it get a
// 404 and fall back to pairing.
[ApiController]
public class KioskEnrolmentController(IKioskEnrolmentService enrolmentService) : ControllerBase
{
    public const string RateLimitPolicy = "kiosk-enrolment";

    private readonly IKioskEnrolmentService _enrolmentService = enrolmentService;

    // Anonymous on purpose: a kiosk asking to be paired has nothing to sign in with yet. Safe
    // because asking gets it nothing - only a pairing code to show on its own screen. A kiosk
    // gets a credential only after a signed-in Admin of a company types that code in, and only
    // the caller holding the poll token returned here can collect it. Rate limited per IP so the
    // table can't be flooded.
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicy)]
    [HttpPost(KioskEnrolmentRoutes.Requests)]
    public async Task<IActionResult> Start([FromBody] KioskEnrolmentRequestDTO request, CancellationToken cancellationToken)
    {
        Result<KioskEnrolmentStartedDTO> result = await _enrolmentService.StartAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);

        return result.ToActionResult();
    }

    // Anonymous on purpose, as above. Without the poll token it answers 404, exactly as for a
    // request that doesn't exist, and the token is 256 random bits.
    [AllowAnonymous]
    [HttpGet(KioskEnrolmentRoutes.RequestTemplate)]
    public async Task<IActionResult> Poll(Guid requestId, CancellationToken cancellationToken)
    {
        string? pollToken = Request.Headers[KioskEnrolmentRoutes.EnrolmentTokenHeader].ToString();

        Result<KioskEnrolmentStatusDTO> result = await _enrolmentService.PollAsync(requestId, pollToken, cancellationToken);

        if (!result.IsSuccess || result.Data is null)
        {
            return NotFound();
        }

        // Never cached anywhere: the approved answer carries the kiosk's credential.
        Response.Headers.CacheControl = "no-store";

        return Ok(result);
    }
}
