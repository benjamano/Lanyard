using System.Security.Claims;
using System.Text.Encodings.Web;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
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
//
// The old shared kiosk secret is no longer accepted: a kiosk without its own credential pairs.
//
// Nothing here issues role claims, so a kiosk can never pass a staff [Authorize(Roles = ...)] check.
public class KioskAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IKioskCredentialService credentialService,
    IKioskMediaTokenService mediaTokenService) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private const string BearerPrefix = "Bearer ";

    private bool _credentialRejected;

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

        return AuthenticateResult.NoResult();
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

        if (result.Error == KioskCredentialService.CheckUnavailableError)
        {
            // A 500, not a 401: the kiosk must keep its credential and simply try again.
            throw new InvalidOperationException(KioskCredentialService.CheckUnavailableError);
        }

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

        if (result.Error == KioskCredentialService.CheckUnavailableError)
        {
            throw new InvalidOperationException(KioskCredentialService.CheckUnavailableError);
        }

        if (!result.IsSuccess || result.Data is null)
        {
            Logger.LogWarning("Kiosk media request to {Path} from {IpAddress} rejected: {Reason}", Request.Path.Value, RemoteIp, result.Error);

            return AuthenticateResult.Fail("Invalid media token.");
        }

        return Success(result.Data.ClientId, result.Data.CompanyId, result.Data.CredentialId, KioskAuthenticationDefaults.MediaTokenAuthenticationType);
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
