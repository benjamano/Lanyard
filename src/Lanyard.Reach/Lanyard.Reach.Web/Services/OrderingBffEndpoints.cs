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
    public static void MapOrderingBff(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/order");

        group.MapGet("/table/{tableToken}", async (
            string tableToken,
            ITenantContext tenantContext,
            LanyardOrderingClient client,
            CancellationToken cancellationToken) =>
        {
            if (!tenantContext.IsResolved)
            {
                return Results.NotFound();
            }

            TableResolutionDto? table = await client.ResolveTableAsync(
                tableToken, tenantContext.Tenant.CompanyId, cancellationToken);

            return table is null ? Results.NotFound() : Results.Ok(table);
        });

        group.MapGet("/table/{tableToken}/menu", async (
            string tableToken,
            ITenantContext tenantContext,
            LanyardOrderingClient client,
            CancellationToken cancellationToken) =>
        {
            if (!tenantContext.IsResolved)
            {
                return Results.NotFound();
            }

            int companyId = tenantContext.Tenant.CompanyId;

            // Resolved through the table token rather than taking a location id from the client,
            // so a customer's phone never learns or supplies an internal location id.
            TableResolutionDto? table = await client.ResolveTableAsync(tableToken, companyId, cancellationToken);

            if (table is null || !table.OrderingEnabled)
            {
                return Results.NotFound();
            }

            MenuDto? menu = await client.GetMenuAsync(table.LocationId, companyId, cancellationToken);

            return menu is null ? Results.NotFound() : Results.Ok(menu);
        });

        group.MapPost("/orders", async (
            CreateOrderRequestDto request,
            ITenantContext tenantContext,
            LanyardOrderingClient client,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!tenantContext.IsResolved)
            {
                return Results.NotFound();
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
                    new { error = "You're going a bit fast for us — please wait a moment and try again." },
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
            if (!tenantContext.IsResolved)
            {
                return Results.NotFound();
            }

            OrderStatusDto? status = await client.GetOrderStatusAsync(
                orderToken, tenantContext.Tenant.CompanyId, cancellationToken);

            return status is null ? Results.NotFound() : Results.Ok(status);
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
            if (!tenantContext.IsResolved)
            {
                return Results.NotFound();
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

        // The tenant's logo, proxied for the same same-origin reason as menu photos. Reuses the
        // Lanyard server's existing CompanyBrandingController endpoint rather than adding one.
        app.MapGet("/tenant-logo", async (
            ITenantContext tenantContext,
            LanyardOrderingClient client,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!tenantContext.IsResolved)
            {
                return Results.NotFound();
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
