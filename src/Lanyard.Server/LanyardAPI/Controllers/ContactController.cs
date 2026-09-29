using System.Threading.RateLimiting;
using Lanyard.API.Contact;
using Lanyard.Application.Services.Email;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lanyard.API.Controllers
{
    // The public homepage's contact form (Public/Sections/ContactForm). Anyone can post to it, so in
    // order it: only takes JSON (a cross-site form can't send that without a CORS preflight, which is
    // never allowed); is rate-limited per address (the contact-form policy); needs the signed stamp the
    // form renders with, and drops a form filled in faster than a person could (ContactFormToken);
    // drops anything that fills in the hidden honeypot field; and stops emailing after a site-wide
    // daily cap, so even many addresses together can't flood the inbox.
    [ApiController]
    [Route("api/contact")]
    [AllowAnonymous]
    public class ContactController(
        IEmailService emailService,
        IDataProtectionProvider dataProtection,
        IConfiguration configuration,
        ILogger<ContactController> logger,
        TimeProvider? timeProvider = null) : ControllerBase
    {
        public const string RateLimitPolicy = "contact-form";

        // Far more real enquiries than a small business gets in a day; past it, visitors are asked to
        // use the email address instead. Shared by the whole process (one Railway instance).
        public const int DailyLimit = 40;

        private static readonly FixedWindowRateLimiter DailyEmails = new(new FixedWindowRateLimiterOptions
        {
            PermitLimit = DailyLimit,
            Window = TimeSpan.FromDays(1),
            QueueLimit = 0,
        });

        // Website is the honeypot: hidden from people, so only a bot fills it in. Token is the
        // form's ContactFormToken.
        public sealed record ContactRequest(string? Name, string? Email, string? Venue, string? Message, string? Website, string? Token);

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

            ContactFormToken.Verdict verdict = ContactFormToken.Check(dataProtection, request.Token, (timeProvider ?? TimeProvider.System).GetUtcNow());

            if (verdict is ContactFormToken.Verdict.Invalid or ContactFormToken.Verdict.Expired)
            {
                logger.LogInformation("Contact form: refused a submission with a {Verdict} form stamp", verdict);
                return BadRequest(new { message = "This page has been open a long time - please reload it and send your message again." });
            }

            // Both look sent to the bot, so it has no reason to try again.
            if (verdict == ContactFormToken.Verdict.TooFast || !string.IsNullOrEmpty(request.Website))
            {
                logger.LogInformation("Contact form: ignored a likely bot submission ({Reason})",
                    verdict == ContactFormToken.Verdict.TooFast ? "filled in too fast" : "honeypot filled in");
                return Ok();
            }

            (ContactEnquiry? enquiry, string? error) = ContactEnquiry.Create(request.Name, request.Email, request.Venue, request.Message);

            if (enquiry is null)
            {
                return BadRequest(new { message = error });
            }

            using RateLimitLease lease = DailyEmails.AttemptAcquire();

            if (!lease.IsAcquired)
            {
                logger.LogWarning("Contact form: the daily limit of {DailyLimit} emails is used up", DailyLimit);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = $"The contact form is busy right now. Please email {to} instead." });
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
