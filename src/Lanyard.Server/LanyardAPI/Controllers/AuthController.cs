using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Lanyard.App.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<UserProfile> _userManager;
        private readonly SignInManager<UserProfile> _signInManager;
        private readonly ICompanyLocationService _companyLocationService;
        private readonly IEmailService _emailService;
        private readonly ILogger<AuthController> _logger;
        private readonly IAntiforgery _antiforgery;

        public AuthController(
            UserManager<UserProfile> userManager,
            SignInManager<UserProfile> signInManager,
            ICompanyLocationService companyLocationService,
            IEmailService emailService,
            ILogger<AuthController> logger,
            IAntiforgery antiforgery)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _companyLocationService = companyLocationService;
            _emailService = emailService;
            _logger = logger;
            _antiforgery = antiforgery;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto dto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            SignInAttemptResult attempt = await AttemptPasswordSignInAsync(dto.Username, dto.Password, dto.RememberMe, dto.LocationId);

            switch (attempt.Kind)
            {
                case SignInOutcomeKind.LockedOut:
                    return Unauthorized(new { message = "Account temporarily locked due to repeated failed attempts. Try again later." });

                case SignInOutcomeKind.RequiresTwoFactor:
                    return Unauthorized(new { message = "Two-factor authentication is required.", requiresTwoFactor = true });

                case SignInOutcomeKind.LocationError:
                    return Unauthorized(new { message = attempt.Error });

                case SignInOutcomeKind.Success:
                    await _signInManager.SignInWithClaimsAsync(attempt.User!, dto.RememberMe, attempt.Claims!);
                    return Ok(new { message = "Login successful", username = attempt.User!.UserName });

                default:
                    return Unauthorized(new { message = "Invalid username or password." });
            }
        }

        [HttpPost("login-form")]
        [Consumes("application/x-www-form-urlencoded")]
        public async Task<IActionResult> LoginForm([FromForm] string username, [FromForm] string password, [FromForm] bool rememberMe = false, [FromForm] string? returnUrl = null, [FromForm] int? locationId = null, [FromForm] int? company = null)
        {
            SignInAttemptResult attempt = await AttemptPasswordSignInAsync(username, password, rememberMe, locationId);

            switch (attempt.Kind)
            {
                case SignInOutcomeKind.LockedOut:
                    return Redirect(BuildLoginErrorRedirect("Account temporarily locked due to repeated failed attempts. Try again later.", company));

                case SignInOutcomeKind.RequiresTwoFactor:
                    return Redirect($"/login/verify-2fa{BuildTwoFactorRedirectQuery(rememberMe, returnUrl, locationId)}");

                case SignInOutcomeKind.LocationError:
                    return Redirect(BuildLoginErrorRedirect(attempt.Error!, company));

                case SignInOutcomeKind.Success:
                    await _signInManager.SignInWithClaimsAsync(attempt.User!, rememberMe, attempt.Claims!);

                    if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                    {
                        return Redirect(returnUrl);
                    }

                    return Redirect("/");

                default:
                    return Redirect(BuildLoginErrorRedirect("Invalid username or password", company));
            }
        }

        [EnableRateLimiting("ip-fixed")]
        [HttpPost("verify-2fa-form")]
        [Consumes("application/x-www-form-urlencoded")]
        public async Task<IActionResult> VerifyTwoFactorForm(
            [FromForm] string code,
            [FromForm] string provider,
            [FromForm] string? rememberMachine = null,
            [FromForm] bool rememberMe = false,
            [FromForm] string? returnUrl = null,
            [FromForm] int? locationId = null)
        {
            // FluentCheckbox posts as a native HTML checkbox: present with browser-default value "on"
            // when checked, and absent entirely when unchecked - not a "true"/"false" bool the model
            // binder understands, so bind it as a string and use standard checkbox presence semantics.
            bool rememberMachineChecked = !string.IsNullOrEmpty(rememberMachine);

            UserProfile? user = await _signInManager.GetTwoFactorAuthenticationUserAsync();

            if (user is null)
            {
                return Redirect($"/login?error={Uri.EscapeDataString("Your session expired. Please log in again.")}");
            }

            bool isValid = provider == "RecoveryCode"
                ? (await _userManager.RedeemTwoFactorRecoveryCodeAsync(user, code)).Succeeded
                : await _userManager.VerifyTwoFactorTokenAsync(user, provider, code);

            if (!isValid)
            {
                await _userManager.AccessFailedAsync(user);

                string message = await _userManager.IsLockedOutAsync(user)
                    ? "Account temporarily locked due to repeated failed attempts. Try again later."
                    : "Invalid or expired code.";

                string query = BuildTwoFactorRedirectQuery(rememberMe, returnUrl, locationId);
                return Redirect($"/login/verify-2fa{query}&error={Uri.EscapeDataString(message)}");
            }

            await _userManager.ResetAccessFailedCountAsync(user);

            List<Claim> extraClaims = [];
            (bool locationOk, string? locationError) = await ValidateAndBuildLocationClaimsAsync(user, locationId, extraClaims);

            if (!locationOk)
            {
                await _signInManager.SignOutAsync();
                return Redirect($"/login?error={Uri.EscapeDataString(locationError!)}");
            }

            if (rememberMachineChecked)
            {
                await _signInManager.RememberTwoFactorClientAsync(user);
            }

            await _signInManager.SignInWithClaimsAsync(user, rememberMe, extraClaims);
            await HttpContext.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return Redirect("/");
        }

        [EnableRateLimiting("ip-fixed")]
        [HttpPost("resend-2fa-code")]
        [Consumes("application/x-www-form-urlencoded")]
        public async Task<IActionResult> ResendTwoFactorCodeForm(
            [FromForm] bool rememberMe = false,
            [FromForm] string? returnUrl = null,
            [FromForm] int? locationId = null)
        {
            UserProfile? user = await _signInManager.GetTwoFactorAuthenticationUserAsync();

            if (user is null)
            {
                return Redirect($"/login?error={Uri.EscapeDataString("Your session expired. Please log in again.")}");
            }

            string code = await _userManager.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider);
            Result<bool> sendResult = await _emailService.SendTwoFactorCodeEmailAsync(user, code);

            string query = BuildTwoFactorRedirectQuery(rememberMe, returnUrl, locationId);

            if (!sendResult.IsSuccess)
            {
                _logger.LogWarning("Failed to send 2FA code email to {UserId}: {Error}", user.Id, sendResult.Error);
                return Redirect($"/login/verify-2fa{query}&error={Uri.EscapeDataString("Couldn't send the code. Try again.")}");
            }

            return Redirect($"/login/verify-2fa{query}&sent=true");
        }

        // Signing out is a state change, so it must not happen on a GET. The kiosk text widget
        // renders stored rich text through Ganss.Xss, whose default allow-list permits <img> - so
        // before this, an <img src="/api/auth/logout"> saved into a widget would sign out every
        // person who looked at that dashboard. Link prefetchers and mail scanners hitting the same
        // URL had the same effect. SameSite=Lax stops the cross-site version of that, but not the
        // same-origin one, which is the one that actually applied here.
        // Validated through IAntiforgery directly rather than with [ValidateAntiForgeryToken]:
        // that attribute resolves ValidateAntiforgeryTokenAuthorizationFilter out of DI, and this
        // app calls AddControllers() rather than AddControllersWithViews(), so the MVC
        // ViewFeatures services backing it are never registered - the attribute throws
        // "No service for type ... ValidateAntiforgeryTokenAuthorizationFilter" at request time
        // instead of validating anything.
        [EnableRateLimiting("ip-fixed")]
        [HttpPost("logout")]
        public async Task<IActionResult> Logout([FromForm] string? returnUrl = null)
        {
            try
            {
                await _antiforgery.ValidateRequestAsync(HttpContext);
            }
            catch (AntiforgeryValidationException ex)
            {
                _logger.LogWarning("Rejected a sign-out POST with an invalid antiforgery token: {Error}", ex.Message);

                return BadRequest("Invalid or missing antiforgery token.");
            }

            await _signInManager.SignOutAsync();

            return RedirectToLoginAfterSignOut(returnUrl);
        }

        // Deliberately does NOT sign anyone out. It renders a form that posts back to the action
        // above, so the sign-out only happens once something has actually submitted it. An <img>
        // tag pointing here just receives HTML it can't render, and a prefetcher fetches the markup
        // without running the script - neither ends a session.
        //
        // Kept as a GET endpoint rather than deleted because every caller navigates here by URL
        // (the nav items, /logout, and the auto-logout timer via /HandleLogout), and a browser
        // navigation is a GET. The redirect is the same one the POST performs, so from the user's
        // side the extra hop is invisible.
        [HttpGet("logout")]
        public IActionResult LogoutGet([FromQuery] string? returnUrl = null)
        {
            AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);

            string safeReturnUrl = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
                ? returnUrl
                : string.Empty;

            HtmlEncoder encoder = HtmlEncoder.Default;

            // The <noscript> submit button is the fallback: without it, a browser with scripting
            // disabled would sit on this page with no way forward.
            string html = $"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                    <meta charset="utf-8" />
                    <title>Signing you out&hellip;</title>
                    <meta name="robots" content="noindex" />
                </head>
                <body>
                    <form id="signOutForm" method="post" action="/api/auth/logout">
                        <input type="hidden" name="{encoder.Encode(tokens.FormFieldName)}" value="{encoder.Encode(tokens.RequestToken ?? string.Empty)}" />
                        <input type="hidden" name="returnUrl" value="{encoder.Encode(safeReturnUrl)}" />
                        <noscript>
                            <p>Signing you out.</p>
                            <button type="submit">Continue</button>
                        </noscript>
                    </form>
                    <script>document.getElementById('signOutForm').submit();</script>
                </body>
                </html>
                """;

            return Content(html, "text/html; charset=utf-8");
        }

        private IActionResult RedirectToLoginAfterSignOut(string? returnUrl)
        {
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            return Redirect("/login");
        }

        private enum SignInOutcomeKind
        {
            InvalidCredentials,
            LockedOut,
            RequiresTwoFactor,
            LocationError,
            Success
        }

        private record SignInAttemptResult(SignInOutcomeKind Kind, UserProfile? User = null, string? Error = null, List<Claim>? Claims = null);

        private async Task<SignInAttemptResult> AttemptPasswordSignInAsync(string username, string password, bool rememberMe, int? locationId)
        {
            UserProfile? user = await FindUserByUsernameOrEmailAsync(username);

            if (user is null)
            {
                return new SignInAttemptResult(SignInOutcomeKind.InvalidCredentials);
            }

            Microsoft.AspNetCore.Identity.SignInResult result = await _signInManager.PasswordSignInAsync(
                user,
                password,
                rememberMe,
                lockoutOnFailure: true);

            if (result.IsLockedOut)
            {
                return new SignInAttemptResult(SignInOutcomeKind.LockedOut);
            }

            if (result.RequiresTwoFactor)
            {
                return new SignInAttemptResult(SignInOutcomeKind.RequiresTwoFactor, user);
            }

            if (!result.Succeeded)
            {
                return new SignInAttemptResult(SignInOutcomeKind.InvalidCredentials);
            }

            List<Claim> extraClaims = [];
            (bool locationOk, string? locationError) = await ValidateAndBuildLocationClaimsAsync(user, locationId, extraClaims);

            if (!locationOk)
            {
                await _signInManager.SignOutAsync();
                return new SignInAttemptResult(SignInOutcomeKind.LocationError, user, locationError);
            }

            return new SignInAttemptResult(SignInOutcomeKind.Success, user, Claims: extraClaims);
        }

        private static string BuildTwoFactorRedirectQuery(bool rememberMe, string? returnUrl, int? locationId)
        {
            List<string> parts = [$"rememberMe={(rememberMe ? "true" : "false")}"];

            if (!string.IsNullOrEmpty(returnUrl))
            {
                parts.Add($"returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            if (locationId.HasValue)
            {
                parts.Add($"locationId={locationId.Value}");
            }

            return "?" + string.Join("&", parts);
        }

        private static string BuildLoginErrorRedirect(string error, int? companyId)
        {
            string query = $"error={Uri.EscapeDataString(error)}";

            if (companyId.HasValue)
            {
                query += $"&company={companyId.Value}";
            }

            return $"/login?{query}";
        }

        private async Task<(bool ok, string? error)> ValidateAndBuildLocationClaimsAsync(UserProfile user, int? locationId, List<Claim> claims)
        {
            bool isAdmin = await _userManager.IsInRoleAsync(user, "Admin");

            if (locationId is null)
            {
                return isAdmin ? (true, null) : (false, "Please select your location.");
            }

            if (isAdmin)
            {
                Result<List<LoginLocationOption>> optionsResult = await _companyLocationService.GetLoginLocationOptionsAsync();

                if (!optionsResult.IsSuccess || optionsResult.Data!.All(x => x.LocationId != locationId.Value))
                {
                    return (false, "The selected location is no longer available.");
                }

                claims.Add(new Claim(LocationClaimTypes.LocationId, locationId.Value.ToString()));

                return (true, null);
            }

            Result<bool> membershipResult = await _companyLocationService.IsUserMemberOfLocationAsync(user.Id, locationId.Value);

            if (!membershipResult.IsSuccess || membershipResult.Data != true)
            {
                return (false, "You do not have access to the selected location.");
            }

            claims.Add(new Claim(LocationClaimTypes.LocationId, locationId.Value.ToString()));

            return (true, null);
        }

        private async Task<UserProfile?> FindUserByUsernameOrEmailAsync(string identifier)
        {
            return await _userManager.FindByNameAsync(identifier)
                ?? await _userManager.FindByEmailAsync(identifier);
        }
    }
}
