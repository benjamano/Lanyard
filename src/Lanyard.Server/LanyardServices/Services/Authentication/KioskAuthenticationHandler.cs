using System.Security.Claims;
using System.Text.Encodings.Web;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Lanyard.Application.Services.Authentication;

// Works out which kiosk is calling. In order:
//
//  1. Authorization: Bearer lk1.<id>.<secret> - the kiosk's own credential. Its principal carries
//     the kiosk's company claim, so TenantContext filters everything the request touches to that
//     company, exactly as it does for a signed-in user.
//  2. ?mt=<media token> - only on the song and audio-clip streaming URLs (see KioskMediaTokenService).
//  3. The old shared secret (X-Lanyard-Client-Secret header or ?secret=), unless
//     Clients:LegacySecretMode is Reject. The kiosk is whatever id it claims; when that id is a
//     kiosk the server knows, its company still turns the tenant filter on.
//
// Nothing here issues role claims, so a kiosk can never pass a staff [Authorize(Roles = ...)] check.
public class KioskAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IKioskCredentialService credentialService,
    IKioskMediaTokenService mediaTokenService,
    IClientSecretValidator legacySecretValidator,
    IConfiguration configuration,
    IMemoryCache cache) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private const string BearerPrefix = "Bearer ";

    // A kiosk still on the shared secret reconnects every few seconds when the server is down;
    // one warning per kiosk an hour is enough to know it hasn't moved over yet.
    private static readonly TimeSpan LegacyWarningInterval = TimeSpan.FromHours(1);

    private bool _credentialRejected;

    public static KioskLegacySecretMode GetLegacySecretMode(IConfiguration configuration) =>
        Enum.TryParse(configuration["Clients:LegacySecretMode"], ignoreCase: true, out KioskLegacySecretMode mode)
            ? mode
            : KioskLegacySecretMode.AcceptAndExchange;

    // The two URLs a kiosk hands straight to Media Foundation, which can't send headers.
    public static bool AcceptsMediaToken(PathString path)
    {
        string value = path.Value ?? string.Empty;

        return value.StartsWith("/api/music/audio/", StringComparison.OrdinalIgnoreCase)
            || (value.StartsWith("/api/audioclips/", StringComparison.OrdinalIgnoreCase) && value.EndsWith("/audio", StringComparison.OrdinalIgnoreCase));
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string authorization = Request.Headers.Authorization.ToString();

        if (authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return await AuthenticateCredentialAsync(authorization[BearerPrefix.Length..].Trim());
        }

        string mediaToken = Request.Query[KioskAuthenticationDefaults.MediaTokenQuery].ToString();

        if (!string.IsNullOrEmpty(mediaToken) && AcceptsMediaToken(Request.Path))
        {
            return await AuthenticateMediaTokenAsync(mediaToken);
        }

        string legacySecret = Request.Headers[ClientConnectionQuery.SecretHeader].ToString();

        if (string.IsNullOrEmpty(legacySecret))
        {
            legacySecret = Request.Query[ClientConnectionQuery.Secret].ToString();
        }

        return await AuthenticateLegacySecretAsync(legacySecret);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        DisableStatusCodePages();

        Response.StatusCode = StatusCodes.Status401Unauthorized;

        // invalid_token tells a kiosk its stored credential is dead (revoked, or from another
        // server), so it should drop it and pair again rather than keep retrying.
        Response.Headers.Append(HeaderNames.WWWAuthenticate, _credentialRejected ? "Bearer error=\"invalid_token\"" : "Bearer");

        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        DisableStatusCodePages();

        return base.HandleForbiddenAsync(properties);
    }

    // A kiosk needs the bare status code. Without this the app's status-code pages re-run the
    // request as the "not found" page, which turns the hub's 401 (a POST) into a 400.
    private void DisableStatusCodePages()
    {
        if (Context.Features.Get<IStatusCodePagesFeature>() is IStatusCodePagesFeature statusCodePages)
        {
            statusCodePages.Enabled = false;
        }
    }

    private async Task<AuthenticateResult> AuthenticateCredentialAsync(string token)
    {
        Result<KioskIdentity> result = await credentialService.ValidateAsync(token, Context.RequestAborted);

        if (!result.IsSuccess || result.Data is null)
        {
            _credentialRejected = true;

            Logger.LogWarning("Kiosk request to {Path} from {IpAddress} rejected: {Reason}", Request.Path.Value, RemoteIp, result.Error);

            return AuthenticateResult.Fail("Invalid kiosk credential.");
        }

        return Success(result.Data.ClientId, result.Data.CompanyId, result.Data.CredentialId, KioskAuthenticationDefaults.CredentialAuthenticationType);
    }

    private async Task<AuthenticateResult> AuthenticateMediaTokenAsync(string token)
    {
        Result<Guid> credentialId = mediaTokenService.Unprotect(token);

        Result<KioskIdentity> result = credentialId.IsSuccess
            ? await credentialService.ValidateByIdAsync(credentialId.Data, Context.RequestAborted)
            : Result<KioskIdentity>.Fail(credentialId.Error ?? "Invalid media token.");

        if (!result.IsSuccess || result.Data is null)
        {
            Logger.LogWarning("Kiosk media request to {Path} from {IpAddress} rejected: {Reason}", Request.Path.Value, RemoteIp, result.Error);

            return AuthenticateResult.Fail("Invalid media token.");
        }

        return Success(result.Data.ClientId, result.Data.CompanyId, result.Data.CredentialId, KioskAuthenticationDefaults.MediaTokenAuthenticationType);
    }

    private async Task<AuthenticateResult> AuthenticateLegacySecretAsync(string providedSecret)
    {
        if (GetLegacySecretMode(configuration) == KioskLegacySecretMode.Reject)
        {
            if (!string.IsNullOrEmpty(providedSecret))
            {
                Logger.LogWarning("Kiosk request to {Path} from {IpAddress} rejected: the shared secret is switched off (Clients:LegacySecretMode is Reject)", Request.Path.Value, RemoteIp);
            }

            return AuthenticateResult.NoResult();
        }

        ClientSecretAuthorizationOutcome outcome = legacySecretValidator.Authorize(providedSecret);

        if (outcome == ClientSecretAuthorizationOutcome.Denied)
        {
            if (string.IsNullOrEmpty(providedSecret))
            {
                return AuthenticateResult.NoResult();
            }

            Logger.LogWarning("Kiosk request to {Path} from {IpAddress} rejected: invalid shared secret", Request.Path.Value, RemoteIp);

            return AuthenticateResult.Fail("Invalid shared secret.");
        }

        if (outcome == ClientSecretAuthorizationOutcome.AllowedUnconfiguredDevelopment)
        {
            Logger.LogWarning("Clients:SharedSecret is not configured; allowing anonymous kiosk access to {Path} from {IpAddress} because the environment is Development. This would be rejected in any other environment.", Request.Path.Value, RemoteIp);
        }

        ClaimsIdentity identity = new(KioskAuthenticationDefaults.LegacySecretAuthenticationType);

        Guid? claimedClientId = ClaimedClientId();

        if (claimedClientId is Guid clientId)
        {
            Result<int> company = await credentialService.GetClientCompanyIdAsync(clientId, Context.RequestAborted);

            // Only a kiosk the server already knows gets an identity. An unknown id stays an
            // unidentified shared-secret caller: it can still fetch updates, but the hub turns it
            // away (it must be paired now) instead of creating a Client row for it.
            if (company.IsSuccess)
            {
                identity.AddClaim(new Claim(KioskClaimTypes.ClientId, clientId.ToString()));
                identity.AddClaim(new Claim(LocationClaimTypes.CompanyId, company.Data.ToString()));
            }
        }

        WarnLegacyUse(claimedClientId);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    // The hub sends the id as ?clientId=; the kiosk REST routes carry it as {clientId}.
    private Guid? ClaimedClientId()
    {
        string? raw = Request.Query[ClientConnectionQuery.ClientId].ToString();

        if (string.IsNullOrEmpty(raw))
        {
            raw = Request.RouteValues.TryGetValue("clientId", out object? routeValue) ? routeValue?.ToString() : null;
        }

        return Guid.TryParse(raw, out Guid clientId) ? clientId : null;
    }

    private void WarnLegacyUse(Guid? clientId)
    {
        string key = $"kiosk-legacy-warning:{clientId?.ToString("N") ?? RemoteIp}";

        if (cache.TryGetValue(key, out _))
        {
            return;
        }

        cache.Set(key, true, LegacyWarningInterval);

        Logger.LogWarning("Kiosk {ClientId} from {IpAddress} authenticated to {Path} with the old shared secret. Update it so it gets its own credential before Clients:LegacySecretMode is set to Reject", clientId?.ToString() ?? "(no id)", RemoteIp, Request.Path.Value);
    }

    private AuthenticateResult Success(Guid clientId, int companyId, Guid credentialId, string authenticationType)
    {
        ClaimsIdentity identity = new(
        [
            new Claim(KioskClaimTypes.ClientId, clientId.ToString()),
            new Claim(KioskClaimTypes.CredentialId, credentialId.ToString()),
            new Claim(LocationClaimTypes.CompanyId, companyId.ToString())
        ], authenticationType);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private string RemoteIp => Context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
}
