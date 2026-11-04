using System.Security.Cryptography;
using System.Text.Json;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.DataProtection;

namespace Lanyard.Application.Services.Authentication;

public interface IKioskMediaTokenService
{
    // How long a media token works. The kiosk asks for a new one well before this runs out.
    TimeSpan Lifetime { get; }

    string Issue(KioskIdentity kiosk);

    // The credential the token was issued for. The caller must still check that credential is
    // live (IKioskCredentialService.ValidateByIdAsync), so revoking a kiosk kills its media tokens too.
    Result<Guid> Unprotect(string? token);
}

// A short-lived, signed stand-in for a kiosk's credential, for the one place a kiosk can't send a
// header: the song and audio-clip URLs it hands straight to Media Foundation to stream. Only those
// two endpoints accept it (KioskAuthenticationHandler.AcceptsMediaToken), so a leaked URL can play
// one company's audio for a few hours and do nothing else.
public class KioskMediaTokenService(IDataProtectionProvider dataProtectionProvider, TimeProvider timeProvider) : IKioskMediaTokenService
{
    private const string Purpose = "Lanyard.KioskMediaToken.v1";

    private readonly ITimeLimitedDataProtector _protector = dataProtectionProvider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
    private readonly TimeProvider _timeProvider = timeProvider;

    public TimeSpan Lifetime => TimeSpan.FromHours(12);

    public string Issue(KioskIdentity kiosk)
    {
        string payload = JsonSerializer.Serialize(new MediaTokenPayload(kiosk.CredentialId));

        return _protector.Protect(payload, _timeProvider.GetUtcNow().Add(Lifetime));
    }

    public Result<Guid> Unprotect(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Result<Guid>.Fail("No media token.");
        }

        try
        {
            string payload = _protector.Unprotect(token, out DateTimeOffset expiration);

            if (expiration <= _timeProvider.GetUtcNow())
            {
                return Result<Guid>.Fail("The media token has expired.");
            }

            MediaTokenPayload? parsed = JsonSerializer.Deserialize<MediaTokenPayload>(payload);

            return parsed is null || parsed.CredentialId == Guid.Empty
                ? Result<Guid>.Fail("The media token is malformed.")
                : Result<Guid>.Ok(parsed.CredentialId);
        }
        catch (CryptographicException)
        {
            // Tampered with, expired, or signed with a key this server doesn't have.
            return Result<Guid>.Fail("The media token is not valid.");
        }
        catch (JsonException)
        {
            return Result<Guid>.Fail("The media token is malformed.");
        }
    }

    private sealed record MediaTokenPayload(Guid CredentialId);
}
