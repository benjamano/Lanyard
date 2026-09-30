using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Identity;

namespace Lanyard.Application.Services.Authentication;

/// <summary>
/// Stores and manages passkeys - the credential behind "sign in with Face ID / fingerprint".
/// </summary>
/// <remarks>
/// The WebAuthn ceremonies themselves (creating options, checking what the browser sent back)
/// live in AuthController, because Identity keeps their state in a cookie on the HTTP request.
/// This service covers everything either side of them: whether a user may add another passkey,
/// saving one once it has been verified, and listing/removing them from Account Management.
/// </remarks>
public interface IPasskeyService
{
    Task<Result<bool>> CanAddPasskeyAsync(string userId);
    Task<Result<string>> SavePasskeyAsync(string userId, UserPasskeyInfo passkey, string? userAgent);
    Task<Result<List<PasskeySummaryDto>>> GetMyPasskeysAsync();
    Task<Result<bool>> RemoveMyPasskeyAsync(string passkeyId);
}
