using System.Net;
using System.Text.RegularExpressions;
using Ganss.Xss;

namespace Lanyard.Application.Services.Chat;

// Turns what the chat composer (Quill) sends into safe HTML and plain text. Only the formatting the
// composer offers survives: bold, italic, underline, strike, lists, links and line breaks. Links
// open in a new tab and can't reach back into Lanyard's window. Sanitised on save, so every
// reader - the thread, a report snapshot, a notification preview - gets the same safe markup.
public static partial class ChatHtml
{
    public const int MaxHtmlLength = 8000;
    public const int MaxTextLength = 4000;

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    private static HtmlSanitizer CreateSanitizer()
    {
        HtmlSanitizer sanitizer = new();

        sanitizer.AllowedTags.Clear();
        foreach (string tag in new[] { "p", "br", "strong", "b", "em", "i", "u", "s", "ol", "ul", "li", "a" })
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.Add("href");
        sanitizer.AllowedAttributes.Add("data-list"); // Quill 2 marks bullet vs numbered items this way

        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("mailto");

        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedClasses.Clear();

        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Dom.IElement { TagName: "A" } link)
            {
                link.SetAttribute("target", "_blank");
                link.SetAttribute("rel", "noopener noreferrer nofollow");
            }
        };

        sanitizer.AllowedAttributes.Add("target");
        sanitizer.AllowedAttributes.Add("rel");

        return sanitizer;
    }

    public record Cleaned(string Html, string Text);

    // Null when there's nothing left to send (an empty message, or only formatting).
    public static Cleaned? Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        string safe = Sanitizer.Sanitize(html).Trim();

        // Quill leaves empty paragraphs behind when a message is sent with trailing newlines.
        safe = TrailingEmptyParagraphs().Replace(safe, string.Empty);

        string text = ToText(safe);

        return string.IsNullOrWhiteSpace(text) ? null : new Cleaned(safe, text);
    }

    public static string ToText(string html)
    {
        string withBreaks = BlockEnds().Replace(html, "\n");
        string stripped = Tags().Replace(withBreaks, string.Empty);
        string decoded = WebUtility.HtmlDecode(stripped);

        IEnumerable<string> lines = decoded.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0);

        return string.Join('\n', lines);
    }

    // One line for an inbox preview or a notification.
    public static string Preview(string text, int max = 120)
    {
        string oneLine = text.Replace('\n', ' ');

        return oneLine.Length <= max ? oneLine : oneLine[..(max - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex(@"</p>|<br\s*/?>|</li>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnds();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"(<p>(<br>|\s)*</p>\s*)+$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingEmptyParagraphs();
}
