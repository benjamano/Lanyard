using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;

namespace Lanyard.Application.Services.Authentication;

/// <summary>
/// The per-user date/time format travels in ASP.NET's standard culture cookie, written from
/// <c>UserProfile.PreferredCulture</c> at login and whenever the preference is saved. The
/// request-localization middleware in Program.cs reads it for every request, and a Blazor
/// circuit inherits the culture of the request that opened it - so one cookie covers
/// prerendering, the interactive circuit and API calls. This replaced App.razor looking the
/// user up on every request and setting the process-wide default culture, which made the last
/// user to load a page set everyone else's date format.
/// </summary>
public static class UserCultureCookie
{
    public const string DefaultCulture = "en-GB";
    public static readonly string[] SupportedCultures = ["en-GB", "en-US"];

    public static string CookieName => CookieRequestCultureProvider.DefaultCookieName;

    /// <summary>The value the culture cookie must carry for a user's preference (falls back to <see cref="DefaultCulture"/>).</summary>
    public static string MakeCookieValue(string? preferredCulture)
    {
        string culture = Normalise(preferredCulture);
        return CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture, culture));
    }

    public static void Append(HttpResponse response, string? preferredCulture)
    {
        response.Cookies.Append(CookieName, MakeCookieValue(preferredCulture), new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddYears(1),
            IsEssential = true,
            HttpOnly = false,
            SameSite = SameSiteMode.Lax,
            Secure = response.HttpContext.Request.IsHttps,
            Path = "/"
        });
    }

    /// <summary>A <c>document.cookie</c> assignment string, for the interactive account page which has no HTTP response to write to.</summary>
    public static string BuildDocumentCookie(string? preferredCulture)
    {
        return $"{CookieName}={Uri.EscapeDataString(MakeCookieValue(preferredCulture))}; path=/; max-age={365 * 24 * 60 * 60}; samesite=lax";
    }

    public static string Normalise(string? preferredCulture)
    {
        if (string.IsNullOrWhiteSpace(preferredCulture))
        {
            return DefaultCulture;
        }

        // Only the cultures the localization middleware is configured with; anything else would
        // be dropped by it anyway. (CultureInfo.GetCultureInfo can't be used as a validity check:
        // on ICU it happily constructs a culture for almost any string.)
        foreach (string supported in SupportedCultures)
        {
            if (string.Equals(supported, preferredCulture, StringComparison.OrdinalIgnoreCase))
            {
                return supported;
            }
        }

        return DefaultCulture;
    }
}
