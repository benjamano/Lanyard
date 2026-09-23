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
            user.Id, type.Id, fileMock.Object, DateTime.UtcNow.AddYears(1), DateTime.UtcNow.AddMonths(6), "uploader-1", CancellationToken.None);

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
            user.Id, type.Id, Mock.Of<IFormFile>(), null, null, "uploader-1", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task UploadStaffDocumentAsync_FailsWhenReminderDateRequiredButMissing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (UserProfile user, StaffDocumentType type) = await SeedUserAndTypeAsync(options, requiresExpiryDate: true);

        StaffDocumentService service = GetService(options);

        Result<StaffDocument> result = await service.UploadStaffDocumentAsync(
            user.Id, type.Id, Mock.Of<IFormFile>(), DateTime.UtcNow.AddYears(1), null, "uploader-1", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task UploadStaffDocumentAsync_FailsWhenReminderDateAfterExpiryDate()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (UserProfile user, StaffDocumentType type) = await SeedUserAndTypeAsync(options, requiresExpiryDate: true);

        StaffDocumentService service = GetService(options);

        DateTime expiryDate = DateTime.UtcNow.AddMonths(6);
        DateTime reminderDate = expiryDate.AddDays(1);

        Result<StaffDocument> result = await service.UploadStaffDocumentAsync(
            user.Id, type.Id, Mock.Of<IFormFile>(), expiryDate, reminderDate, "uploader-1", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task UploadStaffDocumentAsync_SucceedsWhenReminderDateOnExpiryDate()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (UserProfile user, StaffDocumentType type) = await SeedUserAndTypeAsync(options, requiresExpiryDate: true);

        Mock<IFileService> fileServiceMock = new();
        fileServiceMock
            .Setup(f => f.UploadFileAsync(It.IsAny<IFormFile>(), null, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<FileMetadata>.Ok(new FileMetadata { Id = Guid.NewGuid(), FileName = "cert.pdf", FilePath = "/tmp/cert.pdf" }));

        StaffDocumentService service = GetService(options, fileServiceMock);

        DateTime expiryDate = DateTime.UtcNow.AddMonths(6);

        Result<StaffDocument> result = await service.UploadStaffDocumentAsync(
            user.Id, type.Id, Mock.Of<IFormFile>(), expiryDate, expiryDate, "uploader-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
    }

    private static async Task<StaffDocument> SeedDocumentAsync(
        DbContextOptions<ApplicationDbContext> options, DateTime expiryDate, DateTime? reminderDate, DateTime? reminderSentDate = null)
    {
        (UserProfile user, StaffDocumentType type) = await SeedUserAndTypeAsync(options);

        await using ApplicationDbContext ctx = new(options);

        StaffDocument document = new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            StaffDocumentTypeId = type.Id,
            FileMetadataId = Guid.NewGuid(),
            ExpiryDate = expiryDate,
            ReminderDate = reminderDate,
            ReminderSentDate = reminderSentDate,
            UploadedDate = DateTime.UtcNow.AddYears(-1),
            UploadedByUserId = user.Id,
            IsActive = true
        };
        ctx.StaffDocuments.Add(document);

        await ctx.SaveChangesAsync();

        return document;
    }

    [TestMethod]
    public async Task GetDocumentsWithPendingRemindersAsync_IncludesDocumentInsideReminderWindow()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        StaffDocument document = await SeedDocumentAsync(options, DateTime.UtcNow.AddDays(5), DateTime.UtcNow.AddDays(-2));

        StaffDocumentService service = GetService(options);
        Result<List<StaffDocument>> result = await service.GetDocumentsWithPendingRemindersAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Data!.Count);
        Assert.AreEqual(document.Id, result.Data![0].Id);
    }

    [TestMethod]
    public async Task GetDocumentsWithPendingRemindersAsync_IncludesDocumentWhenReminderDateEqualsExpiryDate()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DateTime sameDay = DateTime.UtcNow.Date;
        await SeedDocumentAsync(options, sameDay, sameDay);

        StaffDocumentService service = GetService(options);
        Result<List<StaffDocument>> result = await service.GetDocumentsWithPendingRemindersAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Data!.Count);
    }

    [TestMethod]
    public async Task GetDocumentsWithPendingRemindersAsync_ExcludesDocumentOutsideReminderWindow()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedDocumentAsync(options, DateTime.UtcNow.AddDays(60), DateTime.UtcNow.AddDays(55));

        StaffDocumentService service = GetService(options);
        Result<List<StaffDocument>> result = await service.GetDocumentsWithPendingRemindersAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Data!.Count);
    }

    [TestMethod]
    public async Task GetDocumentsWithPendingRemindersAsync_ExcludesDocumentAlreadySent()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DateTime expiryDate = DateTime.UtcNow.AddDays(5);
        StaffDocument document = await SeedDocumentAsync(options, expiryDate, DateTime.UtcNow.AddDays(-2));

        StaffDocumentService service = GetService(options);
        Result<bool> markResult = await service.MarkReminderSentAsync(document.Id);
        Assert.IsTrue(markResult.IsSuccess);

        Result<List<StaffDocument>> result = await service.GetDocumentsWithPendingRemindersAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Data!.Count);
    }

    [TestMethod]
    public async Task MarkReminderSentAsync_FailsOnSecondCallForSameDocument()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        StaffDocument document = await SeedDocumentAsync(options, DateTime.UtcNow.AddDays(5), DateTime.UtcNow.AddDays(-2));

        StaffDocumentService service = GetService(options);

        Result<bool> firstResult = await service.MarkReminderSentAsync(document.Id);
        Assert.IsTrue(firstResult.IsSuccess);

        // A second claim on the same document must not silently succeed, or the hosted service
        // would send a duplicate email - see ReminderSentDate's [ConcurrencyCheck] for the case
        // where the second read also observes null (a true overlapping-sweep race).
        Result<bool> secondResult = await service.MarkReminderSentAsync(document.Id);
        Assert.IsFalse(secondResult.IsSuccess);
    }

    [TestMethod]
    public async Task GetDocumentsWithPendingRemindersAsync_ReIncludesAfterReminderSentDateClearedOnRenewal()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DateTime originalExpiry = DateTime.UtcNow.AddDays(5);
        StaffDocument document = await SeedDocumentAsync(options, originalExpiry, DateTime.UtcNow.AddDays(-2));

        StaffDocumentService service = GetService(options);
        await service.MarkReminderSentAsync(document.Id);

        // Simulate a renewal: re-uploading the document creates a fresh row in practice, but the
        // effect on this row (a new expiry/reminder pair, reminder eligible again) is the same as
        // clearing ReminderSentDate alongside updating the dates.
        DateTime renewedExpiry = DateTime.UtcNow.AddDays(6);
        DateTime renewedReminder = DateTime.UtcNow.AddDays(-1);

        await using (ApplicationDbContext ctx = new(options))
        {
            StaffDocument tracked = await ctx.StaffDocuments.FirstAsync(x => x.Id == document.Id);
            tracked.ExpiryDate = renewedExpiry;
            tracked.ReminderDate = renewedReminder;
            tracked.ReminderSentDate = null;
            await ctx.SaveChangesAsync();
        }

        Result<List<StaffDocument>> result = await service.GetDocumentsWithPendingRemindersAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Data!.Count);
    }
}
