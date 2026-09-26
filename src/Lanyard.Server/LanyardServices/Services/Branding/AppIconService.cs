using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.Branding;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using System.Text.Json;

namespace Lanyard.Application.Services.Branding;

public class AppIconService(IFileService fileService, IMemoryCache cache) : IAppIconService
{
    private readonly IFileService _fileService = fileService;
    private readonly IMemoryCache _cache = cache;

    private static readonly int[] Sizes = [180, 192, 512];

    // Same raster-only list as CompanyBrandingController's logo endpoint, and all formats Skia decodes.
    private static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp"
    };

    // Share of the icon's width the logo may fill. "Any" icons leave a 10% margin each side, which
    // also clears the corners iOS rounds off. Maskable icons must fit inside Android's safe zone,
    // a centred circle 80% of the icon's width; a little under that keeps the logo off the crop edge.
    private const float AnyContentRatio = 0.8f;
    private const float MaskableSafeZoneDiameterRatio = 0.76f;

    // Average brightness (0-1) of a logo's visible pixels above which it's treated as a light logo,
    // e.g. a white mark meant for a dark header, which would vanish on the default white background.
    private const double LightLogoLuminanceThreshold = 0.85;

    // The Lanyard λ badge stamped in the icon's bottom-right corner, so a company's logo on a home
    // screen is still recognisable as the Lanyard app. Sizes and positions are shares of the icon's
    // width. "Any" icons anchor the badge's bottom-right at 90%, clear of iOS's rounded corner.
    // Maskable icons anchor it at 77%, just inside the safe-zone circle (radius 40%) at 45 degrees,
    // so Android's crop never cuts it off.
    private const float AnyBadgeHeightRatio = 0.2f;
    private const float AnyBadgeAnchorRatio = 0.9f;
    private const float MaskableBadgeHeightRatio = 0.15f;
    private const float MaskableBadgeAnchorRatio = 0.77f;

    // Gap kept clear of the logo around the badge, and how far a pixel there may stray from the
    // area's average colour (per channel, 0-255) and still count as background. A few stray pixels
    // (JPEG noise, anti-aliasing) are allowed before the corner counts as taken by the logo.
    private const float BadgeClearanceRatio = 0.025f;
    private const int BadgeBackgroundTolerance = 40;
    private const double BadgeMaxStrayPixelShare = 0.01;

    private static readonly SKColor DarkBackground = SKColor.Parse("#171717");

    private static readonly Lazy<(SKBitmap Glyph, SKRectI Bounds)> BadgeGlyph = new(LoadBadgeGlyph);

    public IReadOnlyCollection<int> SupportedSizes => Sizes;

    public async Task<Result<byte[]>> RenderLogoIconAsync(Guid logoFileId, int size, AppIconPurpose purpose, CancellationToken cancellationToken)
    {
        if (!Sizes.Contains(size))
        {
            return Result<byte[]>.Fail($"Unsupported app icon size {size}.");
        }

        // Keyed by the logo's file id: uploading a new logo gives the company a new LogoFileId, so a
        // stale icon is never served and nothing needs invalidating.
        string cacheKey = $"app-icon:{logoFileId:N}:{size}:{purpose}";

        if (_cache.TryGetValue(cacheKey, out byte[]? cached) && cached is not null)
        {
            return Result<byte[]>.Ok(cached);
        }

        try
        {
            Result<FileMetadata> meta = await _fileService.GetFileMetadataAsync(logoFileId, cancellationToken);

            if (!meta.Success || meta.Data?.ContentType is not string contentType || !AllowedImageContentTypes.Contains(contentType))
            {
                return Result<byte[]>.Fail("The company logo is not a supported image type.");
            }

            Result<Stream> download = await _fileService.DownloadFileAsync(logoFileId, cancellationToken);

            if (!download.Success || download.Data is null)
            {
                return Result<byte[]>.Fail(download.Error ?? "The company logo could not be downloaded.");
            }

            byte[] logoBytes;

            await using (Stream logoStream = download.Data)
            using (MemoryStream buffer = new())
            {
                await logoStream.CopyToAsync(buffer, cancellationToken);
                logoBytes = buffer.ToArray();
            }

            using SKBitmap? logo = SKBitmap.Decode(logoBytes);

            if (logo is null)
            {
                return Result<byte[]>.Fail("The company logo could not be read as an image.");
            }

            byte[] png = RenderIcon(logo, size, purpose);

            _cache.Set(cacheKey, png, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(1) });

            return Result<byte[]>.Ok(png);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Fail($"Failed to render the app icon: {ex.Message}");
        }
    }

    public string BuildCompanyManifestJson(CompanyBrandingInfo branding)
    {
        string appName = AppInstallLinks.AppNameFor(branding.Name);

        // Same id and scope as wwwroot/manifest.webmanifest, so it stays the one installed Lanyard
        // app: Android swaps the existing home-screen icon and name over on its next manifest check
        // instead of treating it as a second app.
        object manifest = new
        {
            id = "/",
            name = appName,
            short_name = appName,
            start_url = "/",
            scope = "/",
            display = "standalone",
            background_color = "#ffffff",
            theme_color = BrandConstants.ResolveAccentColor(branding.ThemeColorHex),
            icons = branding.LogoFileId is Guid logoFileId ? LogoIcons(branding.CompanyId, logoFileId) : DefaultIcons
        };

        return JsonSerializer.Serialize(manifest);
    }

    // No "monochrome" icon - Android's themed icons would recolour it to a flat silhouette, which a
    // full-colour logo doesn't survive.
    private static object[] LogoIcons(int companyId, Guid logoFileId)
    {
        string iconBase = $"/api/companies/{companyId}/app-icon";
        string version = logoFileId.ToString("N");

        return
        [
            new { src = $"{iconBase}/192?v={version}", sizes = "192x192", type = "image/png", purpose = "any" },
            new { src = $"{iconBase}/512?v={version}", sizes = "512x512", type = "image/png", purpose = "any" },
            new { src = $"{iconBase}/512?maskable=true&v={version}", sizes = "512x512", type = "image/png", purpose = "maskable" }
        ];
    }

    // The λ icons from wwwroot/manifest.webmanifest, rooted because this manifest is served from /api.
    private static readonly object[] DefaultIcons =
    [
        new { src = "/favicon-144.png", sizes = "144x144", type = "image/png", purpose = "any" },
        new { src = "/icon-192.png", sizes = "192x192", type = "image/png", purpose = "any" },
        new { src = "/icon-512.png", sizes = "512x512", type = "image/png", purpose = "any" },
        new { src = "/icon-maskable-512.png", sizes = "512x512", type = "image/png", purpose = "maskable" },
        new { src = "/icon-monochrome-512.png", sizes = "512x512", type = "image/png", purpose = "monochrome" }
    ];

    private static byte[] RenderIcon(SKBitmap logo, int size, AppIconPurpose purpose)
    {
        // Logos are often uploaded with transparent padding baked in; trimming it first means the
        // margin below is the only margin, so the logo isn't shrunk twice.
        (SKRectI content, bool hasTransparency) = FindOpaqueBounds(logo);
        float width = content.Width;
        float height = content.Height;

        float scale = purpose == AppIconPurpose.Maskable
            // Largest scale whose corners still sit inside the safe-zone circle, whatever the aspect ratio.
            ? size * MaskableSafeZoneDiameterRatio / MathF.Sqrt(width * width + height * height)
            : Math.Min(size * AnyContentRatio / width, size * AnyContentRatio / height);

        float drawWidth = width * scale;
        float drawHeight = height * scale;
        SKRect destination = SKRect.Create((size - drawWidth) / 2f, (size - drawHeight) / 2f, drawWidth, drawHeight);

        using SKBitmap icon = new(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKCanvas canvas = new(icon);

        canvas.Clear(hasTransparency && IsLightLogo(logo, content) ? DarkBackground : SKColors.White);

        using SKImage logoImage = SKImage.FromBitmap(logo);
        using SKPaint paint = new() { IsAntialias = true };
        canvas.DrawImage(logoImage, SKRect.Create(content.Left, content.Top, content.Width, content.Height), destination,
            new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        canvas.Flush();

        DrawBadgeIfCornerIsClear(icon, canvas, purpose);

        using SKData data = icon.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    // Stamps the λ in the bottom-right corner, black or white, whichever stands out from what's
    // behind it. Only when that corner is empty background: a logo that reaches into it (a square
    // one filling the icon, say) is left alone rather than having the badge drawn over it.
    private static void DrawBadgeIfCornerIsClear(SKBitmap icon, SKCanvas canvas, AppIconPurpose purpose)
    {
        (SKBitmap glyph, SKRectI glyphBounds) = BadgeGlyph.Value;
        int size = icon.Width;

        (float heightRatio, float anchorRatio) = purpose == AppIconPurpose.Maskable
            ? (MaskableBadgeHeightRatio, MaskableBadgeAnchorRatio)
            : (AnyBadgeHeightRatio, AnyBadgeAnchorRatio);

        float badgeHeight = size * heightRatio;
        float badgeWidth = badgeHeight * glyphBounds.Width / glyphBounds.Height;
        float anchor = size * anchorRatio;
        SKRect badge = new(anchor - badgeWidth, anchor - badgeHeight, anchor, anchor);

        SKRect checkArea = badge;
        checkArea.Inflate(size * BadgeClearanceRatio, size * BadgeClearanceRatio);

        if (!TryGetUniformColour(icon, SKRectI.Round(checkArea), out SKColor background))
        {
            return;
        }

        double luminance = (0.2126 * background.Red + 0.7152 * background.Green + 0.0722 * background.Blue) / 255d;
        SKColor badgeColour = luminance > 0.5 ? DarkBackground : SKColors.White;

        // The glyph is a white λ on transparency; SrcIn keeps its shape and swaps in the badge colour.
        using SKColorFilter tint = SKColorFilter.CreateBlendMode(badgeColour, SKBlendMode.SrcIn);
        using SKPaint paint = new() { IsAntialias = true, ColorFilter = tint };
        using SKImage glyphImage = SKImage.FromBitmap(glyph);
        canvas.DrawImage(glyphImage, SKRect.Create(glyphBounds.Left, glyphBounds.Top, glyphBounds.Width, glyphBounds.Height), badge,
            new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        canvas.Flush();
    }

    // Whether every pixel in the area (bar a few strays) is close to one colour, and that colour.
    private static bool TryGetUniformColour(SKBitmap bitmap, SKRectI area, out SKColor colour)
    {
        area.Intersect(new SKRectI(0, 0, bitmap.Width, bitmap.Height));
        colour = SKColors.Empty;

        if (area.IsEmpty)
        {
            return false;
        }

        long red = 0, green = 0, blue = 0;

        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                red += pixel.Red;
                green += pixel.Green;
                blue += pixel.Blue;
            }
        }

        long count = (long)area.Width * area.Height;
        colour = new SKColor((byte)(red / count), (byte)(green / count), (byte)(blue / count));

        long strays = 0;

        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);

                if (Math.Abs(pixel.Red - colour.Red) > BadgeBackgroundTolerance
                    || Math.Abs(pixel.Green - colour.Green) > BadgeBackgroundTolerance
                    || Math.Abs(pixel.Blue - colour.Blue) > BadgeBackgroundTolerance)
                {
                    strays++;
                }
            }
        }

        return strays <= count * BadgeMaxStrayPixelShare;
    }

    // The same white λ as wwwroot/icon-monochrome-512.png (embedded from there), trimmed to the glyph.
    private static (SKBitmap Glyph, SKRectI Bounds) LoadBadgeGlyph()
    {
        using Stream stream = typeof(AppIconService).Assembly.GetManifestResourceStream("Lanyard.AppIconBadge.png")
            ?? throw new InvalidOperationException("The app icon badge resource is missing.");

        SKBitmap glyph = SKBitmap.Decode(stream) ?? throw new InvalidOperationException("The app icon badge could not be read.");

        return (glyph, FindOpaqueBounds(glyph).Bounds);
    }

    // The smallest rectangle holding every pixel that isn't fully transparent, and whether the image
    // has any transparency at all. An opaque image (e.g. a JPEG) comes back as its full bounds.
    private static (SKRectI Bounds, bool HasTransparency) FindOpaqueBounds(SKBitmap bitmap)
    {
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        bool hasTransparency = false;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                byte alpha = bitmap.GetPixel(x, y).Alpha;
                hasTransparency |= alpha < 255;

                if (alpha == 0)
                {
                    continue;
                }

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        SKRectI bounds = right < 0
            ? new SKRectI(0, 0, bitmap.Width, bitmap.Height)
            : new SKRectI(left, top, right + 1, bottom + 1);

        return (bounds, hasTransparency);
    }

    // Only called for a logo with transparency: an opaque logo brings its own background (usually
    // white, as in most JPEG logos), which sits cleanly on the white icon and must not get a dark one.
    private static bool IsLightLogo(SKBitmap bitmap, SKRectI content)
    {
        double luminanceSum = 0;
        double weightSum = 0;

        // Sampling a grid is plenty to tell a white mark from a coloured one, and keeps a large upload cheap.
        int step = Math.Max(1, Math.Max(content.Width, content.Height) / 128);

        for (int y = content.Top; y < content.Bottom; y += step)
        {
            for (int x = content.Left; x < content.Right; x += step)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                double weight = pixel.Alpha / 255d;

                luminanceSum += weight * (0.2126 * pixel.Red + 0.7152 * pixel.Green + 0.0722 * pixel.Blue) / 255d;
                weightSum += weight;
            }
        }

        return weightSum > 0 && luminanceSum / weightSum > LightLogoLuminanceThreshold;
    }
}
