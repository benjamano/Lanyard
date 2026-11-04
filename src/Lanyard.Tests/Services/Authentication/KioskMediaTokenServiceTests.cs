using Lanyard.Application.Services.Authentication;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.SignalR;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Authentication;

[TestClass]
public class KioskMediaTokenServiceTests
{
    private static readonly KioskIdentity Kiosk = new(Guid.NewGuid(), 1, Guid.NewGuid());

    [TestMethod]
    public void Unprotect_ReturnsTheCredentialTheTokenWasIssuedFor()
    {
        KioskMediaTokenService service = new(new EphemeralDataProtectionProvider(), TimeProvider.System);

        Result<Guid> result = service.Unprotect(service.Issue(Kiosk));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(Kiosk.CredentialId, result.Data);
    }

    [TestMethod]
    public void Unprotect_RejectsAnExpiredToken()
    {
        TestClock clock = new(DateTime.UtcNow.AddDays(-2));
        KioskMediaTokenService issuer = new(new EphemeralDataProtectionProvider(), clock);

        Result<Guid> result = issuer.Unprotect(issuer.Issue(Kiosk));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public void Unprotect_RejectsATokenFromAnotherServersKeys()
    {
        KioskMediaTokenService ours = new(new EphemeralDataProtectionProvider(), TimeProvider.System);
        KioskMediaTokenService theirs = new(new EphemeralDataProtectionProvider(), TimeProvider.System);

        Assert.IsFalse(ours.Unprotect(theirs.Issue(Kiosk)).IsSuccess);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("garbage")]
    public void Unprotect_RejectsGarbage(string? token)
    {
        KioskMediaTokenService service = new(new EphemeralDataProtectionProvider(), TimeProvider.System);

        Assert.IsFalse(service.Unprotect(token).IsSuccess);
    }

    [TestMethod]
    [DataRow("/api/music/audio/7c9e6679-7425-40de-944b-e07fc1f90ae7", true)]
    [DataRow("/api/audioclips/7c9e6679-7425-40de-944b-e07fc1f90ae7/audio", true)]
    [DataRow("/api/files/list", false)]
    [DataRow("/api/files/download/7c9e6679-7425-40de-944b-e07fc1f90ae7", false)]
    [DataRow("/api/client-updates/7c9e6679-7425-40de-944b-e07fc1f90ae7/feed", false)]
    [DataRow("/websocket", false)]
    public void MediaTokens_AreOnlyAcceptedOnTheStreamingUrls(string path, bool accepted)
    {
        Assert.AreEqual(accepted, KioskAuthenticationHandler.AcceptsMediaToken(new PathString(path)));
    }

    // The kiosk invokes these by string; the server method name must match.
    [TestMethod]
    public void HubMethodName_MatchesTheSharedConstant()
    {
        Assert.AreEqual(ClientHubMethods.IssueMediaToken, nameof(SignalRControlHub.IssueMediaToken));
        Assert.AreEqual(KioskCredentialQuery.MediaToken, KioskAuthenticationDefaults.MediaTokenQuery);
    }
}
