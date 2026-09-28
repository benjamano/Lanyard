using Lanyard.Application.Services.Demo;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Lanyard.API.Controllers
{
    // The demo's "get my branding from my website". Called from the browser (demoBranding.js), not
    // the Blazor circuit, so the logo goes straight to the browser - it's far bigger than a circuit
    // message allows, and it's only ever kept in the visitor's own browser.
    [ApiController]
    [Route("api/demo/branding")]
    [Authorize]
    public class DemoBrandingController(
        IWebsiteBrandingService websiteBranding,
        IDemoGuard demoGuard,
        IOptions<DemoOptions> demoOptions) : ControllerBase
    {
        public sealed record LookupRequest(string? Url);

        // Signed-in demo sessions only: the server fetching a URL someone typed in is exactly what
        // PublicAddressGuard exists for, and nobody else has a reason to use it.
        [HttpPost("lookup")]
        [EnableRateLimiting(RateLimitPolicy)]
        public async Task<IActionResult> Lookup([FromBody] LookupRequest request, CancellationToken ct)
        {
            if (!demoOptions.Value.Enabled || !await demoGuard.IsDemoSessionAsync())
            {
                return NotFound();
            }

            Result<WebsiteBranding> result = await websiteBranding.LookupAsync(request.Url ?? string.Empty, ct);

            return result.IsSuccess
                ? Ok(new { name = result.Data!.Name, color = result.Data.ColorHex, logo = result.Data.LogoDataUrl, site = result.Data.SiteUrl })
                : BadRequest(new { message = result.Error });
        }

        public const string RateLimitPolicy = "demo-website-lookup";
    }
}
