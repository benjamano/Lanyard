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

        OrderingApiOutcome outcome = OrderingApiOutcome.Ok;

        if (!cache.TryGetValue(cacheKey, out TenantBrandingDto? tenant))
        {
            OrderingApiResult<TenantBrandingDto> byHost =
                await orderingClient.GetTenantByHostAsync(hostname, context.RequestAborted);

            tenant = byHost.Value;
            outcome = byHost.Outcome;

            // The slug is only worth trying when the host is genuinely unmapped. If the lookup
            // failed because the server refused our credential or could not be reached, the slug
            // call would fail for exactly the same reason - and retrying would replace an
            // accurate diagnosis with a second, identical one.
            if (tenant is null && outcome == OrderingApiOutcome.NotFound && !string.IsNullOrWhiteSpace(slug))
            {
                OrderingApiResult<TenantBrandingDto> bySlug =
                    await orderingClient.GetTenantBySlugAsync(slug, context.RequestAborted);

                tenant = bySlug.Value;
                outcome = bySlug.Outcome;

                if (tenant is not null)
                {
                    _logger.LogInformation("Resolved tenant {CompanyId} by slug {Slug} because host {Hostname} is not mapped",
                        tenant.CompanyId, slug, hostname);
                }
            }

            // Only successful lookups are cached. Caching a failure would let one blip - or one
            // wrong environment variable - take a live customer's whole site offline for the
            // full TTL, and re-asking every request until an answer arrives is the cheaper
            // mistake.
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
            // Why it failed decides both the log level and what the customer is told. These used
            // to be indistinguishable, which is how a missing Reach:SharedSecret came to be
            // reported to customers as an unrecognised table code.
            switch (outcome)
            {
                case OrderingApiOutcome.Unauthorized:
                    tenantContext.SetResolutionFailure(TenantResolutionFailure.ServerUnavailable);

                    _logger.LogError(
                        "Cannot serve {Hostname}: the Lanyard server refused Reach's credential. Set "
                        + "Reach__SharedSecret on this site to the same value as the Lanyard server's.",
                        hostname);
                    break;

                case OrderingApiOutcome.NotFound:
                    // Information, not Warning: bots probe bare IPs and stale hostnames constantly,
                    // and a warning per hit would bury anything that actually matters.
                    _logger.LogInformation("No tenant is mapped to host {Hostname}", hostname);
                    break;

                default:
                    tenantContext.SetResolutionFailure(TenantResolutionFailure.ServerUnavailable);

                    _logger.LogWarning(
                        "Cannot serve {Hostname}: the tenant lookup against the Lanyard server failed ({Outcome}).",
                        hostname, outcome);
                    break;
            }
        }

        await _next(context);
    }
}
