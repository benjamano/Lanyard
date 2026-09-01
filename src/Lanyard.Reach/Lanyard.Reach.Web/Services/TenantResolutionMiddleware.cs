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

        // ?tenant=<slug> is the documented way in before a customer's DNS points at us - dev,
        // staging, and the gap between onboarding and propagation. Only consulted when the
        // hostname itself does not resolve, so it can never be used to view one tenant's site
        // while sitting on another's live domain.
        string? slug = context.Request.Query["tenant"].ToString();

        string cacheKey = string.IsNullOrWhiteSpace(slug)
            ? $"tenant:host:{hostname}"
            : $"tenant:host:{hostname}:slug:{slug.ToLowerInvariant()}";

        if (!cache.TryGetValue(cacheKey, out TenantBrandingDto? tenant))
        {
            tenant = await orderingClient.GetTenantByHostAsync(hostname, context.RequestAborted);

            if (tenant is null && !string.IsNullOrWhiteSpace(slug))
            {
                tenant = await orderingClient.GetTenantBySlugAsync(slug, context.RequestAborted);

                if (tenant is not null)
                {
                    _logger.LogInformation("Resolved tenant {CompanyId} by slug {Slug} because host {Hostname} is not mapped",
                        tenant.CompanyId, slug, hostname);
                }
            }

            // Only successful lookups are cached. GetTenantByHostAsync returns null both for a
            // genuinely unknown host and for a transient failure reaching the Lanyard server,
            // and it cannot distinguish them - so caching null would let one blip take a live
            // customer's whole site offline for the full TTL. Re-asking on every request until
            // an answer arrives is the cheaper mistake.
            if (tenant is not null)
            {
                cache.Set(cacheKey, tenant, CacheDuration);
            }
        }

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
