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

        using SKSurface surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas canvas = surface.Canvas;

        canvas.Clear(hasTransparency && IsLightLogo(logo, content) ? SKColor.Parse("#171717") : SKColors.White);

        using SKImage logoImage = SKImage.FromBitmap(logo);
        using SKPaint paint = new() { IsAntialias = true };
        canvas.DrawImage(logoImage, SKRect.Create(content.Left, content.Top, content.Width, content.Height), destination,
            new SKSamplingOptions(SKCubicResampler.Mitchell), paint);

        using SKImage snapshot = surface.Snapshot();
        using SKData data = snapshot.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
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
