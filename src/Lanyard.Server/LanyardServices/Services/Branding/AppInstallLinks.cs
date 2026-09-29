using Lanyard.Application.Services.Locations;
using System.Security.Cryptography;
using System.Text;

namespace Lanyard.Application.Services.Branding;

/// <summary>
/// The manifest, iPhone home-screen icon and app name a page links to, so an install picks up the
/// company's name and logo. Shared by App.razor (the server-rendered &lt;head&gt;) and the pages that
/// learn the company later in the circuit (MainLayout, Login), which swap them over via
/// lanyardSetAppCompany.
/// </summary>
public record AppInstallLinks(string ManifestHref, string AppleTouchIconHref, string AppName)
{
    // Remembers the last company seen on this device, so a signed-out page (the bare /login an
    // installed app opens on after logout) still links that company's manifest. Otherwise Android's
    // periodic manifest check would find the default one and swap the icon and name back to Lanyard's.
    public const string CompanyCookieName = "lanyard.app-company";

    public const string DefaultAppName = "Lanyard";

    public static AppInstallLinks Default { get; } = new("manifest.webmanifest", "apple-touch-icon.png", DefaultAppName);

    // Every company gets its own manifest, logo or not, so the installed app carries its name. The
    // ?v= changes whenever anything in that manifest does, so the hour-long response cache never
    // serves a stale name, colour or icon.
    public static AppInstallLinks For(CompanyBrandingInfo branding)
    {
        string manifestHref = $"/api/companies/{branding.CompanyId}/manifest.webmanifest?v={ManifestVersion(branding)}";

        string appleTouchIconHref = branding.LogoFileId is Guid logo
            ? $"/api/companies/{branding.CompanyId}/app-icon/180?v={logo:N}"
            : Default.AppleTouchIconHref;

        return new(manifestHref, appleTouchIconHref, AppNameFor(branding.Name));
    }

    public static string AppNameFor(string? companyName) =>
        string.IsNullOrWhiteSpace(companyName) ? DefaultAppName : companyName.Trim();

    // A stable hash rather than string.GetHashCode, which changes every process start and would
    // bust every device's cached manifest on each deploy.
    private static string ManifestVersion(CompanyBrandingInfo branding)
    {
        string source = $"{AppNameFor(branding.Name)}|{branding.ThemeColorHex}|{branding.LogoFileId:N}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];
    }
}
