using System.Net;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Demo;

// What a company's own website says about its branding.
public sealed record WebsiteBranding(string? Name, string? ColorHex, string? LogoDataUrl, string SiteUrl);

public interface IWebsiteBrandingService
{
    // Reads the name, theme colour and best icon from a website (and its web app manifest).
    Task<Result<WebsiteBranding>> LookupAsync(string url, CancellationToken ct = default);
}

// For the demo's "get my branding from my website". Everything is fetched through the
// PublicAddressGuard handler (public internet only) with small size and time limits, since the
// URL comes from anyone trying the public demo.
public sealed partial class WebsiteBrandingService(IHttpClientFactory httpClientFactory, ILogger<WebsiteBrandingService> logger) : IWebsiteBrandingService
{
    public const string HttpClientName = "website-branding";

    private const int MaxPageBytes = 1024 * 1024;
    private const int MaxManifestBytes = 256 * 1024;
    private const int MaxImageBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(15);

    private static readonly string[] GenericTitleWords = ["home", "homepage", "home page", "welcome", "official site", "official website", "index"];

    private sealed record IconCandidate(Uri Url, int Score);

    public async Task<Result<WebsiteBranding>> LookupAsync(string url, CancellationToken ct = default)
    {
        if (!TryNormaliseUrl(url, out Uri? siteUrl))
        {
            return Result<WebsiteBranding>.Fail("That doesn't look like a website address - try something like yourcompany.co.uk.");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OverallTimeout);
        HttpClient client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            (string? html, Uri? pageUrl, HttpStatusCode status) = await GetPageAsync(client, siteUrl, timeout.Token);

            if (html is null || pageUrl is null)
            {
                logger.LogInformation("Website branding lookup for {Url} got {Status} and no page", siteUrl, (int)status);

                // Bot protection (Cloudflare and friends) answers automated requests like this one
                // with these; saying so is more useful than "check the address".
                return Result<WebsiteBranding>.Fail(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                    ? "That website doesn't let automated visitors read it, so we can't pick up its branding - you can set it below instead."
                    : "We couldn't load that website. Check the address and try again.");
            }

            IDocument document = await new HtmlParser().ParseDocumentAsync(html, timeout.Token);
            ManifestInfo? manifest = await GetManifestAsync(client, document, pageUrl, timeout.Token);

            string? name = FindName(document, manifest);
            string? color = FindColor(document, manifest);
            string? logo = await FindLogoAsync(client, document, manifest, pageUrl, timeout.Token);

            if (name is null && color is null && logo is null)
            {
                return Result<WebsiteBranding>.Fail("We couldn't find any branding on that website.");
            }

            return Result<WebsiteBranding>.Ok(new WebsiteBranding(name, color, logo, pageUrl.GetLeftPart(UriPartial.Authority)));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result<WebsiteBranding>.Fail("That website took too long to answer.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("Website branding lookup for {Url} failed: {Error}", siteUrl, ex.Message);
            return Result<WebsiteBranding>.Fail("We couldn't load that website. Check the address and try again.");
        }
    }

    public static bool TryNormaliseUrl(string? input, out Uri? url)
    {
        url = null;
        string value = input?.Trim() ?? string.Empty;

        if (value.Length is 0 or > 2048)
        {
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !parsed.IsDefaultPort
            || !parsed.Host.Contains('.'))
        {
            return false;
        }

        url = parsed;
        return true;
    }

    private static async Task<(string? Html, Uri? FinalUrl, HttpStatusCode Status)> GetPageAsync(HttpClient client, Uri url, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");

        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/xhtml+xml"))
        {
            return (null, null, response.StatusCode);
        }

        byte[]? bytes = await ReadLimitedAsync(response, MaxPageBytes, ct);
        Encoding encoding = GetEncoding(response.Content.Headers.ContentType);

        return (bytes is null ? null : encoding.GetString(bytes), response.RequestMessage?.RequestUri ?? url, response.StatusCode);
    }

    // Reads at most maxBytes (a truncated page still has its <head>); images over the limit are dropped.
    private static async Task<byte[]?> ReadLimitedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct, bool allowTruncation = true)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16 * 1024];
        int read;

        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            int take = Math.Min(read, maxBytes - (int)buffer.Length);
            buffer.Write(chunk, 0, take);

            if (buffer.Length >= maxBytes)
            {
                return allowTruncation ? buffer.ToArray() : null;
            }
        }

        return buffer.ToArray();
    }

    private static Encoding GetEncoding(MediaTypeHeaderValue? contentType)
    {
        try
        {
            return string.IsNullOrEmpty(contentType?.CharSet) ? Encoding.UTF8 : Encoding.GetEncoding(contentType.CharSet.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Manifest
    // ---------------------------------------------------------------------------------------

    private sealed record ManifestInfo(string? Name, string? ShortName, string? ThemeColor, List<IconCandidate> Icons);

    private static async Task<ManifestInfo?> GetManifestAsync(HttpClient client, IDocument document, Uri pageUrl, CancellationToken ct)
    {
        string? href = document.QuerySelector("link[rel~='manifest' i]")?.GetAttribute("href");

        if (!TryResolve(pageUrl, href, out Uri? manifestUrl))
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await client.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            byte[]? bytes = await ReadLimitedAsync(response, MaxManifestBytes, ct, allowTruncation: false);

            if (bytes is null)
            {
                return null;
            }

            using JsonDocument json = JsonDocument.Parse(bytes);
            JsonElement root = json.RootElement;
            List<IconCandidate> icons = [];

            if (root.TryGetProperty("icons", out JsonElement iconArray) && iconArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement icon in iconArray.EnumerateArray())
                {
                    string? src = icon.TryGetProperty("src", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    string? sizes = icon.TryGetProperty("sizes", out JsonElement z) && z.ValueKind == JsonValueKind.String ? z.GetString() : null;
                    string? purpose = icon.TryGetProperty("purpose", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

                    // Monochrome icons are single-colour silhouettes, not the logo.
                    if (TryResolve(manifestUrl!, src, out Uri? iconUrl) && purpose?.Contains("monochrome", StringComparison.OrdinalIgnoreCase) != true)
                    {
                        icons.Add(new IconCandidate(iconUrl!, 40 + Math.Min(LargestSize(sizes), 512) / 8));
                    }
                }
            }

            return new ManifestInfo(StringProp(root, "name"), StringProp(root, "short_name"), StringProp(root, "theme_color"), icons);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException)
        {
            return null;
        }

        static string? StringProp(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    // ---------------------------------------------------------------------------------------
    // Name, colour, logo
    // ---------------------------------------------------------------------------------------

    public static string? FindName(IDocument document, string? manifestName = null, string? manifestShortName = null)
    {
        string? name = Meta(document, "meta[property='og:site_name' i]")
            ?? Meta(document, "meta[name='application-name' i]")
            ?? Meta(document, "meta[name='apple-mobile-web-app-title' i]")
            ?? manifestName
            ?? manifestShortName
            ?? NameFromTitle(document.Title);

        name = name?.Trim();
        return string.IsNullOrEmpty(name) ? null : name[..Math.Min(name.Length, 60)];
    }

    private static string? FindName(IDocument document, ManifestInfo? manifest) =>
        FindName(document, manifest?.Name, manifest?.ShortName);

    // "Home | Acme Leisure" -> "Acme Leisure": the shortest part of the title that isn't "Home".
    private static string? NameFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string[] parts = TitleSeparator().Split(title)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0 && !GenericTitleWords.Contains(x.ToLowerInvariant()))
            .ToArray();

        return parts.Length == 0 ? title.Trim() : parts.OrderBy(x => x.Length).First();
    }

    public static string? FindColor(IDocument document, string? manifestThemeColor = null)
    {
        // A theme-color with a media query is usually the light/dark pair: prefer the one without
        // one, then the light-scheme one.
        IElement[] themeColors = [.. document.QuerySelectorAll("meta[name='theme-color' i]")];
        IEnumerable<string?> candidates = themeColors
            .OrderBy(x => x.GetAttribute("media") is null ? 0 : x.GetAttribute("media")!.Contains("light", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .Select(x => x.GetAttribute("content"))
            .Append(manifestThemeColor)
            .Append(Meta(document, "meta[name='msapplication-TileColor' i]"));

        foreach (string? candidate in candidates)
        {
            // White (or near it) is what most sites use for a plain browser bar, not a brand colour.
            if (TryParseColor(candidate, out string hex) && !IsNearWhite(hex))
            {
                return hex;
            }
        }

        return null;
    }

    private static string? FindColor(IDocument document, ManifestInfo? manifest) => FindColor(document, manifest?.ThemeColor);

    // Best first: big square app icons, then SVG and sized icons, then the plain favicon, then a
    // social-share image (often a photo, so last).
    public static List<Uri> RankIcons(IDocument document, Uri pageUrl, IEnumerable<(Uri Url, int Score)>? manifestIcons = null)
    {
        List<IconCandidate> candidates = [.. (manifestIcons ?? []).Select(x => new IconCandidate(x.Url, x.Score))];

        foreach (IElement link in document.QuerySelectorAll("link[rel][href]"))
        {
            string rel = link.GetAttribute("rel")!.ToLowerInvariant();

            if (!TryResolve(pageUrl, link.GetAttribute("href"), out Uri? url))
            {
                continue;
            }

            int size = LargestSize(link.GetAttribute("sizes"));
            bool isSvg = url!.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(link.GetAttribute("type"), "image/svg+xml", StringComparison.OrdinalIgnoreCase);

            if (rel.Contains("apple-touch-icon"))
            {
                candidates.Add(new IconCandidate(url, 60 + Math.Min(size == 0 ? 180 : size, 512) / 8));
            }
            else if (rel.Split(' ').Contains("icon"))
            {
                candidates.Add(new IconCandidate(url, isSvg ? 55 : 20 + Math.Min(size, 512) / 8));
            }
            else if (rel == "mask-icon")
            {
                // Safari pinned-tab icons are single-colour; only better than nothing.
                candidates.Add(new IconCandidate(url, 5));
            }
        }

        if (TryResolve(pageUrl, Meta(document, "meta[property='og:image' i]"), out Uri? ogImage))
        {
            candidates.Add(new IconCandidate(ogImage!, 10));
        }

        candidates.Add(new IconCandidate(new Uri(pageUrl, "/favicon.ico"), 8));

        return [.. candidates.OrderByDescending(x => x.Score).Select(x => x.Url).Distinct()];
    }

    private async Task<string?> FindLogoAsync(HttpClient client, IDocument document, ManifestInfo? manifest, Uri pageUrl, CancellationToken ct)
    {
        foreach (Uri url in RankIcons(document, pageUrl, manifest?.Icons.Select(x => (x.Url, x.Score))).Take(4))
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                string? mediaType = response.Content.Headers.ContentType?.MediaType;

                if (!response.IsSuccessStatusCode || mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                byte[]? bytes = await ReadLimitedAsync(response, MaxImageBytes, ct, allowTruncation: false);

                if (bytes is { Length: > 0 })
                {
                    return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
                }
            }
            catch (HttpRequestException)
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static string? Meta(IDocument document, string selector)
    {
        string? content = document.QuerySelector(selector)?.GetAttribute("content")?.Trim();
        return string.IsNullOrEmpty(content) ? null : content;
    }

    private static bool TryResolve(Uri baseUrl, string? href, out Uri? url)
    {
        url = null;

        if (string.IsNullOrWhiteSpace(href) || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUrl, href.Trim(), out Uri? resolved) || resolved.Scheme is not ("http" or "https"))
        {
            return false;
        }

        url = resolved;
        return true;
    }

    private static int LargestSize(string? sizes)
    {
        if (string.IsNullOrWhiteSpace(sizes))
        {
            return 0;
        }

        return sizes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('x', 'X'))
            .Where(x => x.Length == 2 && int.TryParse(x[0], out _))
            .Select(x => int.Parse(x[0], CultureInfo.InvariantCulture))
            .DefaultIfEmpty(sizes.Contains("any", StringComparison.OrdinalIgnoreCase) ? 512 : 0)
            .Max();
    }

    // #abc, #aabbcc, #aabbccdd and rgb()/rgba() -> #AABBCC.
    public static bool TryParseColor(string? value, out string hex)
    {
        hex = string.Empty;
        string v = value?.Trim() ?? string.Empty;

        Match shortHex = ShortHex().Match(v);
        if (shortHex.Success)
        {
            hex = $"#{shortHex.Groups[1].Value}{shortHex.Groups[1].Value}{shortHex.Groups[2].Value}{shortHex.Groups[2].Value}{shortHex.Groups[3].Value}{shortHex.Groups[3].Value}".ToUpperInvariant();
            return true;
        }

        Match longHex = LongHex().Match(v);
        if (longHex.Success)
        {
            hex = $"#{longHex.Groups[1].Value}".ToUpperInvariant();
            return true;
        }

        Match rgb = Rgb().Match(v);
        if (rgb.Success
            && byte.TryParse(rgb.Groups[1].Value, out byte r)
            && byte.TryParse(rgb.Groups[2].Value, out byte g)
            && byte.TryParse(rgb.Groups[3].Value, out byte b))
        {
            hex = $"#{r:X2}{g:X2}{b:X2}";
            return true;
        }

        return false;
    }

    private static bool IsNearWhite(string hex)
    {
        int r = int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber);
        int g = int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber);
        int b = int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber);

        return r > 235 && g > 235 && b > 235;
    }

    [GeneratedRegex(@"\s+[|\-–—·:]\s+")]
    private static partial Regex TitleSeparator();

    [GeneratedRegex("^#([0-9a-fA-F])([0-9a-fA-F])([0-9a-fA-F])$")]
    private static partial Regex ShortHex();

    [GeneratedRegex("^#([0-9a-fA-F]{6})(?:[0-9a-fA-F]{2})?$")]
    private static partial Regex LongHex();

    [GeneratedRegex(@"^rgba?\(\s*(\d{1,3})\s*[, ]\s*(\d{1,3})\s*[, ]\s*(\d{1,3})")]
    private static partial Regex Rgb();
}
