using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Authentication;

// Company-wide "everyone must use two-factor authentication" switch. Managers and Admins turn it
// on; RouteAuthorizationGate then holds anyone at that company without 2FA on a set-up screen
// until they enable it.
public interface ITwoFactorPolicyService
{
    // The signed-in manager's company, with each of its staff and whether they have 2FA on.
    // Admin or Manager only.
    Task<Result<TwoFactorPolicyDto>> GetPolicyForCurrentCompanyAsync();

    // Admin or Manager only, and only for the company they're signed in to.
    Task<Result<bool>> SetRequiredForCurrentCompanyAsync(bool isRequired);

    // Whether the signed-in user's company makes 2FA compulsory. False when signed out, in the
    // demo, or when the user has no company for this session.
    Task<Result<bool>> IsRequiredForCurrentUserAsync();

    // True only when the company requires 2FA and the signed-in user hasn't turned it on yet.
    // Never throws; a failed check is logged and treated as "not required" so a database hiccup
    // can't lock every member of staff out of every page.
    Task<bool> IsSetupRequiredForCurrentUserAsync();
}
