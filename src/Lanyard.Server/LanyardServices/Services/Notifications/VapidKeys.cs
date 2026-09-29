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

        // Pasted secrets often pick up a stray space or newline, which the push services reject.
        string? configuredPublic = options.PublicKey?.Trim();
        string? configuredPrivate = options.PrivateKey?.Trim();

        if (!string.IsNullOrEmpty(configuredPublic) && !string.IsNullOrEmpty(configuredPrivate))
        {
            // A private key that doesn't belong to the public key still lets browsers subscribe, but
            // every push is then refused (FCM: "invalid JWT", Mozilla: "InvalidSignature"). Catch it
            // here with a clear message instead of as a failed test send.
            if (!IsMatchingPair(configuredPublic, configuredPrivate))
            {
                logger.LogError("Push notifications are off: Push:PrivateKey does not match Push:PublicKey. Generate a fresh pair and set both together");

                return new VapidKeys(null, null, subject);
            }

            return new VapidKeys(configuredPublic, configuredPrivate, subject);
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

    // True when both keys are well-formed and the private key signs data the public key verifies.
    public static bool IsMatchingPair(string publicKey, string privateKey)
    {
        try
        {
            byte[] pub = FromBase64Url(publicKey);
            byte[] priv = FromBase64Url(privateKey);

            if (pub.Length != 65 || pub[0] != 0x04 || priv.Length != 32)
            {
                return false;
            }

            ECPoint q = new() { X = pub[1..33], Y = pub[33..] };

            using ECDsa verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = q });

            // Import the private scalar alone so the platform derives its own public point, rather
            // than trusting the configured one.
            using ECDsa signer = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = priv });

            byte[] data = "lanyard-vapid-check"u8.ToArray();

            return verifier.VerifyData(data, signer.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static byte[] FromBase64Url(string value)
    {
        string base64 = value.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
