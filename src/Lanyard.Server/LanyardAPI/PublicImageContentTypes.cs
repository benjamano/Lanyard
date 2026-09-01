namespace Lanyard.API
{
    /// <summary>
    /// Content types an anonymous, same-origin image endpoint is allowed to serve.
    ///
    /// Raster only. An SVG served from one of these URLs would execute any embedded &lt;script&gt;
    /// when navigated to directly, and the admin-side uploaders' Accept="image/*" is only a
    /// client-side hint - this list is the real gate.
    ///
    /// Shared by CompanyBrandingController (logos, login backgrounds) and OrderingController
    /// (menu photos) so the two cannot drift apart: an allowlist that is correct in one place
    /// and stale in another is the same as not having one.
    /// </summary>
    public static class PublicImageContentTypes
    {
        public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/png",
            "image/jpeg",
            "image/gif",
            "image/webp"
        };

        public static bool IsAllowed(string? contentType) =>
            contentType is not null && Allowed.Contains(contentType);
    }
}
