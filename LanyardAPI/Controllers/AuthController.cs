using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Lanyard.Application.Services.Authentication;

namespace Lanyard.App.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<UserProfile> _userManager;
        private readonly JwtTokenService _jwtTokenService;

        public AuthController(UserManager<UserProfile> userManager, JwtTokenService jwtTokenService)
        {
            _userManager = userManager;
            _jwtTokenService = jwtTokenService;
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

            bool result = await _userManager.CheckPasswordAsync(user, dto.Password);
            if (!result)
            {
                return Unauthorized(new { message = "Invalid username or password." });
            }

            string token = await _jwtTokenService.GenerateTokenAsync(user);

            return Ok(new LoginResponseDto
            {
                Token = token,
                Username = user.UserName!
            });
        }
    }
}
