using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Authentication
{
    /// <summary>
    /// Validates the pre-shared secret that kiosk clients present when they connect to the
    /// SignalR hub or fetch content from the client-facing API endpoints. The clients have no
    /// interactive user login, so this shared secret is what distinguishes a genuine client
    /// from an anonymous caller who merely guessed a client-ID GUID.
    ///
    /// The secret is read from configuration key <c>Clients:SharedSecret</c> (supplied in
    /// production via the <c>Clients__SharedSecret</c> environment variable). If no secret is
    /// configured the validator reports <see cref="IsConfigured"/> = false and callers fall back
    /// to open access with a warning, preserving backwards compatibility for local development.
    /// </summary>
    public interface IClientSecretValidator
    {
        /// <summary>True when a non-empty shared secret has been configured.</summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Returns true when <paramref name="provided"/> matches the configured secret using a
        /// constant-time comparison. Always false when no secret is configured or the input is null.
        /// </summary>
        bool IsValid(string? provided);
    }

    public class ClientSecretValidator : IClientSecretValidator
    {
        private readonly byte[]? _secretBytes;

        public ClientSecretValidator(IConfiguration configuration, ILogger<ClientSecretValidator> logger)
        {
            string? secret = configuration["Clients:SharedSecret"];

            if (string.IsNullOrWhiteSpace(secret))
            {
                _secretBytes = null;

                logger.LogWarning(
                    "Clients:SharedSecret is not configured. The SignalR hub and client API endpoints "
                    + "will accept anonymous clients. Set the Clients__SharedSecret environment variable "
                    + "to require kiosk clients to authenticate.");
            }
            else
            {
                _secretBytes = Encoding.UTF8.GetBytes(secret);
            }
        }

        public bool IsConfigured => _secretBytes is not null;

        public bool IsValid(string? provided)
        {
            if (_secretBytes is null || string.IsNullOrEmpty(provided))
            {
                return false;
            }

            byte[] providedBytes = Encoding.UTF8.GetBytes(provided);

            return CryptographicOperations.FixedTimeEquals(providedBytes, _secretBytes);
        }
    }
}
