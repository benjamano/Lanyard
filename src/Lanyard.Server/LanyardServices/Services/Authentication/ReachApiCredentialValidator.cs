using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Authentication
{
    /// <summary>
    /// Validates the credential that the public-facing site (Lanyard.Reach.Web) presents when it
    /// calls the ordering API on a customer's behalf.
    ///
    /// Deliberately a *separate* secret from <see cref="IClientSecretValidator"/>'s
    /// <c>Clients:SharedSecret</c>, rather than reusing it. Reach is internet-facing and serves
    /// anonymous customers; kiosk clients drive DMX, projection and music on the venue floor.
    /// Sharing one secret would mean a compromise of the public web host handed an attacker
    /// control of the lighting rig. Two secrets means one blast radius each.
    ///
    /// Read from configuration key <c>Reach:SharedSecret</c> (supplied in production via the
    /// <c>Reach__SharedSecret</c> environment variable). As with the kiosk secret, an unset value
    /// is tolerated only in Development, and every request it lets through logs a warning.
    /// </summary>
    public interface IReachApiCredentialValidator
    {
        bool IsConfigured { get; }

        /// <summary>
        /// True when the request carries a credential matching the configured Reach secret, or
        /// when no secret is configured and the environment is Development.
        /// </summary>
        bool IsAuthorized(HttpContext httpContext);
    }

    public class ReachApiCredentialValidator : IReachApiCredentialValidator
    {
        /// <summary>
        /// Header name only - no query-string fallback, unlike the kiosk path. Kiosk clients need
        /// one because SignalR's negotiate cannot set headers; Reach is a server calling a server
        /// and always can. Keeping it out of the query string keeps it out of access logs.
        /// </summary>
        public const string SecretHeaderName = "X-Lanyard-Reach-Secret";

        private readonly byte[]? _secretBytes;
        private readonly bool _isDevelopment;
        private readonly ILogger<ReachApiCredentialValidator> _logger;

        public ReachApiCredentialValidator(
            IConfiguration configuration,
            ILogger<ReachApiCredentialValidator> logger,
            IHostEnvironment environment)
        {
            _logger = logger;
            _isDevelopment = environment.IsDevelopment();

            string? secret = configuration["Reach:SharedSecret"];

            if (string.IsNullOrWhiteSpace(secret))
            {
                _secretBytes = null;

                if (_isDevelopment)
                {
                    logger.LogWarning(
                        "Reach:SharedSecret is not configured. Because the environment is Development, "
                        + "the ordering API will accept unauthenticated callers, with a warning logged on "
                        + "every such request. Set the Reach__SharedSecret environment variable to require "
                        + "the public site to authenticate.");
                }
            }
            else
            {
                _secretBytes = Encoding.UTF8.GetBytes(secret);
            }
        }

        public bool IsConfigured => _secretBytes is not null;

        public bool IsAuthorized(HttpContext httpContext)
        {
            string remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
            string path = httpContext.Request.Path.ToString();

            if (_secretBytes is null)
            {
                if (_isDevelopment)
                {
                    _logger.LogWarning(
                        "Reach:SharedSecret is not configured; allowing unauthenticated ordering request to {Path} from {IpAddress} because the environment is Development. This would be rejected in any other environment.",
                        path, remoteIp);

                    return true;
                }

                _logger.LogWarning("Ordering request to {Path} from {IpAddress} rejected: no Reach secret is configured", path, remoteIp);

                return false;
            }

            string provided = httpContext.Request.Headers[SecretHeaderName].ToString();

            if (string.IsNullOrEmpty(provided))
            {
                _logger.LogWarning("Ordering request to {Path} from {IpAddress} rejected: missing Reach secret", path, remoteIp);

                return false;
            }

            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), _secretBytes))
            {
                _logger.LogWarning("Ordering request to {Path} from {IpAddress} rejected: invalid Reach secret", path, remoteIp);

                return false;
            }

            return true;
        }
    }
}
