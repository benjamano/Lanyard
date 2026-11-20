using Lanyard.Application.Services;
using Lanyard.Application.Services.Locations;
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
    private static readonly LocationScope Admin = new(true, null, null, null);
    private static readonly LocationScope ManagerAtCompany1 = new(false, 10, 1, null, IsManager: true);
    private static readonly LocationScope ManagerAtCompany2 = new(false, 20, 2, null, IsManager: true);
    private static readonly LocationScope Staff = new(false, 10, 1, null);

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

        // Jane works at company 1's location 10, so a company 1 Manager manages her and a
        // company 2 Manager doesn't.
        ctx.Locations.Add(new Location { Id = 10, CompanyId = 1, Name = "Ipswich" });
        ctx.Locations.Add(new Location { Id = 20, CompanyId = 2, Name = "Elsewhere" });
        ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = user.Id, LocationId = 10 });

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
            .Setup(f => f.UploadFileAsync(It.IsAny<IFormFile>(), null, It.IsAny<string>(), It.IsAny<FileUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<FileMetadata>.Ok(new FileMetadata { Id = fileId, FileName = "cert.pdf", FilePath = "/tmp/cert.pdf" }));

        StaffDocumentService service = GetService(options, fileServiceMock);
        Mock<IFormFile> fileMock = new();

        Result<StaffDocument> result = await service.UploadStaffDocumentAsync(
            Admin, user.Id, type.Id, fileMock.Object, DateTime.UtcNow.AddYears(1), DateTime.UtcNow.AddMonths(6), "uploader-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(fileId, result.Data!.FileMetadataId);

        fileServiceMock.Verify(f => f.UploadFileAsync(fileMock.Object, null, "uploader-1",
            It.Is<FileUploadOptions>(o => o.IsRestricted && !o.AddAudioToMusicLibrary), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UploadStaffDocumentAsync_FailsWhenExpiryDateRequiredButMissing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (UserProfile user, StaffDocumentType type) = await SeedUserAndTypeAsync(options, requiresExpiryDate: true);

        StaffDocumentService service = GetService(options);

        Result<StaffDocument> result = await service.UploadStaffDocumentAsync(
            Admin, user.Id, type.Id, Mock.Of<IFormFile>(), null, null, "uploader-1", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task UploadStaffDocumentAsync_FailsWhenReminderDateRequiredButMissing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (UserProfile user, StaffDocumentType type) = await SeedUserAndTypeAsync(options, requiresExpiryDate: true);

        StaffDocumentService service = GetService(options);

        Result<StaffDocument> result = await service.UploadStaffDocumentAsync(
            Admin, user.Id, type.Id, Mock.Of<IFormFile>(), DateTime.UtcNow.AddYears(1), null, "uploader-1", CancellationToken.None);

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
            Admin, user.Id, type.Id, Mock.Of<IFormFile>(), expiryDate, reminderDate, "uploader-1", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task UploadStaffDocumentAsync_SucceedsWhenReminderDateOnExpiryDate()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (UserProfile user, StaffDocumentType type) = await SeedUserAndTypeAsync(options, requiresExpiryDate: true);

        Mock<IFileService> fileServiceMock = new();
        fileServiceMock
            .Setup(f => f.UploadFileAsync(It.IsAny<IFormFile>(), null, It.IsAny<string>(), It.IsAny<FileUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<FileMetadata>.Ok(new FileMetadata { Id = Guid.NewGuid(), FileName = "cert.pdf", FilePath = "/tmp/cert.pdf" }));

        StaffDocumentService service = GetService(options, fileServiceMock);

        DateTime expiryDate = DateTime.UtcNow.AddMonths(6);

        Result<StaffDocument> result = await service.UploadStaffDocumentAsync(
            Admin, user.Id, type.Id, Mock.Of<IFormFile>(), expiryDate, expiryDate, "uploader-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
    }

    private static async Task<StaffDocument> SeedDocumentAsync(
        DbContextOptions<ApplicationDbContext> options, DateTime expiryDate, DateTime? reminderDate, DateTime? reminderSentDate = null)
    {
        (UserProfile user, StaffDocumentType type) = await SeedUserAndTypeAsync(options);

        await using ApplicationDbContext ctx = new(options);

        FileMetadata file = new() { Id = Guid.NewGuid(), FileName = "cert.pdf", FilePath = "/tmp/cert.pdf", IsRestricted = true };
        ctx.FileMetadata.Add(file);

        StaffDocument document = new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            StaffDocumentTypeId = type.Id,
            FileMetadataId = file.Id,
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

    [TestMethod]
    public async Task UploadStaffDocumentAsync_IsRefusedForStaffAndOtherCompaniesManagers()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (UserProfile user, StaffDocumentType type) = await SeedUserAndTypeAsync(options, requiresExpiryDate: false);

        Mock<IFileService> fileServiceMock = new();
        StaffDocumentService service = GetService(options, fileServiceMock);

        foreach (LocationScope scope in new[] { Staff, ManagerAtCompany2 })
        {
            Result<StaffDocument> result = await service.UploadStaffDocumentAsync(
                scope, user.Id, type.Id, Mock.Of<IFormFile>(), null, null, "uploader-1", CancellationToken.None);

            Assert.IsFalse(result.IsSuccess);
        }

        fileServiceMock.Verify(f => f.UploadFileAsync(It.IsAny<IFormFile>(), It.IsAny<Guid?>(), It.IsAny<string>(),
            It.IsAny<FileUploadOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GetStaffDocumentsForUserAsync_OnlyAdminsAndTheirManagersSeeThem()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        StaffDocument document = await SeedDocumentAsync(options, DateTime.UtcNow.AddDays(60), null);
        StaffDocumentService service = GetService(options);

        Assert.AreEqual(1, (await service.GetStaffDocumentsForUserAsync(Admin, document.UserId)).Data!.Count);
        Assert.AreEqual(1, (await service.GetStaffDocumentsForUserAsync(ManagerAtCompany1, document.UserId)).Data!.Count);
        Assert.IsFalse((await service.GetStaffDocumentsForUserAsync(ManagerAtCompany2, document.UserId)).IsSuccess);
        Assert.IsFalse((await service.GetStaffDocumentsForUserAsync(Staff, document.UserId)).IsSuccess);
    }

    [TestMethod]
    public async Task OpenStaffDocumentFileAsync_OwnerAndManagersOnly_ThroughTheRestrictedPath()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        StaffDocument document = await SeedDocumentAsync(options, DateTime.UtcNow.AddDays(60), null);

        Mock<IFileService> fileServiceMock = new();
        fileServiceMock
            .Setup(f => f.OpenRestrictedFileContentAsync(document.FileMetadataId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<FileContent>.Fail("stubbed"));
        StaffDocumentService service = GetService(options, fileServiceMock);

        await service.OpenStaffDocumentFileAsync(document.Id, document.UserId, Staff, null, CancellationToken.None);
        await service.OpenStaffDocumentFileAsync(document.Id, "manager-1", ManagerAtCompany1, null, CancellationToken.None);
        fileServiceMock.Verify(f => f.OpenRestrictedFileContentAsync(document.FileMetadataId, null, It.IsAny<CancellationToken>()), Times.Exactly(2));

        Result<FileContent> colleague = await service.OpenStaffDocumentFileAsync(document.Id, "colleague-1", Staff, null, CancellationToken.None);
        Result<FileContent> otherCompany = await service.OpenStaffDocumentFileAsync(document.Id, "manager-2", ManagerAtCompany2, null, CancellationToken.None);

        Assert.IsFalse(colleague.IsSuccess);
        Assert.IsFalse(otherCompany.IsSuccess);
        Assert.AreEqual("Document not found.", colleague.Error, "A refusal must read the same as a missing document");
        fileServiceMock.Verify(f => f.OpenRestrictedFileContentAsync(It.IsAny<Guid>(), It.IsAny<FileByteRange?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        fileServiceMock.Verify(f => f.OpenFileContentAsync(It.IsAny<Guid>(), It.IsAny<FileByteRange?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task DeleteStaffDocumentAsync_RemovesTheRestrictedFile_AndRefusesStaff()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        StaffDocument document = await SeedDocumentAsync(options, DateTime.UtcNow.AddDays(60), null);

        Mock<IFileService> fileServiceMock = new();
        fileServiceMock
            .Setup(f => f.DeleteRestrictedFileAsync(document.FileMetadataId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Ok(true));
        StaffDocumentService service = GetService(options, fileServiceMock);

        Assert.IsFalse((await service.DeleteStaffDocumentAsync(Staff, document.Id, CancellationToken.None)).IsSuccess);
        Assert.IsTrue((await service.DeleteStaffDocumentAsync(ManagerAtCompany1, document.Id, CancellationToken.None)).IsSuccess);

        fileServiceMock.Verify(f => f.DeleteRestrictedFileAsync(document.FileMetadataId, It.IsAny<CancellationToken>()), Times.Once);
        fileServiceMock.Verify(f => f.DeleteFileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
