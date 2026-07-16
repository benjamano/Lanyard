using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Lanyard.App.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<UserProfile> _userManager;
        private readonly SignInManager<UserProfile> _signInManager;

        public AuthController(
            UserManager<UserProfile> userManager,
            SignInManager<UserProfile> signInManager)
        {
            _userManager = userManager;
            _signInManager = signInManager;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto dto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            UserProfile? user = await _userManager.FindByNameAsync(dto.Username);
            if (user is null)
            {
                return Unauthorized(new { message = "Invalid username or password." });
            }

            Microsoft.AspNetCore.Identity.SignInResult result = await _signInManager.PasswordSignInAsync(
                user,
                dto.Password,
                dto.RememberMe,
                lockoutOnFailure: false);

            if (!result.Succeeded)
            {
                return Unauthorized(new { message = "Invalid username or password." });
            }

            return Ok(new { message = "Login successful", username = user.UserName });
        }

        [HttpPost("login-form")]
        [Consumes("application/x-www-form-urlencoded")]
        public async Task<IActionResult> LoginForm([FromForm] string username, [FromForm] string password, [FromForm] bool rememberMe = false, [FromForm] string? returnUrl = null)
        {
            UserProfile? user = await _userManager.FindByNameAsync(username);
            if (user is null)
            {
                return Redirect($"/login?error={Uri.EscapeDataString("Invalid username or password")}");
            }

            Microsoft.AspNetCore.Identity.SignInResult result = await _signInManager.PasswordSignInAsync(
                user,
                password,
                rememberMe,
                lockoutOnFailure: false);

            if (!result.Succeeded)
            {
                return Redirect($"/login?error={Uri.EscapeDataString("Invalid username or password")}");
            }

            // Redirect to return URL (only if it is a local path — guards against
            // open-redirect attacks) or default to /.
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return Redirect("/");
        }

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

            // Preserve the page the user was on (validated as a local path) so that
            // re-login can return them there.
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                string loginUrl = $"/login?returnUrl={Uri.EscapeDataString(returnUrl)}";
                return Redirect(loginUrl);
            }

            return Redirect("/login");
        }
    }
}
