using Microsoft.AspNetCore.Http;

namespace Lanyard.App;

// "PublicSite" configuration: the public address Lanyard is indexed under.
public class PublicSiteOptions
{
    public const string SectionName = "PublicSite";

    // e.g. https://lanyard.benjaminmercer.co.uk - used for the homepage's canonical and share-preview
    // URLs, and to decide which host search engines may index.
    public string BaseUrl { get; set; } = string.Empty;

    // Shown in the homepage's "Get in touch" section; the section is left out when this is empty.
    public string ContactEmail { get; set; } = string.Empty;

    // An absolute URL on the public site ("/" -> the base URL itself).
    public string Url(string path) => $"{BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    // Search engines may index exactly one page: the public homepage, on the public host. Every
    // other page (login, staff pages, the API, the demo endpoints) and every other host (staging,
    // Railway's own *.up.railway.app address) stays noindex.
    public bool IsIndexable(HttpRequest request)
    {
        return request.Path == "/"
            && Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? baseUri)
            && string.Equals(request.Host.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase);
    }
}
