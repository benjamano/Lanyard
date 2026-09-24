using Microsoft.AspNetCore.Http;

namespace Lanyard.Application.Services.Scheduling;

// The cookie a paired clock-in tablet carries. HttpOnly so no script on any page can read it, and
// long-lived because a wall-mounted tablet shouldn't need re-pairing - revoking it server-side is
// how a lost or retired device is cut off.
public static class TerminalCookie
{
    public const string Name = "lanyard_terminal";

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(400);

    public static CookieOptions Options(bool isHttps) => new()
    {
        HttpOnly = true,
        Secure = isHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = "/",
        Expires = DateTimeOffset.UtcNow.Add(Lifetime)
    };
}
