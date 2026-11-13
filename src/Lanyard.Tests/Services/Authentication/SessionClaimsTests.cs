using System.Security.Claims;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Authentication;

[TestClass]
public class SessionClaimsTests
{
    private static ClaimsPrincipal SignedIn(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));

    [TestMethod]
    public void CarryOver_KeepsTheSignInOnlyClaims_AndNotRoles()
    {
        ClaimsPrincipal current = SignedIn(
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim(LocationClaimTypes.LocationId, "4"),
            new Claim(LocationClaimTypes.CompanyId, "2"),
            new Claim(SessionClaims.AuthenticationMethod, "mfa"));

        List<Claim> carried = SessionClaims.CarryOver(current);

        CollectionAssert.AreEquivalent(
            new[] { LocationClaimTypes.LocationId, LocationClaimTypes.CompanyId, SessionClaims.AuthenticationMethod },
            carried.Select(x => x.Type).ToArray(),
            "Roles and the user id come from the store; only the sign-in choices are carried.");
        Assert.AreEqual("4", carried.Single(x => x.Type == LocationClaimTypes.LocationId).Value);
    }

    [TestMethod]
    public void CarryOver_NoPrincipal_CarriesNothing()
    {
        Assert.AreEqual(0, SessionClaims.CarryOver(null).Count);
    }

    [TestMethod]
    public void CopyMissing_AddsMissingClaims_WithoutDuplicating()
    {
        ClaimsPrincipal current = SignedIn(
            new Claim(LocationClaimTypes.LocationId, "4"),
            new Claim(LocationClaimTypes.CompanyId, "2"));
        ClaimsIdentity rebuilt = new([new Claim(LocationClaimTypes.CompanyId, "2")], "Test");

        SessionClaims.CopyMissing(current, rebuilt);

        Assert.AreEqual("4", rebuilt.FindFirst(LocationClaimTypes.LocationId)?.Value);
        Assert.AreEqual(1, rebuilt.FindAll(LocationClaimTypes.CompanyId).Count());
    }
}
