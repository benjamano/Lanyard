using Lanyard.API.Controllers;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Branding;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Branding;

// The logo and background endpoints' HTTP caching: the file id is the ETag, a URL versioned with
// the current id is immutable, anything else is short-lived, and a matching If-None-Match is a 304
// that never touches the file.
[TestClass]
public class CompanyBrandingControllerTests
{
    private static readonly Guid LogoId = Guid.NewGuid();
    private static readonly Guid BackgroundId = Guid.NewGuid();

    private Mock<ICompanyLocationService> _companies = null!;
    private Mock<IFileService> _files = null!;
    private MemoryCache _cache = null!;

    [TestInitialize]
    public void Setup()
    {
        _companies = new Mock<ICompanyLocationService>();
        _companies.Setup(x => x.GetCompanyBrandingAsync(1))
            .ReturnsAsync(Result<CompanyBrandingInfo>.Ok(new CompanyBrandingInfo(1, "Play2Day", "#C8102E", LogoId, BackgroundId)));
        _companies.Setup(x => x.GetCompanyBrandingAsync(It.Is<int>(id => id != 1)))
            .ReturnsAsync(Result<CompanyBrandingInfo>.Fail("Company not found."));

        _files = new Mock<IFileService>();
        SetupFile(LogoId, "image/png");
        SetupFile(BackgroundId, "image/jpeg");

        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    [TestCleanup]
    public void Cleanup() => _cache.Dispose();

    private void SetupFile(Guid id, string contentType)
    {
        _files.Setup(x => x.GetFileMetadataAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<FileMetadata>.Ok(new FileMetadata { Id = id, FileName = "f", FilePath = "f", ContentType = contentType }));
        _files.Setup(x => x.DownloadFileAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Result<Stream>.Ok(new MemoryStream([1, 2, 3])));
    }

    private CompanyBrandingController Controller(string queryString = "", string? ifNoneMatch = null)
    {
        DefaultHttpContext http = new();
        http.Request.QueryString = new QueryString(queryString);
        if (ifNoneMatch is not null)
        {
            http.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        return new CompanyBrandingController(_companies.Object, _files.Object, new Mock<IAppIconService>().Object, _cache,
            NullLogger<CompanyBrandingController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    [TestMethod]
    public async Task Logo_VersionedWithTheCurrentId_IsImmutable()
    {
        CompanyBrandingController controller = Controller($"?v={LogoId:N}");

        IActionResult result = await controller.GetLogo(1, CancellationToken.None);

        FileStreamResult file = (FileStreamResult)result;
        Assert.AreEqual("image/png", file.ContentType);
        Assert.AreEqual($"\"{LogoId:N}\"", controller.Response.Headers.ETag.ToString());
        string cacheControl = controller.Response.Headers.CacheControl.ToString();
        StringAssert.Contains(cacheControl, "public");
        StringAssert.Contains(cacheControl, "max-age=31536000");
        StringAssert.Contains(cacheControl, "immutable");
    }

    [TestMethod]
    public async Task Logo_Unversioned_IsCachedBriefly()
    {
        CompanyBrandingController controller = Controller();

        Assert.IsInstanceOfType<FileStreamResult>(await controller.GetLogo(1, CancellationToken.None));

        string cacheControl = controller.Response.Headers.CacheControl.ToString();
        StringAssert.Contains(cacheControl, "max-age=300");
        Assert.IsFalse(cacheControl.Contains("immutable"));
        Assert.AreEqual($"\"{LogoId:N}\"", controller.Response.Headers.ETag.ToString());
    }

    [TestMethod]
    public async Task Logo_WithAnOldVersion_IsCachedBriefly_AndServesTheCurrentLogo()
    {
        // e.g. an email sent before the logo was replaced.
        CompanyBrandingController controller = Controller($"?v={Guid.NewGuid():N}");

        Assert.IsInstanceOfType<FileStreamResult>(await controller.GetLogo(1, CancellationToken.None));

        StringAssert.Contains(controller.Response.Headers.CacheControl.ToString(), "max-age=300");
        _files.Verify(x => x.DownloadFileAsync(LogoId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Logo_MatchingIfNoneMatch_Is304_WithoutTouchingTheFile()
    {
        CompanyBrandingController controller = Controller($"?v={LogoId:N}", $"\"{LogoId:N}\"");

        IActionResult result = await controller.GetLogo(1, CancellationToken.None);

        Assert.AreEqual(StatusCodes.Status304NotModified, ((StatusCodeResult)result).StatusCode);
        Assert.AreEqual($"\"{LogoId:N}\"", controller.Response.Headers.ETag.ToString());
        _files.Verify(x => x.GetFileMetadataAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _files.Verify(x => x.DownloadFileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Logo_StaleIfNoneMatch_AfterTheLogoChanged_GetsTheNewFile()
    {
        CompanyBrandingController controller = Controller(ifNoneMatch: $"\"{Guid.NewGuid():N}\"");

        Assert.IsInstanceOfType<FileStreamResult>(await controller.GetLogo(1, CancellationToken.None));
    }

    [TestMethod]
    public async Task Logo_ContentType_IsLookedUpOncePerFile()
    {
        await Controller().GetLogo(1, CancellationToken.None);
        await Controller().GetLogo(1, CancellationToken.None);

        _files.Verify(x => x.GetFileMetadataAsync(LogoId, It.IsAny<CancellationToken>()), Times.Once);
        _files.Verify(x => x.DownloadFileAsync(LogoId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task Logo_DisallowedContentType_IsStillNotFound_AndNotCacheable()
    {
        Guid svgId = Guid.NewGuid();
        _companies.Setup(x => x.GetCompanyBrandingAsync(2))
            .ReturnsAsync(Result<CompanyBrandingInfo>.Ok(new CompanyBrandingInfo(2, "Partyman", null, svgId, null)));
        SetupFile(svgId, "image/svg+xml");
        CompanyBrandingController controller = Controller($"?v={svgId:N}");

        Assert.IsInstanceOfType<NotFoundResult>(await controller.GetLogo(2, CancellationToken.None));
        Assert.AreEqual(0, controller.Response.Headers.CacheControl.Count);
        Assert.AreEqual(0, controller.Response.Headers.ETag.Count);
        _files.Verify(x => x.DownloadFileAsync(svgId, It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Logo_UnknownCompany_IsNotFound()
    {
        Assert.IsInstanceOfType<NotFoundResult>(await Controller().GetLogo(99, CancellationToken.None));
    }

    [TestMethod]
    public async Task Background_ServesTheBackground_WithItsOwnETag()
    {
        CompanyBrandingController controller = Controller($"?v={BackgroundId:N}");

        FileStreamResult file = (FileStreamResult)await controller.GetBackgroundImage(1, CancellationToken.None);

        Assert.AreEqual("image/jpeg", file.ContentType);
        Assert.AreEqual($"\"{BackgroundId:N}\"", controller.Response.Headers.ETag.ToString());
        StringAssert.Contains(controller.Response.Headers.CacheControl.ToString(), "immutable");
    }

    [TestMethod]
    public async Task Background_LogosETag_DoesNotMatch()
    {
        // The two images of one company never validate each other's cached copy.
        CompanyBrandingController controller = Controller(ifNoneMatch: $"\"{LogoId:N}\"");

        Assert.IsInstanceOfType<FileStreamResult>(await controller.GetBackgroundImage(1, CancellationToken.None));
    }
}
