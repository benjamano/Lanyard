using Lanyard.Application.Services.Training;

namespace Lanyard.Application.Services.Email;

public static class EmailBranding
{
    // The company logo as an absolute URL an email client can fetch, or null when there's no logo.
    // Cache-keyed by the logo file id (see MainLayout.ApplyBrandingAsync), so replacing the logo
    // gives a new URL and a fresh fetch.
    public static string? LogoUrl(EmailOptions options, TrainingBranding branding) =>
        branding is { CompanyId: int companyId, LogoFileId: Guid logoFileId }
            ? $"{options.PublicBaseUrl.TrimEnd('/')}/api/companies/{companyId}/logo?v={logoFileId:N}"
            : null;

    // An absolute link into the app, for emails sent from a background worker with no live page.
    public static string Link(EmailOptions options, string path) =>
        $"{options.PublicBaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
}
