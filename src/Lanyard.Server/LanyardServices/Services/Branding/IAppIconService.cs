using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Branding;

public enum AppIconPurpose
{
    // Shown as-is: launcher icons, the iPhone home-screen icon.
    Any,
    // Android crops these to its own shape (circle, squircle...), so the logo is kept inside the
    // central safe-zone circle instead of the full square.
    Maskable
}

/// <summary>
/// The installed ("Add to home screen") app's icons and manifest, made from a company's uploaded logo
/// so each company's staff see their own logo on their phone rather than the Lanyard λ. A company
/// with no logo keeps the static λ icons and manifest in wwwroot.
/// </summary>
public interface IAppIconService
{
    /// <summary>The only sizes the icon endpoint renders: 180 (iPhone), 192 and 512 (Android).</summary>
    IReadOnlyCollection<int> SupportedSizes { get; }

    /// <summary>A square PNG of the logo, padded onto a background it stays visible on.</summary>
    Task<Result<byte[]>> RenderLogoIconAsync(Guid logoFileId, int size, AppIconPurpose purpose, CancellationToken cancellationToken);

    /// <summary>The web app manifest JSON for a company that has a logo.</summary>
    string BuildCompanyManifestJson(CompanyBrandingInfo branding, Guid logoFileId);
}
