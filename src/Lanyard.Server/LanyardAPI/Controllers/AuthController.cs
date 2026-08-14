using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
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

        public AuthController(
            UserManager<UserProfile> userManager,
            SignInManager<UserProfile> signInManager,
            ICompanyLocationService companyLocationService)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _companyLocationService = companyLocationService;
        }

        [EnableRateLimiting("ip-fixed")]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto dto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            UserProfile? user = await FindUserByUsernameOrEmailAsync(dto.Username);
            if (user is null)
            {
                return Unauthorized(new { message = "Invalid username or password." });
            }

            Microsoft.AspNetCore.Identity.SignInResult result = await _signInManager.PasswordSignInAsync(
                user,
                dto.Password,
                dto.RememberMe,
                lockoutOnFailure: true);

            if (result.IsLockedOut)
            {
                return Unauthorized(new { message = "Account temporarily locked due to repeated failed attempts. Try again later." });
            }

            if (!result.Succeeded)
            {
                return Unauthorized(new { message = "Invalid username or password." });
            }

            List<Claim> extraClaims = [];
            (bool locationOk, string? locationError) = await ValidateAndBuildLocationClaimsAsync(user, dto.LocationId, extraClaims);

            if (!locationOk)
            {
                await _signInManager.SignOutAsync();
                return Unauthorized(new { message = locationError });
            }

            await _signInManager.SignInWithClaimsAsync(user, dto.RememberMe, extraClaims);

            return Ok(new { message = "Login successful", username = user.UserName });
        }

        [EnableRateLimiting("ip-fixed")]
        [HttpPost("login-form")]
        [Consumes("application/x-www-form-urlencoded")]
        public async Task<IActionResult> LoginForm([FromForm] string username, [FromForm] string password, [FromForm] bool rememberMe = false, [FromForm] string? returnUrl = null, [FromForm] int? locationId = null)
        {
            UserProfile? user = await FindUserByUsernameOrEmailAsync(username);
            if (user is null)
            {
                return Redirect($"/login?error={Uri.EscapeDataString("Invalid username or password")}");
            }

            Microsoft.AspNetCore.Identity.SignInResult result = await _signInManager.PasswordSignInAsync(
                user,
                password,
                rememberMe,
                lockoutOnFailure: true);

            if (result.IsLockedOut)
            {
                return Redirect($"/login?error={Uri.EscapeDataString("Account temporarily locked due to repeated failed attempts. Try again later.")}");
            }

            if (!result.Succeeded)
            {
                return Redirect($"/login?error={Uri.EscapeDataString("Invalid username or password")}");
            }

            List<Claim> extraClaims = [];
            (bool locationOk, string? locationError) = await ValidateAndBuildLocationClaimsAsync(user, locationId, extraClaims);

            if (!locationOk)
            {
                await _signInManager.SignOutAsync();
                return Redirect($"/login?error={Uri.EscapeDataString(locationError!)}");
            }

            await _signInManager.SignInWithClaimsAsync(user, rememberMe, extraClaims);

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return Redirect("/");
        }

        [EnableRateLimiting("ip-fixed")]
        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            return Ok(new { message = "Logout successful" });
        }

        [EnableRateLimiting("ip-fixed")]
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
