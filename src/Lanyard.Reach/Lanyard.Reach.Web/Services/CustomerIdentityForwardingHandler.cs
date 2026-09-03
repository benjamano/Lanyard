using System.Security.Cryptography;
using System.Text;

namespace Lanyard.Reach.Web.Services;

/// <summary>
/// Attaches a per-customer identifier to every call Reach makes to the Lanyard ordering API.
///
/// Without this the ordering rate limits are useless: Reach proxies every customer's request
/// server-side, so the Lanyard server sees one source address for the entire customer base and
/// would partition all of them into a single window - the exact failure the ordering policies
/// exist to prevent.
///
/// A hash rather than the raw address, because the value is only ever used as an opaque
/// partition key. The Lanyard server never needs to reverse it, and this keeps customer IP
/// addresses out of another service's rate-limiter state and logs.
/// </summary>
public class CustomerIdentityForwardingHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    /// <summary>Matches Lanyard.API.OrderingRateLimits.ClientIdHeaderName - the two must agree.</summary>
    public const string ClientIdHeaderName = "X-Lanyard-Reach-Client-Id";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? address = _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

        if (!string.IsNullOrEmpty(address))
        {
            request.Headers.Remove(ClientIdHeaderName);
            request.Headers.Add(ClientIdHeaderName, Fingerprint(address));
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static string Fingerprint(string address) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address)))[..32];
}
