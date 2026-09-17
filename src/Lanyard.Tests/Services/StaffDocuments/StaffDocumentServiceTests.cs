using Lanyard.Application.Services;
using Lanyard.Application.Services.StaffDocuments;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.StaffDocuments;

[TestClass]
public class StaffDocumentServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static StaffDocumentService GetService(
        DbContextOptions<ApplicationDbContext> options,
        Mock<IFileService>? fileServiceMock = null)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new StaffDocumentService(factoryMock.Object, (fileServiceMock ?? new Mock<IFileService>()).Object);
    }

    private static async Task<(UserProfile user, StaffDocumentType type)> SeedUserAndTypeAsync(
        DbContextOptions<ApplicationDbContext> options, bool requiresExpiryDate = true)
    {
        await using ApplicationDbContext ctx = new(options);

        UserProfile user = new() { Id = "user-1", UserName = "user-1", FirstName = "Jane", LastName = "Doe" };
        ctx.Users.Add(user);

        StaffDocumentType type = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = 1,
            Name = "First Aid Certificate",
            RequiresExpiryDate = requiresExpiryDate,
            IsActive = true
        };
        ctx.StaffDocumentTypes.Add(type);

        await ctx.SaveChangesAsync();

        return (user, type);
    }

    [TestMethod]
    public async Task UploadStaffDocumentAsync_DelegatesToFileServiceAndPersistsDocument()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (UserProfile user, StaffDocumentType type) = await SeedUserAndTypeAsync(options);

        Guid fileId = Guid.NewGuid();
        Mock<IFileService> fileServiceMock = new();
        fileServiceMock
            .Setup(f => f.UploadFileAsync(It.IsAny<IFormFile>(), null, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<FileMetadata>.Ok(new FileMetadata { Id = fileId, FileName = "cert.pdf", FilePath = "/tmp/cert.pdf" }));

        StaffDocumentService service = GetService(options, fileServiceMock);
        Mock<IFormFile> fileMock = new();

        Result<StaffDocument> result = await service.UploadStaffDocumentAsync(
            user.Id, type.Id, fileMock.Object, DateTime.UtcNow.AddYears(1), "uploader-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(fileId, result.Data!.FileMetadataId);

        fileServiceMock.Verify(f => f.UploadFileAsync(fileMock.Object, null, "uploader-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UploadStaffDocumentAsync_FailsWhenExpiryDateRequiredButMissing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (UserProfile user, StaffDocumentType type) = await SeedUserAndTypeAsync(options, requiresExpiryDate: true);

        StaffDocumentService service = GetService(options);

        Result<StaffDocument> result = await service.UploadStaffDocumentAsync(
            user.Id, type.Id, Mock.Of<IFormFile>(), null, "uploader-1", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
    }

    private static async Task<(StaffDocument document, StaffDocumentReminderInterval interval)> SeedDocumentWithIntervalAsync(
        DbContextOptions<ApplicationDbContext> options, DateTime expiryDate, int daysBeforeExpiry)
    {
        (UserProfile user, StaffDocumentType type) = await SeedUserAndTypeAsync(options);

        await using ApplicationDbContext ctx = new(options);

        StaffDocumentReminderInterval interval = new()
        {
            Id = Guid.NewGuid(),
            StaffDocumentTypeId = type.Id,
            DaysBeforeExpiry = daysBeforeExpiry,
            IsActive = true
        };
        ctx.StaffDocumentReminderIntervals.Add(interval);

        StaffDocument document = new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            StaffDocumentTypeId = type.Id,
            FileMetadataId = Guid.NewGuid(),
            ExpiryDate = expiryDate,
            UploadedDate = DateTime.UtcNow.AddYears(-1),
            UploadedByUserId = user.Id,
            IsActive = true
        };
        ctx.StaffDocuments.Add(document);

        await ctx.SaveChangesAsync();

        return (document, interval);
    }

    [TestMethod]
    public async Task GetDocumentsWithPendingRemindersAsync_IncludesDocumentInsideReminderWindow()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (StaffDocument document, StaffDocumentReminderInterval interval) =
            await SeedDocumentWithIntervalAsync(options, DateTime.UtcNow.AddDays(5), daysBeforeExpiry: 7);

        StaffDocumentService service = GetService(options);
        Result<List<PendingStaffDocumentReminder>> result = await service.GetDocumentsWithPendingRemindersAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Data!.Count);
        Assert.AreEqual(document.Id, result.Data![0].Document.Id);
        Assert.AreEqual(interval.Id, result.Data![0].Interval.Id);
    }

    [TestMethod]
    public async Task GetDocumentsWithPendingRemindersAsync_ExcludesDocumentOutsideReminderWindow()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedDocumentWithIntervalAsync(options, DateTime.UtcNow.AddDays(60), daysBeforeExpiry: 7);

        StaffDocumentService service = GetService(options);
        Result<List<PendingStaffDocumentReminder>> result = await service.GetDocumentsWithPendingRemindersAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Data!.Count);
    }

    [TestMethod]
    public async Task GetDocumentsWithPendingRemindersAsync_ExcludesIntervalAlreadySentForCurrentExpiry()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DateTime expiryDate = DateTime.UtcNow.AddDays(5);
        (StaffDocument document, StaffDocumentReminderInterval interval) =
            await SeedDocumentWithIntervalAsync(options, expiryDate, daysBeforeExpiry: 7);

        StaffDocumentService service = GetService(options);
        Result<bool> markResult = await service.MarkReminderSentAsync(document.Id, interval.Id, expiryDate);
        Assert.IsTrue(markResult.IsSuccess);

        Result<List<PendingStaffDocumentReminder>> result = await service.GetDocumentsWithPendingRemindersAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Data!.Count);
    }

    [TestMethod]
    public async Task GetDocumentsWithPendingRemindersAsync_ReIncludesAfterExpiryDateChangesOnRenewal()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DateTime originalExpiry = DateTime.UtcNow.AddDays(5);
        (StaffDocument document, StaffDocumentReminderInterval interval) =
            await SeedDocumentWithIntervalAsync(options, originalExpiry, daysBeforeExpiry: 7);

        StaffDocumentService service = GetService(options);
        await service.MarkReminderSentAsync(document.Id, interval.Id, originalExpiry);

        // Simulate a renewal: re-uploading the document with a new expiry date, still inside
        // the same 7-day window relative to "now".
        DateTime renewedExpiry = DateTime.UtcNow.AddDays(6);

        await using (ApplicationDbContext ctx = new(options))
        {
            StaffDocument tracked = await ctx.StaffDocuments.FirstAsync(x => x.Id == document.Id);
            tracked.ExpiryDate = renewedExpiry;
            await ctx.SaveChangesAsync();
        }

        Result<List<PendingStaffDocumentReminder>> result = await service.GetDocumentsWithPendingRemindersAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Data!.Count);
    }
}
