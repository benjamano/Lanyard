using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SkiaSharp;

namespace Lanyard.Tests.Services.Files;

[TestClass]
public class FileThumbnailTests
{
    private DbContextOptions<ApplicationDbContext> _options = null!;
    private FileService _files = null!;
    private readonly List<string> _paths = [];

    [TestInitialize]
    public void Setup()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(_options));

        Mock<IWebHostEnvironment> environment = new();
        environment.SetupGet(x => x.EnvironmentName).Returns(Environments.Development);

        _files = new FileService(factory.Object, Mock.Of<ICurrentUserAccessor>(), Mock.Of<ISongAnalysisQueue>(), environment.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (string path in _paths.Where(File.Exists))
            File.Delete(path);
    }

    private static byte[] Png(int width, int height)
    {
        using SKBitmap bitmap = new(width, height);
        using (SKCanvas canvas = new(bitmap))
            canvas.Clear(SKColors.OrangeRed);

        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static IFormFile Form(string name, byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = "application/octet-stream" };

    private async Task<FileMetadata> RowAsync(Guid id)
    {
        await using ApplicationDbContext db = new(_options);
        return await db.FileMetadata.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private void Track(FileMetadata file)
    {
        _paths.Add(file.FilePath);

        if (!string.IsNullOrEmpty(file.ThumbnailPath))
            _paths.Add(file.ThumbnailPath);
    }

    [TestMethod]
    public void Render_ShrinksToFit_AndReportsTheOriginalSize()
    {
        RenderedThumbnail? thumb = ThumbnailRenderer.Render(new MemoryStream(Png(2000, 1000)));

        Assert.IsNotNull(thumb);
        Assert.AreEqual(2000, thumb.SourceWidth);
        Assert.AreEqual(1000, thumb.SourceHeight);

        using SKBitmap decoded = SKBitmap.Decode(thumb.WebP);
        Assert.AreEqual(ThumbnailRenderer.MaxSide, decoded.Width);
        Assert.AreEqual(ThumbnailRenderer.MaxSide / 2, decoded.Height);
    }

    // A PNG can't be decoded at a smaller size, so a huge one would mean a huge bitmap in memory.
    [TestMethod]
    public void Render_SkipsAPngTooBigToDecodeSafely()
    {
        RenderedThumbnail? thumb = ThumbnailRenderer.Render(new MemoryStream(Png(5000, 4000)));

        Assert.IsNull(thumb, "20 MP PNG: no thumbnail rather than an 80 MB bitmap.");
    }

    [TestMethod]
    public void Render_ReturnsNullForSomethingThatIsntAPicture()
    {
        Assert.IsNull(ThumbnailRenderer.Render(new MemoryStream("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray())));
    }

    [TestMethod]
    public async Task UploadingAPicture_StoresItsSizeAndAThumbnail()
    {
        Result<FileMetadata> uploaded = await _files.UploadFileAsync(Form("poster.png", Png(1200, 900)), null, "user1", CancellationToken.None);
        FileMetadata row = await RowAsync(uploaded.Data!.Id);
        Track(row);

        Assert.AreEqual(1200, row.Width);
        Assert.AreEqual(900, row.Height);
        Assert.IsFalse(string.IsNullOrEmpty(row.ThumbnailPath));

        Result<Stream> thumb = await _files.OpenThumbnailAsync(row.Id, CancellationToken.None);
        Assert.IsTrue(thumb.IsSuccess);
        await thumb.Data!.DisposeAsync();
    }

    [TestMethod]
    public async Task UploadingAVideo_UsesThePosterAndTheMeasuredLength()
    {
        FileUploadOptions options = new(Media: new UploadedMedia(1920, 1080, 12.34), Poster: Form("poster.jpg", Png(960, 540)));

        Result<FileMetadata> uploaded = await _files.UploadFileAsync(Form("intro.mp4", [0, 0, 0, 24]), null, "user1", options, CancellationToken.None);
        FileMetadata row = await RowAsync(uploaded.Data!.Id);
        Track(row);

        Assert.AreEqual(1920, row.Width, "The browser's measurement wins over the poster's size.");
        Assert.AreEqual(TimeSpan.FromSeconds(12.3), row.VideoLength);
        Assert.IsFalse(string.IsNullOrEmpty(row.ThumbnailPath));
    }

    [TestMethod]
    public async Task AnSvg_IsMarkedAsHavingNoThumbnail()
    {
        Result<FileMetadata> uploaded = await _files.UploadFileAsync(Form("logo.svg", "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray()), null, "user1", CancellationToken.None);
        FileMetadata row = await RowAsync(uploaded.Data!.Id);
        Track(row);

        Assert.AreEqual(string.Empty, row.ThumbnailPath, "Tried, and there isn't one: the backfill mustn't keep trying.");
    }

    [TestMethod]
    public async Task Backfill_MakesAThumbnailForAnOlderImage_Once()
    {
        string path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, Png(800, 600));
        _paths.Add(path);

        Guid id = Guid.NewGuid();
        await using (ApplicationDbContext db = new(_options))
        {
            db.FileMetadata.Add(new FileMetadata { Id = id, FileName = "old.png", FilePath = path, FileSize = 10, ContentType = "image/png", UploadedBy = "user1" });
            await db.SaveChangesAsync();
        }

        Result<bool> first = await _files.CreateMissingThumbnailAsync(id, CancellationToken.None);
        Result<bool> second = await _files.CreateMissingThumbnailAsync(id, CancellationToken.None);
        FileMetadata row = await RowAsync(id);
        Track(row);

        Assert.IsTrue(first.Data);
        Assert.IsFalse(second.Data, "Already has one.");
        Assert.AreEqual(800, row.Width);
    }

    [TestMethod]
    public async Task DeletingAFile_RemovesItsThumbnailToo()
    {
        Result<FileMetadata> uploaded = await _files.UploadFileAsync(Form("poster.png", Png(300, 300)), null, "user1", CancellationToken.None);
        FileMetadata row = await RowAsync(uploaded.Data!.Id);
        Track(row);

        await _files.DeleteFileAsync(row.Id, CancellationToken.None);

        Assert.IsFalse(File.Exists(row.ThumbnailPath));
        Assert.IsFalse(File.Exists(row.FilePath));
    }
}
