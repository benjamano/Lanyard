using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;

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

        public AuthController(
            UserManager<UserProfile> userManager,
            SignInManager<UserProfile> signInManager,
            ICompanyLocationService companyLocationService,
            IEmailService emailService,
            ILogger<AuthController> logger)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _companyLocationService = companyLocationService;
            _emailService = emailService;
            _logger = logger;
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
        public async Task<IActionResult> LoginForm([FromForm] string username, [FromForm] string password, [FromForm] bool rememberMe = false, [FromForm] string? returnUrl = null, [FromForm] int? locationId = null)
        {
            SignInAttemptResult attempt = await AttemptPasswordSignInAsync(username, password, rememberMe, locationId);

            switch (attempt.Kind)
            {
                case SignInOutcomeKind.LockedOut:
                    return Redirect($"/login?error={Uri.EscapeDataString("Account temporarily locked due to repeated failed attempts. Try again later.")}");

                case SignInOutcomeKind.RequiresTwoFactor:
                    return Redirect($"/login/verify-2fa{BuildTwoFactorRedirectQuery(rememberMe, returnUrl, locationId)}");

                case SignInOutcomeKind.LocationError:
                    return Redirect($"/login?error={Uri.EscapeDataString(attempt.Error!)}");

                case SignInOutcomeKind.Success:
                    await _signInManager.SignInWithClaimsAsync(attempt.User!, rememberMe, attempt.Claims!);

                    if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                    {
                        return Redirect(returnUrl);
                    }

                    return Redirect("/");

                default:
                    return Redirect($"/login?error={Uri.EscapeDataString("Invalid username or password")}");
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

        [EnableRateLimiting("ip-fixed")]
        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            return Ok(new { message = "Logout successful" });
        }

        [HttpGet("logout")]
        public async Task<IActionResult> LogoutGet([FromQuery] string? returnUrl = null)
        {
            await _signInManager.SignOutAsync();

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                string loginUrl = $"/login?returnUrl={Uri.EscapeDataString(returnUrl)}";
                return Redirect(loginUrl);
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
