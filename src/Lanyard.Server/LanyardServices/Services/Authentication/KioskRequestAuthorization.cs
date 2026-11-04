using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Lanyard.Application.Services.Authentication;

public enum KioskRequestAccess
{
    // Neither a signed-in user nor a kiosk: answer 401.
    Denied,

    // A kiosk asking for another kiosk's data (a {clientId} route that isn't its own): answer 404,
    // so "not yours" looks the same as "doesn't exist".
    NotThisKiosk,

    // A signed-in user. Their own company filter applies, as on any staff page.
    Staff,

    // A kiosk. HttpContext.User is now the kiosk, so the tenant filter is its company's.
    Kiosk
}

// The check at the top of every endpoint a kiosk calls (songs, audio clips, files, update feed,
// offline cache). Signed-in staff use the same endpoints from the browser, so the cookie user is
// tried first, then the kiosk scheme (KioskAuthenticationHandler).
//
// An in-body check rather than [Authorize]: with the cookie scheme in the mix, a failed kiosk
// request would be redirected to the login page instead of getting the 401 a kiosk understands.
public static class KioskRequestAuthorization
{
    public static async Task<KioskRequestAccess> AuthorizeAsync(HttpContext httpContext, Guid? routeClientId = null)
    {
        if (httpContext.User?.Identity?.IsAuthenticated == true)
        {
            return KioskRequestAccess.Staff;
        }

        AuthenticateResult result = await httpContext.AuthenticateAsync(KioskAuthenticationDefaults.Scheme);

        if (!result.Succeeded || result.Principal is null)
        {
            return KioskRequestAccess.Denied;
        }

        // Before any service runs: TenantContext reads HttpContext.User the first time it's asked.
        httpContext.User = result.Principal;

        if (routeClientId is Guid requested && GetClientId(result.Principal) is Guid kioskId && kioskId != requested)
        {
            return KioskRequestAccess.NotThisKiosk;
        }

        return KioskRequestAccess.Kiosk;
    }

    // The kiosk a principal belongs to, or null for staff and for a shared-secret caller the
    // server couldn't identify.
    public static Guid? GetClientId(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue(KioskClaimTypes.ClientId), out Guid clientId) ? clientId : null;

    public static Guid? GetCredentialId(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue(KioskClaimTypes.CredentialId), out Guid credentialId) ? credentialId : null;
}
