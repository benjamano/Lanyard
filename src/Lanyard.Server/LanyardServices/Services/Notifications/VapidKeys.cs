using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Notifications;

// Bound from the "Push" section (Push__PublicKey etc. on Railway). The private key is a secret
// and never goes in appsettings or the repo.
public class PushOptions
{
    public string? PublicKey { get; set; }
    public string? PrivateKey { get; set; }

    // Who the push services contact about misuse: a mailto: address or an https URL.
    public string? Subject { get; set; }
}

// The VAPID key pair this server signs pushes with. Browsers bind each subscription to the public
// key, so changing keys invalidates every subscription: set them once per environment and leave them.
public sealed class VapidKeys
{
    private VapidKeys(string? publicKey, string? privateKey, string subject)
    {
        PublicKey = publicKey;
        PrivateKey = privateKey;
        Subject = subject;
    }

    public string? PublicKey { get; }
    public string? PrivateKey { get; }
    public string Subject { get; }

    public bool IsConfigured => !string.IsNullOrEmpty(PublicKey) && !string.IsNullOrEmpty(PrivateKey);

    public static VapidKeys Create(PushOptions options, string? publicBaseUrl, bool isDevelopment, ILogger logger)
    {
        string subject = !string.IsNullOrWhiteSpace(options.Subject) ? options.Subject
            : !string.IsNullOrWhiteSpace(publicBaseUrl) && publicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? publicBaseUrl.TrimEnd('/')
            : "mailto:notifications@lanyard.invalid";

        if (!string.IsNullOrEmpty(options.PublicKey) && !string.IsNullOrEmpty(options.PrivateKey))
        {
            return new VapidKeys(options.PublicKey, options.PrivateKey, subject);
        }

        if (isDevelopment)
        {
            // Throwaway keys so push can be tried locally without setup. Subscriptions made with them
            // stop working at the next restart; lanyardPush.sync notices the key changed and
            // re-subscribes the browser.
            (string publicKey, string privateKey) = Generate();
            logger.LogWarning("No Push:PublicKey/Push:PrivateKey configured; using a temporary VAPID key pair for this run");

            return new VapidKeys(publicKey, privateKey, subject);
        }

        logger.LogWarning("Push notifications are off: Push:PublicKey and Push:PrivateKey are not configured");

        return new VapidKeys(null, null, subject);
    }

    // A P-256 key pair in the base64url form browsers and the push library expect: the public key
    // as the 65-byte uncompressed point, the private key as the 32-byte scalar.
    public static (string PublicKey, string PrivateKey) Generate()
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = ecdsa.ExportParameters(includePrivateParameters: true);

        byte[] publicKey = new byte[65];
        publicKey[0] = 0x04;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);

        return (Base64Url(publicKey), Base64Url(parameters.D!));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
