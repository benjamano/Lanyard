namespace Lanyard.Application.Services.Branding;

/// <summary>
/// The manifest and iPhone home-screen icon a page links to, so an install picks up the company's
/// logo. Shared by App.razor (the server-rendered &lt;head&gt;) and the pages that learn the company
/// later in the circuit (MainLayout, Login), which swap the links over via lanyardSetAppCompany.
/// </summary>
public record AppInstallLinks(string ManifestHref, string AppleTouchIconHref)
{
    // Remembers the last company seen on this device, so a signed-out page (the bare /login an
    // installed app opens on after logout) still links that company's manifest. Otherwise Android's
    // periodic manifest check would find the default one and swap the icon back to the λ.
    public const string CompanyCookieName = "lanyard.app-company";

    public static AppInstallLinks Default { get; } = new("manifest.webmanifest", "apple-touch-icon.png");

    public static AppInstallLinks For(int companyId, Guid? logoFileId) => logoFileId is Guid logo
        ? new($"/api/companies/{companyId}/manifest.webmanifest?v={logo:N}", $"/api/companies/{companyId}/app-icon/180?v={logo:N}")
        : Default;
}
