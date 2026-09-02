using System.Net;
using System.Net.Http.Json;
using Lanyard.Shared.DTO;

namespace Lanyard.Reach.Web.Services;

/// <summary>
/// The ordering endpoints the customer's browser calls, on the tenant's own domain.
///
/// Every one of these forwards to the Lanyard server with Reach's credential attached
/// server-side, scoped to the tenant resolved from the hostname. Two consequences worth being
/// explicit about:
///
///  - The browser never makes a cross-origin request, so no CORS policy exists to get wrong.
///  - The client cannot choose which tenant it is. Company id comes from the resolved host,
///    never from anything the caller sent, so a crafted request cannot reach across tenants.
/// </summary>
public static class OrderingBffEndpoints
{
    /// <summary>
    /// Maps a failed upstream call to the status the customer's browser should see. Keeping 429
    /// and 502 distinct from 404 is what stops "we're busy, try again" being shown as "this
    /// table doesn't exist".
    /// </summary>
    private static IResult Translate(OrderingApiOutcome outcome) => outcome switch
    {
        OrderingApiOutcome.RateLimited => Results.Json(
            new { error = "We're a bit busy. Give it a few seconds and try again." },
            statusCode: StatusCodes.Status429TooManyRequests),
        // Unauthorized is ours, not the caller's: a rejected credential is a bad gateway from the
        // customer's point of view, and must never be reported as a missing table.
        OrderingApiOutcome.Unavailable or OrderingApiOutcome.Unauthorized => Results.Json(
            new { error = "We couldn't reach the kitchen just now. Please try again." },
            statusCode: StatusCodes.Status502BadGateway),
        _ => Results.NotFound()
    };

    /// <summary>
    /// The guard every endpoint runs first. Returns null when a tenant resolved, otherwise the
    /// response to send. An unresolved tenant is only a 404 when the hostname genuinely belongs
    /// to nobody; if we simply could not ask the Lanyard server, that is a 502 and the caller
    /// should retry rather than conclude the table does not exist.
    /// </summary>
    private static IResult? TenantGate(ITenantContext tenantContext)
    {
        if (tenantContext.IsResolved)
        {
            return null;
        }

        return tenantContext.Failure == TenantResolutionFailure.ServerUnavailable
            ? Translate(OrderingApiOutcome.Unavailable)
            : Results.NotFound();
    }

    public static void MapOrderingBff(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/order");

        group.MapGet("/table/{tableToken}", async (
            string tableToken,
            ITenantContext tenantContext,
            LanyardOrderingClient client,
            CancellationToken cancellationToken) =>
        {
            if (TenantGate(tenantContext) is IResult gate)
            {
                return gate;
            }

            OrderingApiResult<TableResolutionDto> table = await client.GetAsync<TableResolutionDto>(
                $"api/ordering/tables/{Uri.EscapeDataString(tableToken)}?companyId={tenantContext.Tenant.CompanyId}",
                cancellationToken);

            // A throttled or unavailable server must not be reported as a missing table: the
            // customer would be told their QR code is wrong and go and find a member of staff.
            return table.IsOk ? Results.Ok(table.Value) : Translate(table.Outcome);
        });

        group.MapGet("/table/{tableToken}/menu", async (
            string tableToken,
            ITenantContext tenantContext,
            LanyardOrderingClient client,
            CancellationToken cancellationToken) =>
        {
            if (TenantGate(tenantContext) is IResult gate)
            {
                return gate;
            }

            int companyId = tenantContext.Tenant.CompanyId;

            // Resolved through the table token rather than taking a location id from the client,
            // so a customer's phone never learns or supplies an internal location id.
            TableResolutionDto? table = await client.ResolveTableAsync(tableToken, companyId, cancellationToken);

            if (table is null || !table.OrderingEnabled)
            {
                return Results.NotFound();
            }

            OrderingApiResult<MenuDto> menu = await client.GetAsync<MenuDto>(
                $"api/ordering/locations/{table.LocationId}/menu?companyId={companyId}", cancellationToken);

            return menu.IsOk ? Results.Ok(menu.Value) : Translate(menu.Outcome);
        });

        group.MapPost("/orders", async (
            CreateOrderRequestDto request,
            ITenantContext tenantContext,
            LanyardOrderingClient client,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (TenantGate(tenantContext) is IResult gate)
            {
                return gate;
            }

            HttpResponseMessage response = await client.CreateOrderAsync(
                request, tenantContext.Tenant.CompanyId, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                CreateOrderResultDto? result = await response.Content
                    .ReadFromJsonAsync<CreateOrderResultDto>(cancellationToken);

                return result is null ? Results.StatusCode(502) : Results.Ok(result);
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                // Passed through verbatim: these are messages written for the customer, and
                // replacing them with something generic would lose the one useful detail
                // ("we've just run out of chips") the customer needs to fix their order.
                string body = await response.Content.ReadAsStringAsync(cancellationToken);

                return Results.Content(body, "application/json", statusCode: (int)HttpStatusCode.BadRequest);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return Results.Json(
                    new { error = "You're going a bit fast for us. Please wait a moment and try again." },
                    statusCode: (int)HttpStatusCode.TooManyRequests);
            }

            return Results.Json(
                new { error = "We couldn't send your order. Please try again." },
                statusCode: (int)HttpStatusCode.BadGateway);
        });

        group.MapGet("/orders/{orderToken:guid}", async (
            Guid orderToken,
            ITenantContext tenantContext,
            LanyardOrderingClient client,
            CancellationToken cancellationToken) =>
        {
            if (TenantGate(tenantContext) is IResult gate)
            {
                return gate;
            }

            OrderingApiResult<OrderStatusDto> status = await client.GetAsync<OrderStatusDto>(
                $"api/ordering/orders/{orderToken}/status?companyId={tenantContext.Tenant.CompanyId}",
                cancellationToken);

            // Especially important on the status poll: a throttled poll reported as 404 would
            // make the customer's own order look like it had vanished.
            return status.IsOk ? Results.Ok(status.Value) : Translate(status.Outcome);
        });

        // Outside the /api/order group because it is referenced directly from an <img> tag.
        // Proxied rather than linked straight to the Lanyard server so the image is same-origin,
        // which keeps it within the page's own img-src and lets the CDN in front of this domain
        // cache it alongside everything else.
        app.MapGet("/menu-image/{itemId:int}", async (
            int itemId,
            ITenantContext tenantContext,
            LanyardOrderingClient client,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (TenantGate(tenantContext) is IResult gate)
            {
                return gate;
            }

            HttpResponseMessage response = await client.GetMenuItemImageAsync(
                itemId, tenantContext.Tenant.CompanyId, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Results.NotFound();
            }

            string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

            httpContext.Response.Headers.CacheControl = "public, max-age=3600";

            return Results.Stream(await response.Content.ReadAsStreamAsync(cancellationToken), contentType);
        });

        // The tenant's browser-tab icon. Proxied like the logo, and for the same reason.
        app.MapGet("/tenant-favicon", async (
            ITenantContext tenantContext,
            LanyardOrderingClient client,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (TenantGate(tenantContext) is IResult gate)
            {
                return gate;
            }

            HttpResponseMessage response = await client.GetCompanyFaviconAsync(
                tenantContext.Tenant.CompanyId, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Results.NotFound();
            }

            string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

            httpContext.Response.Headers.CacheControl = "public, max-age=3600";

            return Results.Stream(await response.Content.ReadAsStreamAsync(cancellationToken), contentType);
        });

        // The tenant's logo, proxied for the same same-origin reason as menu photos. Reuses the
        // Lanyard server's existing CompanyBrandingController endpoint rather than adding one.
        app.MapGet("/tenant-logo", async (
            ITenantContext tenantContext,
            LanyardOrderingClient client,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (TenantGate(tenantContext) is IResult gate)
            {
                return gate;
            }

            HttpResponseMessage response = await client.GetCompanyLogoAsync(
                tenantContext.Tenant.CompanyId, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Results.NotFound();
            }

            string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

            httpContext.Response.Headers.CacheControl = "public, max-age=3600";

            return Results.Stream(await response.Content.ReadAsStreamAsync(cancellationToken), contentType);
        });
    }
}
