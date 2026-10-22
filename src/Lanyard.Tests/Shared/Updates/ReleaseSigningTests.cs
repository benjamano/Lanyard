using System.Security.Cryptography;
using System.Text;
using Lanyard.Shared.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.Updates;

// Security Issue 9.9: a kiosk only installs a package the release workflow signed.
[TestClass]
public class ReleaseSigningTests
{
    private static (string PrivatePem, string PublicPem) NewKeyPair()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportECPrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    // What .github/scripts/sign-client-release.ps1 does.
    private static string Sign(string manifest, string privatePem)
    {
        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        return Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256));
    }

    private static string Manifest(string version = "1.0.44", string sha256 = "31DE") =>
        ReleaseSigning.Manifest(version, $"LanyardClient-{version}-full.nupkg", 106781712, "a8b3", sha256);

    [TestMethod]
    public void ASignedManifest_Verifies()
    {
        (string privatePem, string publicPem) = NewKeyPair();

        Assert.IsTrue(ReleaseSigning.Verify(Manifest(), Sign(Manifest(), privatePem), publicPem));
    }

    [TestMethod]
    public void ATamperedHash_AnotherVersion_OrAMissingSignature_IsRefused()
    {
        (string privatePem, string publicPem) = NewKeyPair();
        string signature = Sign(Manifest(), privatePem);

        Assert.IsFalse(ReleaseSigning.Verify(Manifest(sha256: "FFFF"), signature, publicPem), "Changing the hash must break the signature");
        Assert.IsFalse(ReleaseSigning.Verify(Manifest(version: "1.0.45"), signature, publicPem), "A signature is for one version only");
        Assert.IsFalse(ReleaseSigning.Verify(Manifest(), null, publicPem));
        Assert.IsFalse(ReleaseSigning.Verify(Manifest(), "not base64!", publicPem));
    }

    [TestMethod]
    public void ASignatureFromAnotherKey_IsRefused()
    {
        (string privatePem, _) = NewKeyPair();
        (_, string otherPublicPem) = NewKeyPair();

        Assert.IsFalse(ReleaseSigning.Verify(Manifest(), Sign(Manifest(), privatePem), otherPublicPem));
    }

    [TestMethod]
    public void TheManifest_UpperCasesHashes_SoTheWorkflowAndKioskAgree()
    {
        Assert.AreEqual("LanyardClient|1.0.44|LanyardClient-1.0.44-full.nupkg|106781712|A8B3|31DE", Manifest());
    }

    // Fails until the real public key is in ReleaseSigning.PublicKeyPem (docs/CLIENT_RELEASE_SIGNING.md).
    // A kiosk built with the placeholder could never install another update.
    [TestMethod]
    public void ThePublicKeyIsConfigured()
    {
        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(ReleaseSigning.PublicKeyPem);

        Assert.AreEqual(256, key.KeySize, "The release signing key must be ECDSA P-256.");
    }

    [TestMethod]
    [DataRow("https://github.com/benjamano/Lanyard/releases/download/LanyardClient-1.0.44/LanyardClient-1.0.44-full.nupkg", true)]
    [DataRow("https://objects.githubusercontent.com/x", true)]
    [DataRow("http://github.com/benjamano/Lanyard/releases/download/x.nupkg", false)]
    [DataRow("https://github.com.evil.example/x.nupkg", false)]
    [DataRow("https://evil.example/x.nupkg", false)]
    [DataRow("file:///C:/x.nupkg", false)]
    [DataRow("not a url", false)]
    public void DownloadUrls_MustBeGitHubOverHttps(string url, bool allowed)
    {
        Assert.AreEqual(allowed, ReleaseSigning.IsAllowedDownloadUrl(url));
    }
}
