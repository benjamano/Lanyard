using Microsoft.AspNetCore.Http;

namespace Lanyard.Application.Services.Authentication
{
    /// <summary>
    /// Authorises requests to the client-facing API endpoints (music audio, file downloads/listing).
    /// These are consumed both by signed-in staff (via the auth cookie) and by kiosk clients (which
    /// have no user login and instead present the shared secret). A request is allowed when it is
    /// either from an authenticated user or carries a valid client secret. When no secret is
    /// configured the endpoints stay open for backwards-compatible local development.
    /// </summary>
    public static class ClientRequestAuthorization
    {
        public const string SecretHeaderName = "X-Lanyard-Client-Secret";
        public const string SecretQueryName = "secret";

        public static bool IsAuthorized(HttpContext httpContext, IClientSecretValidator clientSecretValidator)
        {
            if (httpContext.User?.Identity?.IsAuthenticated == true)
            {
                return true;
            }

            // No secret configured => preserve open access (dev / not-yet-hardened deployments).
            if (!clientSecretValidator.IsConfigured)
            {
                return true;
            }

            string? providedSecret = httpContext.Request.Headers[SecretHeaderName].ToString();

            if (string.IsNullOrEmpty(providedSecret))
            {
                providedSecret = httpContext.Request.Query[SecretQueryName].ToString();
            }

            return clientSecretValidator.IsValid(providedSecret);
        }
    }
}
