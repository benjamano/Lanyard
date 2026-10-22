using Ganss.Xss;

namespace Lanyard.Application.Services.Training;

// Makes a course section's body safe to render. The section is written by a Manager in the course
// editor (Quill) and shown as raw markup to every Admin and staff member who takes the course, so
// without this a Manager could store <img onerror=...> and run script in an Admin's browser
// (Security Issue 9.5). Only what the editor's toolbar produces survives: headings, bold, italic,
// lists, links and images (the File Manager image picker inserts /api/files/download/... with an
// optional width/height from the resize handles). Run on save, and again on render for sections
// saved before this existed.
public static class CourseHtml
{
    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    private static HtmlSanitizer CreateSanitizer()
    {
        HtmlSanitizer sanitizer = new();

        sanitizer.AllowedTags.Clear();
        foreach (string tag in new[] { "p", "br", "h1", "h2", "h3", "h4", "strong", "b", "em", "i", "u", "s", "ol", "ul", "li", "a", "img", "span", "blockquote", "pre", "code" })
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        foreach (string attribute in new[] { "href", "src", "alt", "width", "height", "data-list", "target", "rel" })
        {
            sanitizer.AllowedAttributes.Add(attribute);
        }

        // Relative URLs (the image picker's /api/files/download/...) carry no scheme and are kept;
        // anything with a scheme must be one of these, so javascript: and data: are dropped.
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("mailto");

        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedClasses.Clear();
        sanitizer.AllowedAtRules.Clear();

        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Dom.IElement { TagName: "A" } link)
            {
                link.SetAttribute("target", "_blank");
                link.SetAttribute("rel", "noopener noreferrer nofollow");
            }
        };

        return sanitizer;
    }

    public static string Clean(string? html) =>
        string.IsNullOrWhiteSpace(html) ? string.Empty : Sanitizer.Sanitize(html);
}
