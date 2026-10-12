using System.Text.RegularExpressions;

namespace Lanyard.Shared.Diagnostics;

/// <summary>
/// Strips credentials out of log text before it leaves the kiosk in a diagnostics log tail.
/// Kiosk URLs carry the shared secret as <c>?secret=</c>, and the projection and video publisher
/// windows log URLs with <c>token=</c>, so key/value pairs are redacted by name. Known secrets
/// are also removed wherever they appear, raw or URL-encoded. The output is idempotent, so the
/// server can run it again on what the kiosk sends.
/// </summary>
public static class LogRedactor
{
    public const string Placeholder = "[REDACTED]";

    // Shorter "secrets" would match ordinary words and shred the log line for no benefit.
    private const int MinimumLiteralSecretLength = 4;

    private static readonly Regex SensitiveParameter = new(
        @"(?<name>\b(?:secret|token|access_token|refresh_token|api_key|apikey|password|pwd|key|sig|signature)=)[^&\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BearerToken = new(
        @"(?<name>\bBearer\s+)[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string? text, IEnumerable<string?>? knownSecrets = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string redacted = text;

        if (knownSecrets != null)
        {
            foreach (string? secret in knownSecrets)
            {
                if (string.IsNullOrEmpty(secret) || secret.Length < MinimumLiteralSecretLength)
                {
                    continue;
                }

                redacted = redacted.Replace(secret, Placeholder, StringComparison.Ordinal);

                string encoded = Uri.EscapeDataString(secret);

                if (encoded != secret)
                {
                    redacted = redacted.Replace(encoded, Placeholder, StringComparison.Ordinal);
                }
            }
        }

        redacted = SensitiveParameter.Replace(redacted, match => match.Groups["name"].Value + Placeholder);
        redacted = BearerToken.Replace(redacted, match => match.Groups["name"].Value + Placeholder);

        return redacted;
    }
}
