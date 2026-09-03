using System.Net;
using System.Net.Http.Json;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;

namespace Lanyard.Reach.Web.Services;

/// <summary>
/// Reach's server-side client for the Lanyard ordering API.
///
/// This is the only thing in Reach that knows the Lanyard server exists. Customers' browsers talk
/// exclusively to the tenant's own domain, and this class makes the onward call, which is why
/// there is no CORS policy to configure, no credential in any page the customer can read, and no
/// change needed to the Lanyard server's Content-Security-Policy.
/// </summary>
public class LanyardOrderingClient(HttpClient httpClient, ILogger<LanyardOrderingClient> logger)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<LanyardOrderingClient> _logger = logger;

    public async Task<OrderingApiResult<TenantBrandingDto>> GetTenantByHostAsync(string hostname, CancellationToken cancellationToken = default) =>
        await GetAsync<TenantBrandingDto>($"api/ordering/tenants/by-host/{Uri.EscapeDataString(hostname)}", cancellationToken);

    public async Task<TenantLegalDetailsDto?> GetLegalDetailsAsync(int companyId, CancellationToken cancellationToken = default) =>
        await GetOrNullAsync<TenantLegalDetailsDto>($"api/ordering/tenants/{companyId}/legal", cancellationToken);

    /// <summary>
    /// One of the company's legal documents, already rendered with its own details substituted in.
    /// Reach never holds the raw template, so it cannot render one company's document against
    /// another's identity.
    /// </summary>
    public async Task<LegalDocumentDto?> GetLegalDocumentAsync(
        int companyId, LegalDocumentType documentType, CancellationToken cancellationToken = default) =>
        await GetOrNullAsync<LegalDocumentDto>(
            $"api/ordering/tenants/{companyId}/documents/{documentType}", cancellationToken);

    public async Task<OrderingApiResult<TenantBrandingDto>> GetTenantBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        await GetAsync<TenantBrandingDto>($"api/ordering/tenants/by-slug/{Uri.EscapeDataString(slug)}", cancellationToken);

    public async Task<TableResolutionDto?> ResolveTableAsync(string tableToken, int companyId, CancellationToken cancellationToken = default) =>
        await GetOrNullAsync<TableResolutionDto>(
            $"api/ordering/tables/{Uri.EscapeDataString(tableToken)}?companyId={companyId}", cancellationToken);

    public async Task<MenuDto?> GetMenuAsync(int locationId, int companyId, CancellationToken cancellationToken = default) =>
        await GetOrNullAsync<MenuDto>(
            $"api/ordering/locations/{locationId}/menu?companyId={companyId}", cancellationToken);

    public async Task<OrderStatusDto?> GetOrderStatusAsync(Guid orderToken, int companyId, CancellationToken cancellationToken = default) =>
        await GetOrNullAsync<OrderStatusDto>(
            $"api/ordering/orders/{orderToken}/status?companyId={companyId}", cancellationToken);

    public async Task<HttpResponseMessage> GetMenuItemImageAsync(int itemId, int companyId, CancellationToken cancellationToken = default) =>
        await _httpClient.GetAsync($"api/ordering/menu-items/{itemId}/image?companyId={companyId}", cancellationToken);

    /// <summary>
    /// The tenant's logo, from the branding endpoints that already exist for this purpose
    /// (CompanyBrandingController) rather than a new one. Proxied like the menu photos so the
    /// browser stays same-origin.
    /// </summary>
    public async Task<HttpResponseMessage> GetCompanyLogoAsync(int companyId, CancellationToken cancellationToken = default) =>
        await _httpClient.GetAsync($"api/companies/{companyId}/logo", cancellationToken);

    /// <summary>The tenant's browser-tab icon, which is a different image from the logo.</summary>
    public async Task<HttpResponseMessage> GetCompanyFaviconAsync(int companyId, CancellationToken cancellationToken = default) =>
        await _httpClient.GetAsync($"api/companies/{companyId}/favicon", cancellationToken);

    /// <summary>
    /// Returns the raw response so the caller can pass the server's customer-facing error text
    /// ("we've just run out of chips") straight through rather than flattening every failure into
    /// one generic message.
    /// </summary>
    public async Task<HttpResponseMessage> CreateOrderAsync(
        CreateOrderRequestDto request,
        int companyId,
        CancellationToken cancellationToken = default) =>
        await _httpClient.PostAsJsonAsync($"api/ordering/orders?companyId={companyId}", request, cancellationToken);

    private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken cancellationToken) =>
        (await GetAsync<T>(path, cancellationToken)).Value;

    /// <summary>
    /// Fetches, keeping *why* a fetch failed rather than collapsing everything to null.
    ///
    /// The distinction matters to the customer: a genuine 404 means their table code is wrong,
    /// while a 429 or a 502 means try again shortly. Flattening both into "not found" told
    /// somebody who was merely going too fast that their QR code was broken.
    /// </summary>
    public async Task<OrderingApiResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            HttpResponseMessage response = await _httpClient.GetAsync(path, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new OrderingApiResult<T>(default, OrderingApiOutcome.NotFound);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("Lanyard ordering API rate limited {Path}", path);

                return new OrderingApiResult<T>(default, OrderingApiOutcome.RateLimited);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogError(
                    "The Lanyard server rejected Reach's credential for {Path}. Reach:SharedSecret here must "
                    + "match Reach__SharedSecret on the Lanyard server; until it does, every ordering request "
                    + "from this site fails.", path);

                return new OrderingApiResult<T>(default, OrderingApiOutcome.Unauthorized);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Lanyard ordering API returned {StatusCode} for {Path}", (int)response.StatusCode, path);

                return new OrderingApiResult<T>(default, OrderingApiOutcome.Unavailable);
            }

            return new OrderingApiResult<T>(
                await response.Content.ReadFromJsonAsync<T>(cancellationToken), OrderingApiOutcome.Ok);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reach the Lanyard ordering API for {Path}", path);

            return new OrderingApiResult<T>(default, OrderingApiOutcome.Unavailable);
        }
    }
}

public enum OrderingApiOutcome
{
    Ok,

    /// <summary>The thing genuinely does not exist - a wrong or retired table code.</summary>
    NotFound,

    /// <summary>Throttled. Transient, and must not be reported to the customer as "not found".</summary>
    RateLimited,

    /// <summary>
    /// The Lanyard server refused our credential. Called out separately from Unavailable because
    /// it is a configuration mistake with one specific fix, not a transient fault, and it takes
    /// the entire site down while looking from the outside like a bad table code.
    /// </summary>
    Unauthorized,

    /// <summary>Anything else - the server erred or could not be reached at all.</summary>
    Unavailable
}

public record OrderingApiResult<T>(T? Value, OrderingApiOutcome Outcome)
{
    public bool IsOk => Outcome == OrderingApiOutcome.Ok && Value is not null;
}
