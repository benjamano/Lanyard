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

    /// <summary>
    /// Why resolution failed, when it did. A page that cannot tell "this host belongs to nobody"
    /// from "we could not reach the Lanyard server" has to guess, and guessing "unknown" told
    /// customers their QR code was wrong when the real fault was ours.
    /// </summary>
    TenantResolutionFailure Failure { get; }

    void Set(TenantBrandingDto tenant);

    void SetResolutionFailure(TenantResolutionFailure failure);
}

public enum TenantResolutionFailure
{
    /// <summary>No tenant is mapped to this hostname. The visitor is genuinely in the wrong place.</summary>
    UnknownHost,

    /// <summary>We could not ask, or were not allowed to. Ours to fix, and transient from the visitor's side.</summary>
    ServerUnavailable
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

    public TenantResolutionFailure Failure { get; private set; } = TenantResolutionFailure.UnknownHost;

    public void Set(TenantBrandingDto tenant) => _tenant = tenant;

    public void SetResolutionFailure(TenantResolutionFailure failure) => Failure = failure;
}
