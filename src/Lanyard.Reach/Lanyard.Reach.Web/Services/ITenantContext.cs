using Lanyard.Shared.DTO;

namespace Lanyard.Reach.Web.Services;

/// <summary>
/// Which customer's site this request is for, resolved once per request from the hostname.
///
/// Not to be confused with the Lanyard server's ICurrentLocationContext, which scopes a signed-in
/// staff member to a venue within one company. This is the outer boundary: which company the
/// visitor is even looking at.
/// </summary>
public interface ITenantContext
{
    bool IsResolved { get; }

    TenantBrandingDto Tenant { get; }

    void Set(TenantBrandingDto tenant);
}

public class TenantContext : ITenantContext
{
    private TenantBrandingDto? _tenant;

    public bool IsResolved => _tenant is not null;

    // Throwing rather than handing back a default tenant is deliberate. Serving one customer's
    // branding - or worse, their menu - on another's domain because resolution quietly failed
    // would be a far worse outcome than an error page.
    public TenantBrandingDto Tenant => _tenant
        ?? throw new InvalidOperationException("No tenant has been resolved for this request.");

    public void Set(TenantBrandingDto tenant) => _tenant = tenant;
}
