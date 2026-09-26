using Lanyard.Application.Services;
using Lanyard.Application.Services.Branding;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SkiaSharp;
using System.Text.Json;

namespace Lanyard.Tests.Services.Branding;

[TestClass]
public class AppIconServiceTests
{
    private static readonly Guid LogoFileId = Guid.NewGuid();

    private static (AppIconService service, Mock<IFileService> fileServiceMock) GetService(byte[] logoBytes, string contentType = "image/png")
    {
        Mock<IFileService> fileServiceMock = new();

        fileServiceMock.Setup(x => x.GetFileMetadataAsync(LogoFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<FileMetadata>.Ok(new FileMetadata { Id = LogoFileId, FileName = "logo", FilePath = "logo", ContentType = contentType }));
        fileServiceMock.Setup(x => x.DownloadFileAsync(LogoFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Result<Stream>.Ok(new MemoryStream(logoBytes)));

        return (new AppIconService(fileServiceMock.Object, new MemoryCache(new MemoryCacheOptions())), fileServiceMock);
    }

    // A width x height canvas cleared to `background`, with a `mark`-coloured rectangle drawn at `markRect`.
    private static byte[] MakePng(int width, int height, SKColor background, SKColor mark, SKRect markRect)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(background);
        using SKPaint paint = new() { Color = mark };
        canvas.DrawRect(markRect, paint);
        canvas.Flush();

        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKBitmap Decode(byte[] png) => SKBitmap.Decode(png) ?? throw new AssertFailedException("Icon is not a valid image.");

    // Bounds of every pixel that differs from the icon's corner (background) colour.
    private static SKRectI ContentBounds(SKBitmap icon)
    {
        SKColor background = icon.GetPixel(0, 0);
        int left = icon.Width, top = icon.Height, right = -1, bottom = -1;

        for (int y = 0; y < icon.Height; y++)
        {
            for (int x = 0; x < icon.Width; x++)
            {
                if (icon.GetPixel(x, y) == background)
                {
                    continue;
                }

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        return new SKRectI(left, top, right + 1, bottom + 1);
    }

    [TestMethod]
    [DataRow(180)]
    [DataRow(192)]
    [DataRow(512)]
    public async Task RenderLogoIconAsync_ReturnsSquarePngOfRequestedSize(int size)
    {
        byte[] logo = MakePng(400, 100, SKColors.Transparent, SKColors.DarkRed, SKRect.Create(0, 0, 400, 100));
        (AppIconService service, _) = GetService(logo);

        Result<byte[]> result = await service.RenderLogoIconAsync(LogoFileId, size, AppIconPurpose.Any, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error);
        using SKBitmap icon = Decode(result.Data!);
        Assert.AreEqual(size, icon.Width);
        Assert.AreEqual(size, icon.Height);
    }

    [TestMethod]
    public async Task RenderLogoIconAsync_TrimsTransparentPaddingAndCentresLogoWithMargin()
    {
        // A small dark square with lots of transparent padding off to one side.
        byte[] logo = MakePng(300, 300, SKColors.Transparent, SKColors.Navy, SKRect.Create(20, 20, 60, 60));
        (AppIconService service, _) = GetService(logo);

        Result<byte[]> result = await service.RenderLogoIconAsync(LogoFileId, 192, AppIconPurpose.Any, CancellationToken.None);

        using SKBitmap icon = Decode(result.Data!);
        SKRectI bounds = ContentBounds(icon);

        Assert.AreEqual(SKColors.White, icon.GetPixel(0, 0), "A dark logo sits on white.");
        // Trimmed, so the mark itself fills 80% of the icon (154px of 192), centred with ~19px either side.
        Assert.IsTrue(Math.Abs(bounds.Width - 154) <= 2, $"Width was {bounds.Width}");
        Assert.IsTrue(Math.Abs(bounds.Left - 19) <= 2, $"Left was {bounds.Left}");
        Assert.IsTrue(Math.Abs(bounds.Top - 19) <= 2, $"Top was {bounds.Top}");
    }

    [TestMethod]
    public async Task RenderLogoIconAsync_WideLogoFitsWidthAndIsCentredVertically()
    {
        byte[] logo = MakePng(400, 100, SKColors.Transparent, SKColors.DarkGreen, SKRect.Create(0, 0, 400, 100));
        (AppIconService service, _) = GetService(logo);

        Result<byte[]> result = await service.RenderLogoIconAsync(LogoFileId, 512, AppIconPurpose.Any, CancellationToken.None);

        using SKBitmap icon = Decode(result.Data!);
        SKRectI bounds = ContentBounds(icon);

        Assert.IsTrue(Math.Abs(bounds.Width - 410) <= 2, $"Width was {bounds.Width}");
        Assert.IsTrue(Math.Abs(bounds.Height - 102) <= 2, $"Height was {bounds.Height}");
        Assert.IsTrue(Math.Abs(bounds.Top + bounds.Bottom - 512) <= 2, "Logo should be vertically centred.");
    }

    [TestMethod]
    public async Task RenderLogoIconAsync_MaskableKeepsLogoInsideSafeZoneCircle()
    {
        byte[] logo = MakePng(400, 100, SKColors.Transparent, SKColors.DarkGreen, SKRect.Create(0, 0, 400, 100));
        (AppIconService service, _) = GetService(logo);

        Result<byte[]> result = await service.RenderLogoIconAsync(LogoFileId, 512, AppIconPurpose.Maskable, CancellationToken.None);

        using SKBitmap icon = Decode(result.Data!);
        SKRectI bounds = ContentBounds(icon);

        // Android's safe zone is a centred circle with radius 40% of the icon (204.8px at 512).
        float halfDiagonal = MathF.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height) / 2f;
        Assert.IsTrue(halfDiagonal <= 512 * 0.4f, $"Logo corners reach {halfDiagonal}px from the centre.");
    }

    [TestMethod]
    public async Task RenderLogoIconAsync_WhiteLogoOnTransparentGetsDarkBackground()
    {
        byte[] logo = MakePng(200, 200, SKColors.Transparent, SKColors.White, SKRect.Create(50, 50, 100, 100));
        (AppIconService service, _) = GetService(logo);

        Result<byte[]> result = await service.RenderLogoIconAsync(LogoFileId, 192, AppIconPurpose.Any, CancellationToken.None);

        using SKBitmap icon = Decode(result.Data!);
        Assert.AreEqual(SKColor.Parse("#171717"), icon.GetPixel(0, 0));
        Assert.AreEqual(SKColors.White, icon.GetPixel(96, 96));
    }

    [TestMethod]
    public async Task RenderLogoIconAsync_OpaqueLogoOnWhiteKeepsWhiteBackground()
    {
        // Typical JPEG-style logo: mostly white, with a small dark mark and no transparency.
        byte[] logo = MakePng(200, 200, SKColors.White, SKColors.Black, SKRect.Create(90, 90, 20, 20));
        (AppIconService service, _) = GetService(logo);

        Result<byte[]> result = await service.RenderLogoIconAsync(LogoFileId, 192, AppIconPurpose.Any, CancellationToken.None);

        using SKBitmap icon = Decode(result.Data!);
        Assert.AreEqual(SKColors.White, icon.GetPixel(0, 0));
    }

    [TestMethod]
    public async Task RenderLogoIconAsync_UnsupportedSize_Fails()
    {
        (AppIconService service, Mock<IFileService> fileServiceMock) = GetService([]);

        Result<byte[]> result = await service.RenderLogoIconAsync(LogoFileId, 1024, AppIconPurpose.Any, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        fileServiceMock.Verify(x => x.DownloadFileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RenderLogoIconAsync_SvgLogo_FailsWithoutDownloading()
    {
        (AppIconService service, Mock<IFileService> fileServiceMock) = GetService([], "image/svg+xml");

        Result<byte[]> result = await service.RenderLogoIconAsync(LogoFileId, 192, AppIconPurpose.Any, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        fileServiceMock.Verify(x => x.DownloadFileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RenderLogoIconAsync_CorruptImage_Fails()
    {
        (AppIconService service, _) = GetService([1, 2, 3, 4]);

        Result<byte[]> result = await service.RenderLogoIconAsync(LogoFileId, 192, AppIconPurpose.Any, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task RenderLogoIconAsync_CachesRenderedIcon()
    {
        byte[] logo = MakePng(100, 100, SKColors.Transparent, SKColors.Navy, SKRect.Create(0, 0, 100, 100));
        (AppIconService service, Mock<IFileService> fileServiceMock) = GetService(logo);

        await service.RenderLogoIconAsync(LogoFileId, 192, AppIconPurpose.Any, CancellationToken.None);
        Result<byte[]> second = await service.RenderLogoIconAsync(LogoFileId, 192, AppIconPurpose.Any, CancellationToken.None);

        Assert.IsTrue(second.IsSuccess);
        fileServiceMock.Verify(x => x.DownloadFileAsync(LogoFileId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public void BuildCompanyManifestJson_UsesCompanyNameColourAndLogoIconsWithoutMonochrome()
    {
        (AppIconService service, _) = GetService([]);
        CompanyBrandingInfo branding = new(7, "Acme Leisure", "#c8102e", LogoFileId, null);

        using JsonDocument manifest = JsonDocument.Parse(service.BuildCompanyManifestJson(branding));
        JsonElement root = manifest.RootElement;

        Assert.AreEqual("/", root.GetProperty("id").GetString(), "Must keep the same app id as the default manifest.");
        Assert.AreEqual("Acme Leisure", root.GetProperty("name").GetString());
        Assert.AreEqual("Acme Leisure", root.GetProperty("short_name").GetString());
        Assert.AreEqual("#c8102e", root.GetProperty("theme_color").GetString());

        List<JsonElement> icons = root.GetProperty("icons").EnumerateArray().ToList();
        Assert.IsTrue(icons.All(x => x.GetProperty("src").GetString()!.StartsWith("/api/companies/7/app-icon/")));
        Assert.IsTrue(icons.All(x => x.GetProperty("src").GetString()!.Contains($"v={LogoFileId:N}")));
        Assert.IsTrue(icons.Any(x => x.GetProperty("purpose").GetString() == "maskable"));
        Assert.IsFalse(icons.Any(x => x.GetProperty("purpose").GetString() == "monochrome"));
    }

    [TestMethod]
    public void BuildCompanyManifestJson_CompanyWithoutLogo_KeepsNameButUsesDefaultIcons()
    {
        (AppIconService service, _) = GetService([]);
        CompanyBrandingInfo branding = new(7, "Acme Leisure", null, null, null);

        using JsonDocument manifest = JsonDocument.Parse(service.BuildCompanyManifestJson(branding));
        JsonElement root = manifest.RootElement;

        Assert.AreEqual("Acme Leisure", root.GetProperty("name").GetString());
        List<string> srcs = root.GetProperty("icons").EnumerateArray().Select(x => x.GetProperty("src").GetString()!).ToList();
        CollectionAssert.Contains(srcs, "/icon-192.png");
        Assert.IsTrue(srcs.All(x => !x.StartsWith("/api/")));
    }

    [TestMethod]
    public void BuildCompanyManifestJson_BlankCompanyName_FallsBackToLanyard()
    {
        (AppIconService service, _) = GetService([]);
        CompanyBrandingInfo branding = new(7, "   ", null, LogoFileId, null);

        using JsonDocument manifest = JsonDocument.Parse(service.BuildCompanyManifestJson(branding));

        Assert.AreEqual("Lanyard", manifest.RootElement.GetProperty("name").GetString());
    }

    [TestMethod]
    public void AppInstallLinks_For_CompanyWithoutLogo_StillUsesCompanyManifestAndName()
    {
        AppInstallLinks links = AppInstallLinks.For(new CompanyBrandingInfo(7, " Acme Leisure ", null, null, null));

        StringAssert.StartsWith(links.ManifestHref, "/api/companies/7/manifest.webmanifest?v=");
        Assert.AreEqual(AppInstallLinks.Default.AppleTouchIconHref, links.AppleTouchIconHref);
        Assert.AreEqual("Acme Leisure", links.AppName);
    }

    [TestMethod]
    public void AppInstallLinks_For_ManifestVersionChangesWithNameButIsStable()
    {
        CompanyBrandingInfo branding = new(7, "Acme", "#c8102e", LogoFileId, null);

        Assert.AreEqual(AppInstallLinks.For(branding).ManifestHref, AppInstallLinks.For(branding).ManifestHref);
        Assert.AreNotEqual(AppInstallLinks.For(branding).ManifestHref, AppInstallLinks.For(branding with { Name = "Acme Leisure" }).ManifestHref);
        Assert.AreEqual($"/api/companies/7/app-icon/180?v={LogoFileId:N}", AppInstallLinks.For(branding).AppleTouchIconHref);
    }
}
