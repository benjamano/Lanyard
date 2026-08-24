using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Authentication
{
    /// <summary>
    /// Authorises requests to the client-facing API endpoints (music audio, file downloads/listing).
    /// These are consumed both by signed-in staff (via the auth cookie) and by kiosk clients (which
    /// have no user login and instead present the shared secret). A request is allowed when it is
    /// either from an authenticated user or carries a valid client secret.
    ///
    /// The shared-secret decision itself is delegated to <see cref="IClientSecretValidator.Authorize"/>
    /// so this REST path and the SignalR hub's connection-gating middleware (in Program.cs) can never
    /// decide the unconfigured-secret case differently.
    /// </summary>
    public static class ClientRequestAuthorization
    {
        public const string SecretHeaderName = "X-Lanyard-Client-Secret";
        public const string SecretQueryName = "secret";

        // ILoggerFactory is an app-wide singleton, so the logger it produces for this fixed
        // category name is the same on every call - resolving and creating it fresh per request
        // was pure overhead on the kiosk file/audio endpoints, which anonymous clients hit
        // repeatedly. Cached lazily rather than injected, since this stays a static helper.
        private static ILogger? _logger;

        public static bool IsAuthorized(HttpContext httpContext, IClientSecretValidator clientSecretValidator)
        {
            if (httpContext.User?.Identity?.IsAuthenticated == true)
            {
                return true;
            }

            string? providedSecret = httpContext.Request.Headers[SecretHeaderName].ToString();

            if (string.IsNullOrEmpty(providedSecret))
            {
                providedSecret = httpContext.Request.Query[SecretQueryName].ToString();
            }

            ILogger logger = _logger ??= httpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Lanyard.Application.Services.Authentication.ClientRequestAuthorization");

            string remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";

            return EvaluateAndLog(clientSecretValidator, providedSecret, logger, remoteIp, httpContext.Request.Path.ToString());
        }

        /// <summary>
        /// The single decision-plus-logging point shared by the REST helper above and the
        /// SignalR hub's /websocket-gating middleware in Program.cs. Both callers pass in the
        /// caller's remote IP and request path purely for the log message; the allow/deny
        /// decision itself always comes from <see cref="IClientSecretValidator.Authorize"/>.
        /// </summary>
        public static bool EvaluateAndLog(IClientSecretValidator clientSecretValidator, string? providedSecret, ILogger logger, string remoteIp, string requestPath)
        {
            ClientSecretAuthorizationOutcome outcome = clientSecretValidator.Authorize(providedSecret);

            switch (outcome)
            {
                case ClientSecretAuthorizationOutcome.Allowed:
                    return true;

                case ClientSecretAuthorizationOutcome.AllowedUnconfiguredDevelopment:
                    logger.LogWarning(
                        "Clients:SharedSecret is not configured; allowing anonymous client access to {Path} from {IpAddress} because the environment is Development. This would be rejected in any other environment.",
                        requestPath, remoteIp);
                    return true;

                default:
                    logger.LogWarning(
                        "Client request to {Path} from {IpAddress} rejected: missing or invalid shared secret",
                        requestPath, remoteIp);
                    return false;
            }
        }
    }
}
