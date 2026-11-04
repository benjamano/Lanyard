using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Lanyard.API.Controllers
{
    // The private iCal feed a person subscribes to from Google, Apple or Outlook calendar (created on
    // My Shifts / Account Management - see CalendarFeedService).
    //
    // Anonymous on purpose: calendar apps fetch the URL from their own servers and can't sign in, so
    // the unguessable token in the path is the credential. That's safe because:
    //  - the token is 256 random bits, stored only as a SHA-256 hash, and revocable/regenerable;
    //  - it can only ever return the one person's own published shifts at the one company the token
    //    was made for - CalendarFeedService pins every query to the token's UserId and CompanyId
    //    rather than relying on the tenant filter, which is off for anonymous requests;
    //  - every failure is a bare 404 (unknown, revoked and malformed tokens look the same), and an
    //    address that keeps guessing gets 429s before it's even looked up.
    // The token is never logged here; Railway's edge request log does record the path, which is
    // why the feed can be revoked at any time.
    //
    // Not [ApiController]: calendar apps want a text/calendar body or a status, never a problem-details
    // JSON response.
    [Route("api/calendar")]
    [AllowAnonymous]
    public class CalendarFeedController(
        ICalendarFeedService feedService,
        ILogger<CalendarFeedController> logger) : ControllerBase
    {
        public const string RateLimitPolicy = "calendar-feed";

        // Wrong tokens per address before it gets 429s instead of lookups. Real calendar apps never
        // send wrong tokens except after a revoke, so this only ever bites a guesser.
        private static readonly PartitionedRateLimiter<string> FailedLookups =
            PartitionedRateLimiter.Create<string, string>(ip => RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

        private readonly ICalendarFeedService _feedService = feedService;
        private readonly ILogger<CalendarFeedController> _logger = logger;

        // The rate-limit policy is per feed (partitioned on the token's hash), not per address:
        // Google and Apple fetch every subscriber's feed from a shared pool of addresses.
        public static string PartitionKey(HttpContext httpContext)
        {
            string token = httpContext.GetRouteValue("token") as string ?? string.Empty;

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];
        }

        [AcceptVerbs("GET", "HEAD", Route = "{token}.ics")]
        [EnableRateLimiting(RateLimitPolicy)]
        public async Task<IActionResult> GetFeed(string token)
        {
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (FailedLookups.GetStatistics(ip)?.CurrentAvailablePermits == 0)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests);
            }

            string baseUrl = $"{Request.Scheme}://{Request.Host}";

            Result<CalendarFeedDocument> result = await _feedService.BuildFeedAsync(token, baseUrl);

            Response.Headers[HeaderNames.CacheControl] = "private, no-transform, max-age=900";
            Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            Response.Headers["Referrer-Policy"] = "no-referrer";

            if (!result.IsSuccess || result.Data is null)
            {
                if (result.Error == CalendarFeedService.NotFoundError)
                {
                    using RateLimitLease _ = FailedLookups.AttemptAcquire(ip);
                    return NotFound();
                }

                // A database hiccup mustn't look like "this calendar is gone" - some apps drop a
                // subscription that 404s - so it's a retryable 503.
                _logger.LogWarning("Failed to build a calendar feed: {Error}", result.Error);
                Response.Headers[HeaderNames.RetryAfter] = "300";
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            Response.Headers[HeaderNames.ETag] = result.Data.ETag;

            IList<EntityTagHeaderValue> ifNoneMatch = Request.GetTypedHeaders().IfNoneMatch;

            if (ifNoneMatch.Any(x => x.Tag == result.Data.ETag || x.Equals(EntityTagHeaderValue.Any)))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            Response.Headers[HeaderNames.ContentDisposition] = "inline; filename=\"shifts.ics\"";

            return Content(result.Data.Body, "text/calendar; charset=utf-8");
        }
    }
}
