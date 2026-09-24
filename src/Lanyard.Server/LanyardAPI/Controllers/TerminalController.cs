using System.Security.Claims;
using System.Text.Encodings.Web;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Lanyard.API.Controllers
{
    // Pairs a tablet as a clock-in terminal by giving it a long-lived HttpOnly cookie. This has to
    // be a controller rather than the Blazor page: a Blazor Server component can't set a cookie on
    // the browser once its circuit is running. The manager starts pairing on the Terminals page,
    // which mints a short-lived pairing code and navigates here with it.
    //
    // Same two-step shape as AuthController.LogoutGet/Logout: the GET only renders a form that posts
    // back, so the state change (creating a terminal, setting its cookie) never happens on a GET a
    // prefetcher or an <img> could trigger. Antiforgery is validated through IAntiforgery directly
    // for the reason documented on AuthController.Logout.
    //
    // Deliberately NOT [ApiController]: these are browser navigations on the tablet, not script
    // calls. Since .NET 10, cookie auth answers an unauthenticated request to an [ApiController]
    // endpoint with a bare 401 instead of redirecting to login - which would leave a manager whose
    // session lapsed mid-pairing staring at a blank page instead of the sign-in form.
    [Route("api/terminal")]
    [Authorize(Roles = "Admin,Manager")]
    public class TerminalController(
        ITerminalEphemeralTokenService tokenService,
        IClockInTerminalService terminalService,
        ICompanyLocationService companyLocationService,
        SignInManager<UserProfile> signInManager,
        IAntiforgery antiforgery,
        ILogger<TerminalController> logger) : ControllerBase
    {
        private const string TerminalsPage = "/manage/rota/terminals";

        private readonly ITerminalEphemeralTokenService _tokenService = tokenService;
        private readonly IClockInTerminalService _terminalService = terminalService;
        private readonly ICompanyLocationService _companyLocationService = companyLocationService;
        private readonly SignInManager<UserProfile> _signInManager = signInManager;
        private readonly IAntiforgery _antiforgery = antiforgery;
        private readonly ILogger<TerminalController> _logger = logger;

        [HttpGet("pair/{code}")]
        public IActionResult PairGet(string code)
        {
            PendingPairing? pairing = _tokenService.PeekPairingCode(code);

            if (pairing is null || pairing.IssuedByUserId != CurrentUserId)
            {
                return Redirect($"{TerminalsPage}?pairing=expired");
            }

            AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
            HtmlEncoder encoder = HtmlEncoder.Default;

            string html = $"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                    <meta charset="utf-8" />
                    <title>Pairing this tablet&hellip;</title>
                    <meta name="robots" content="noindex" />
                </head>
                <body>
                    <form id="pairForm" method="post" action="/api/terminal/pair">
                        <input type="hidden" name="{encoder.Encode(tokens.FormFieldName)}" value="{encoder.Encode(tokens.RequestToken ?? string.Empty)}" />
                        <input type="hidden" name="code" value="{encoder.Encode(code)}" />
                        <noscript>
                            <p>Pairing this tablet as a clock-in terminal.</p>
                            <button type="submit">Continue</button>
                        </noscript>
                    </form>
                    <script>document.getElementById('pairForm').submit();</script>
                </body>
                </html>
                """;

            return Content(html, "text/html; charset=utf-8");
        }

        [EnableRateLimiting("ip-fixed")]
        [HttpPost("pair")]
        public async Task<IActionResult> Pair([FromForm] string? code)
        {
            try
            {
                await _antiforgery.ValidateRequestAsync(HttpContext);
            }
            catch (AntiforgeryValidationException ex)
            {
                _logger.LogWarning("Rejected a terminal pairing POST with an invalid antiforgery token: {Error}", ex.Message);

                return BadRequest("Invalid or missing antiforgery token.");
            }

            string? userId = CurrentUserId;
            PendingPairing? pairing = string.IsNullOrEmpty(code) ? null : _tokenService.ConsumePairingCode(code);

            // The code must have been started by the person completing it - a pairing link sent to
            // someone else, or replayed, goes nowhere.
            if (pairing is null || userId is null || pairing.IssuedByUserId != userId)
            {
                return Redirect($"{TerminalsPage}?pairing=expired");
            }

            if (!User.IsInRole("Admin"))
            {
                Result<bool> member = await _companyLocationService.IsUserMemberOfLocationAsync(userId, pairing.LocationId);

                if (!member.IsSuccess || !member.Data)
                {
                    _logger.LogWarning("User {UserId} tried to pair a terminal at location {LocationId} they don't belong to", userId, pairing.LocationId);

                    return Redirect($"{TerminalsPage}?pairing=forbidden");
                }
            }

            Result<PairedTerminal> paired = await _terminalService.PairAsync(pairing.LocationId, pairing.TerminalName, userId);

            if (!paired.IsSuccess || paired.Data is null)
            {
                _logger.LogWarning("Failed to pair a terminal for {UserId}: {Error}", userId, paired.Error);

                return Redirect($"{TerminalsPage}?pairing=failed");
            }

            Response.Cookies.Append(TerminalCookie.Name, paired.Data.RawToken, TerminalCookie.Options(Request.IsHttps));

            // A shared wall tablet shouldn't stay signed in as the manager who set it up.
            if (pairing.SignOutAfterPairing)
            {
                await _signInManager.SignOutAsync();
            }

            return Redirect("/rota/terminal");
        }

        private string? CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
