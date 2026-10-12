using Lanyard.Application.Services.ClientReleases;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.ClientReleases;

[TestClass]
public class ClientTargetVersionResolverTests
{
    private static ClientRelease Release(string version, bool prerelease = false, bool promoted = false, bool withdrawn = false) => new()
    {
        Version = version,
        Tag = $"LanyardClient-{version}",
        IsPrerelease = prerelease,
        PromotedToStableAt = promoted ? DateTime.UtcNow : null,
        WithdrawnAt = withdrawn ? DateTime.UtcNow : null
    };

    [TestMethod]
    public void Stable_TakesNewestNormalRelease_IgnoringUnpromotedPrereleases()
    {
        List<ClientRelease> releases = [Release("1.0.40"), Release("1.0.41"), Release("1.0.42", prerelease: true)];

        ClientTargetResolution result = ClientTargetVersionResolver.Resolve(ClientUpdateRing.Stable, null, releases);

        Assert.AreEqual("1.0.41", result.TargetVersion);
        Assert.IsNull(result.Problem);
    }

    [TestMethod]
    public void Stable_TakesPromotedPrerelease()
    {
        List<ClientRelease> releases = [Release("1.0.41"), Release("1.0.42", prerelease: true, promoted: true)];

        ClientTargetResolution result = ClientTargetVersionResolver.Resolve(ClientUpdateRing.Stable, null, releases);

        Assert.AreEqual("1.0.42", result.TargetVersion);
    }

    [TestMethod]
    public void Stable_FallsBack_WhenNewestStableIsWithdrawn()
    {
        List<ClientRelease> releases = [Release("1.0.41"), Release("1.0.42", prerelease: true, promoted: true, withdrawn: true)];

        ClientTargetResolution result = ClientTargetVersionResolver.Resolve(ClientUpdateRing.Stable, null, releases);

        Assert.AreEqual("1.0.41", result.TargetVersion);
    }

    [TestMethod]
    public void Stable_HasNoTarget_WhenNothingIsStable()
    {
        List<ClientRelease> releases = [Release("1.0.42", prerelease: true)];

        ClientTargetResolution result = ClientTargetVersionResolver.Resolve(ClientUpdateRing.Stable, null, releases);

        Assert.IsNull(result.TargetVersion);
        Assert.IsNotNull(result.Problem);
    }

    [TestMethod]
    public void Canary_TakesNewestReleaseOfAnyKind()
    {
        List<ClientRelease> releases = [Release("1.0.41"), Release("1.0.42", prerelease: true)];

        ClientTargetResolution result = ClientTargetVersionResolver.Resolve(ClientUpdateRing.Canary, null, releases);

        Assert.AreEqual("1.0.42", result.TargetVersion);
    }

    [TestMethod]
    public void Canary_MovesBack_WhenNewestIsWithdrawn()
    {
        List<ClientRelease> releases = [Release("1.0.41"), Release("1.0.42", prerelease: true, withdrawn: true)];

        ClientTargetResolution result = ClientTargetVersionResolver.Resolve(ClientUpdateRing.Canary, null, releases);

        Assert.AreEqual("1.0.41", result.TargetVersion);
    }

    [TestMethod]
    public void Newest_ComparesVersionsNumerically()
    {
        List<ClientRelease> releases = [Release("1.0.9"), Release("1.0.10")];

        ClientTargetResolution result = ClientTargetVersionResolver.Resolve(ClientUpdateRing.Stable, null, releases);

        Assert.AreEqual("1.0.10", result.TargetVersion);
    }

    [TestMethod]
    public void Pinned_TargetsThePinnedVersion_EvenWhenOlder()
    {
        List<ClientRelease> releases = [Release("1.0.40"), Release("1.0.41")];

        ClientTargetResolution result = ClientTargetVersionResolver.Resolve(ClientUpdateRing.Pinned, "1.0.40", releases);

        Assert.AreEqual("1.0.40", result.TargetVersion);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("1.0.99")]
    public void Pinned_HasNoTarget_WhenPinIsMissingOrUnknown(string? pinned)
    {
        List<ClientRelease> releases = [Release("1.0.41")];

        ClientTargetResolution result = ClientTargetVersionResolver.Resolve(ClientUpdateRing.Pinned, pinned, releases);

        Assert.IsNull(result.TargetVersion);
        Assert.IsNotNull(result.Problem);
    }

    [TestMethod]
    public void Pinned_HasNoTarget_WhenPinnedReleaseIsWithdrawn()
    {
        // Staying put is safer than guessing which other version the admin would want.
        List<ClientRelease> releases = [Release("1.0.40", withdrawn: true), Release("1.0.41")];

        ClientTargetResolution result = ClientTargetVersionResolver.Resolve(ClientUpdateRing.Pinned, "1.0.40", releases);

        Assert.IsNull(result.TargetVersion);
        StringAssert.Contains(result.Problem, "withdrawn");
    }
}
