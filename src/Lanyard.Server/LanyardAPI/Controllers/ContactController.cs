using Lanyard.Application.Services.Email;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lanyard.API.Controllers
{
    // The public homepage's contact form (Public/Sections/ContactForm). Anyone can post to it, so it's
    // rate-limited per address, ignores anything that fills in the hidden honeypot field, and only
    // takes JSON - a cross-site form can't send that without a CORS preflight, which is never allowed.
    [ApiController]
    [Route("api/contact")]
    [AllowAnonymous]
    public class ContactController(
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<ContactController> logger) : ControllerBase
    {
        public const string RateLimitPolicy = "contact-form";

        // Website is the honeypot: hidden from people, so only a bot fills it in.
        public sealed record ContactRequest(string? Name, string? Email, string? Venue, string? Message, string? Website);

        [HttpPost]
        [EnableRateLimiting(RateLimitPolicy)]
        public async Task<IActionResult> Send([FromBody] ContactRequest request)
        {
            // Same setting as the homepage's "Get in touch" section, which isn't shown without it.
            string to = configuration["PublicSite:ContactEmail"]?.Trim() ?? string.Empty;

            if (!to.Contains('@'))
            {
                return NotFound();
            }

            // Looks sent to the bot, so it has no reason to try again.
            if (!string.IsNullOrEmpty(request.Website))
            {
                logger.LogInformation("Contact form: ignored a submission that filled in the honeypot field");
                return Ok();
            }

            (ContactEnquiry? enquiry, string? error) = ContactEnquiry.Create(request.Name, request.Email, request.Venue, request.Message);

            if (enquiry is null)
            {
                return BadRequest(new { message = error });
            }

            Result<bool> result = await emailService.SendContactEnquiryEmailAsync(to, enquiry);

            if (!result.IsSuccess)
            {
                logger.LogWarning("Contact form enquiry couldn't be emailed: {Error}", result.Error);
                return StatusCode(StatusCodes.Status502BadGateway, new { message = $"Your message couldn't be sent just now. Please email {to} instead." });
            }

            logger.LogInformation("Contact form enquiry emailed to the contact address");
            return Ok();
        }
    }
}
