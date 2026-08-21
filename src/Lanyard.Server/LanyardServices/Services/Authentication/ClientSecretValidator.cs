using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Authentication
{
    /// <summary>
    /// The result of deciding whether a caller may proceed based on the shared secret it
    /// presented. This is the single decision point both the client-facing REST endpoints and the
    /// SignalR hub's connection-gating middleware consult, so the two paths cannot drift apart.
    /// </summary>
    public enum ClientSecretAuthorizationOutcome
    {
        /// <summary>A configured secret was presented and matched.</summary>
        Allowed,

        /// <summary>
        /// No secret is configured, but the environment is Development, so the request is let
        /// through anyway to preserve local kiosk workflows. Callers must log a Warning on every
        /// request that returns this outcome.
        /// </summary>
        AllowedUnconfiguredDevelopment,

        /// <summary>
        /// The caller must be rejected: either a secret is configured and the caller's did not
        /// match, or no secret is configured and the environment is not Development.
        /// </summary>
        Denied
    }

    /// <summary>
    /// Validates the pre-shared secret that kiosk clients present when they connect to the
    /// SignalR hub or fetch content from the client-facing API endpoints. The clients have no
    /// interactive user login, so this shared secret is what distinguishes a genuine client
    /// from an anonymous caller who merely guessed a client-ID GUID.
    ///
    /// The secret is read from configuration key <c>Clients:SharedSecret</c> (supplied in
    /// production via the <c>Clients__SharedSecret</c> environment variable). Outside Development,
    /// the host refuses to start at all if this is unset (see Program.cs), so
    /// <see cref="IsConfigured"/> can only be false there while running in Development, where
    /// <see cref="Authorize"/> deliberately stays permissive for local kiosk work.
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

        /// <summary>
        /// The single access decision for a caller presenting <paramref name="provided"/> as its
        /// shared secret. Both the client REST endpoints and the SignalR hub gate must call this
        /// rather than re-implementing the configured/unconfigured branching themselves.
        /// </summary>
        ClientSecretAuthorizationOutcome Authorize(string? provided);
    }

    public class ClientSecretValidator : IClientSecretValidator
    {
        private readonly byte[]? _secretBytes;
        private readonly bool _isDevelopment;

        public ClientSecretValidator(IConfiguration configuration, ILogger<ClientSecretValidator> logger, IHostEnvironment environment)
        {
            string? secret = configuration["Clients:SharedSecret"];
            _isDevelopment = environment.IsDevelopment();

            if (string.IsNullOrWhiteSpace(secret))
            {
                _secretBytes = null;

                if (_isDevelopment)
                {
                    logger.LogWarning(
                        "Clients:SharedSecret is not configured. Because the environment is Development, "
                        + "the SignalR hub and client API endpoints will accept anonymous clients, with a "
                        + "warning logged on every such request. Set the Clients__SharedSecret environment "
                        + "variable to require kiosk clients to authenticate.");
                }
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

        public ClientSecretAuthorizationOutcome Authorize(string? provided)
        {
            if (IsConfigured)
            {
                return IsValid(provided) ? ClientSecretAuthorizationOutcome.Allowed : ClientSecretAuthorizationOutcome.Denied;
            }

            return _isDevelopment
                ? ClientSecretAuthorizationOutcome.AllowedUnconfiguredDevelopment
                : ClientSecretAuthorizationOutcome.Denied;
        }
    }
}
