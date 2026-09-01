using Lanyard.Shared.DTO;
using Microsoft.Extensions.Caching.Memory;

namespace Lanyard.Reach.Web.Services;

/// <summary>
/// Resolves which customer's site a request is for, from the hostname the visitor asked for.
///
/// This runs on every request, including static assets, so the hostname -> tenant mapping is
/// cached in memory for a short period. The TTL is short rather than absent because onboarding a
/// new domain should take effect without a restart - the whole point of keeping tenants in a
/// table rather than in configuration.
/// </summary>
public class TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly ILogger<TenantResolutionMiddleware> _logger = logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenantContext,
        LanyardOrderingClient orderingClient,
        IMemoryCache cache)
    {
        // Host, not Request.Host.Value directly: behind Cloudflare this is the customer's domain
        // only because UseForwardedHeaders has already rewritten it (see Program.cs). Without
        // that call this would be the origin's own hostname and every tenant would fail to
        // resolve - so the two must stay together.
        string hostname = context.Request.Host.Host.ToLowerInvariant();

        TenantBrandingDto? tenant = await cache.GetOrCreateAsync($"tenant:{hostname}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            return await orderingClient.GetTenantByHostAsync(hostname, context.RequestAborted);
        });

        if (tenant is not null)
        {
            tenantContext.Set(tenant);
        }
        else
        {
            // Logged at Information, not Warning: bots probe bare IPs and stale hostnames
            // constantly, and a warning per hit would bury anything that actually matters.
            _logger.LogInformation("No tenant is mapped to host {Hostname}", hostname);
        }

        await _next(context);
    }
}
