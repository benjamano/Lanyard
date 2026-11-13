using Lanyard.Infrastructure.Models;
using System.Security.Claims;

namespace Lanyard.Application.Services.Authentication;

// Claims a session gets at sign-in that the user/role store can't rebuild: the location (and its
// company) picked on the login page, and how the user authenticated. Whenever the cookie principal
// is rebuilt from the store - the security stamp check, or a session refresh after a role change -
// these have to be copied across from the old principal or they're silently lost.
public static class SessionClaims
{
    public const string AuthenticationMethod = "amr";

    private static readonly string[] CarriedTypes =
    [
        LocationClaimTypes.LocationId,
        LocationClaimTypes.CompanyId,
        AuthenticationMethod,
    ];

    public static List<Claim> CarryOver(ClaimsPrincipal? from)
    {
        if (from is null)
        {
            return [];
        }

        return CarriedTypes
            .Select(type => from.FindFirst(type))
            .OfType<Claim>()
            .Select(claim => new Claim(claim.Type, claim.Value))
            .ToList();
    }

    // Adds the carried claims the new principal doesn't already have.
    public static void CopyMissing(ClaimsPrincipal? from, ClaimsIdentity to)
    {
        foreach (Claim claim in CarryOver(from))
        {
            if (!to.HasClaim(x => x.Type == claim.Type))
            {
                to.AddClaim(claim);
            }
        }
    }
}
