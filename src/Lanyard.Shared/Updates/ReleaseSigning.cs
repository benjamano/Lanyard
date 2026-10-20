using System.Security.Cryptography;
using System.Text;

namespace Lanyard.Shared.Updates;

// Proof that a kiosk update package came out of Lanyard's own release workflow (Security Issue
// 9.9). The feed - including the package's hashes - comes from the Lanyard server, so on its own it
// is only as trustworthy as the server and the connection to it. The release workflow signs a short
// manifest of each package with a private key that only GitHub Actions holds; the kiosk checks it
// against the public key below before it lets Velopack download anything, and Velopack then checks
// the downloaded bytes against the hashes the manifest vouches for.
//
// ECDSA P-256 with SHA-256, signature in .NET's default IEEE P1363 format, base64. The workflow
// signs with the same .NET API (PowerShell 7), so both sides agree on the format.
public static class ReleaseSigning
{
    // REPLACE BEFORE MERGING: the public half of the CLIENT_RELEASE_SIGNING_KEY GitHub secret.
    // ReleaseSigningTests.ThePublicKeyIsConfigured fails until it is a real P-256 key, so CI stops
    // a kiosk build that could never update again from shipping. See docs/CLIENT_RELEASE_SIGNING.md.
    public const string PublicKeyPem = "REPLACE_WITH_CLIENT_RELEASE_SIGNING_PUBLIC_KEY";

    public static string SignatureFileName(string packageFileName) => packageFileName + ".sig";

    public static string Manifest(string version, string fileName, long size, string? sha1, string? sha256) =>
        $"LanyardClient|{version}|{fileName}|{size}|{(sha1 ?? string.Empty).ToUpperInvariant()}|{(sha256 ?? string.Empty).ToUpperInvariant()}";

    public static bool Verify(string manifest, string? signatureBase64, string publicKeyPem = PublicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64))
        {
            return false;
        }

        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);

            return key.VerifyData(Encoding.UTF8.GetBytes(manifest), Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    // Where a package may be downloaded from: GitHub's release download hosts, over https only.
    // The feed's URLs come from the server, so this stops a tampered feed pointing anywhere else.
    private static readonly string[] AllowedDownloadHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    public static bool IsAllowedDownloadUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
}
