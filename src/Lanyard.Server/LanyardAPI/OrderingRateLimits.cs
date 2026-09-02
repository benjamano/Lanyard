using Lanyard.Application.Services.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Lanyard.API
{
    /// <summary>
    /// Rate-limit policy names and partitioning for the public ordering API.
    ///
    /// The app-wide "ip-fixed" policy partitions on the caller's IP, which is right for every
    /// other controller but actively wrong here. Reach proxies ordering requests server-side, so
    /// every customer in every venue arrives from Reach's single IP: a per-IP window would be
    /// shared by the entire customer base rather than protecting against any one of them.
    ///
    /// Reach therefore forwards a per-customer identifier (see <see cref="ClientIdHeaderName"/>)
    /// and these policies partition on that, which restores per-customer limiting. The header is
    /// only trusted because the request already proved it came from Reach by presenting the Reach
    /// shared secret; an unauthenticated caller cannot reach these endpoints at all.
    /// </summary>
    public static class OrderingRateLimits
    {
        public const string ReadPolicy = "ordering-read";
        public const string WritePolicy = "ordering-write";

        /// <summary>
        /// Stripe's webhook. Generous and partitioned by IP, because Stripe does not send the
        /// per-customer header and a throttled retry means a paid order never reaching the
        /// kitchen. Present at all only so a flood cannot be aimed at this endpoint for free.
        /// </summary>
        public const string WebhookPolicy = "ordering-webhook";

        public const int WebhookPermitLimit = 600;

        /// <summary>
        /// Set by Reach to the customer's own address as Reach sees it. Falls back to the
        /// connection IP when absent, which in practice means "Reach itself" - deliberately the
        /// conservative direction, since a missing header should tighten limits, not remove them.
        /// </summary>
        public const string ClientIdHeaderName = "X-Lanyard-Reach-Client-Id";

        /// <summary>
        /// Menu, table resolution, photos, and the status poll. Sized for a customer who loads a
        /// menu with images and then polls every few seconds while their food is made, with
        /// enough headroom that a page refresh or two does not lock them out mid-order.
        /// </summary>
        public const int ReadPermitLimit = 240;

        /// <summary>
        /// Order placement. A customer places one order, occasionally two. This is low enough to
        /// make scripted spam pointless and high enough that a genuine retry after a failed
        /// submission still works.
        /// </summary>
        public const int WritePermitLimit = 10;

        public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        public static string ResolvePartitionKey(HttpContext httpContext)
        {
            string forwarded = httpContext.Request.Headers[ClientIdHeaderName].ToString();

            // The forwarded id is only honoured from Reach itself. Rate limiting runs as
            // middleware, before the controller checks the Reach credential, so an unauthenticated
            // caller hitting this API directly could otherwise send a different client id on every
            // request and never be limited at all - each one landing in its own fresh partition.
            //
            // Checked here rather than trusted, and the fallback is the connection's own address,
            // which the caller cannot choose.
            if (!string.IsNullOrWhiteSpace(forwarded) && IsFromReach(httpContext))
            {
                return forwarded;
            }

            return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }

        private static bool IsFromReach(HttpContext httpContext)
        {
            IReachApiCredentialValidator? validator = httpContext.RequestServices
                .GetService<IReachApiCredentialValidator>();

            // No validator registered means this is not a configured ordering deployment; fall
            // back to the address rather than trusting the header by default.
            return validator is not null && validator.IsAuthorized(httpContext);
        }
    }
}
