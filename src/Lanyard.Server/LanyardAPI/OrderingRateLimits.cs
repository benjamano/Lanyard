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

            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                return forwarded;
            }

            return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }
    }
}
