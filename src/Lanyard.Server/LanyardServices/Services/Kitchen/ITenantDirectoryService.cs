using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;

namespace Lanyard.Application.Services.Kitchen;

/// <summary>
/// Resolves which company a public-facing hostname belongs to, and supplies the branding the
/// public site needs to render itself as that tenant.
///
/// This is what makes onboarding a new customer domain a database row plus DNS rather than a
/// deployment: nothing about a tenant is compiled in.
/// </summary>
public interface ITenantDirectoryService
{
    /// <summary>
    /// Looks up a tenant by the hostname the visitor's browser asked for. Returns a failure
    /// (not a default tenant) when the host is unknown - serving some other company's site to
    /// an unrecognised host would be worse than serving nothing.
    /// </summary>
    Task<Result<TenantBrandingDto>> GetTenantByHostnameAsync(string hostname);

    /// <summary>Fallback lookup for dev, staging, and the gap before a customer's DNS is pointed at us.</summary>
    Task<Result<TenantBrandingDto>> GetTenantBySlugAsync(string slug);

    /// <summary>
    /// The company's legal identity, for the customer-facing ordering terms. Separate from
    /// branding because it is fetched only when someone opens the terms, not on every request.
    /// </summary>
    Task<Result<TenantLegalDetailsDto>> GetLegalDetailsAsync(int companyId);

    Task<Result<List<CompanyDomain>>> GetDomainsForCompanyAsync(int companyId);

    Task<Result<CompanyDomain>> SaveDomainAsync(CompanyDomain domain);

    Task<Result<bool>> DeactivateDomainAsync(int domainId);
}
