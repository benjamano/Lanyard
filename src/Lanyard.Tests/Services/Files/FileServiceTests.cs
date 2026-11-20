using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DTO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Lanyard.Application.Services;
using Microsoft.EntityFrameworkCore;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Application.Services.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Lanyard.Tests.Services.Files;


[TestClass]
public class FileServiceTests
{
    private Mock<IDbContextFactory<ApplicationDbContext>> _dbFactoryMock = null!;
    private Mock<ICurrentUserAccessor> _currentUserAccessorMock = null!;
    private Mock<ISongAnalysisQueue> _analysisQueueMock = null!;
    private Mock<IWebHostEnvironment> _environmentMock = null!;
    private FileService _fileService = null!;
    private ApplicationDbContext _dbContext = null!;
    private DbContextOptions<ApplicationDbContext> _options = null!;

    // Reads straight from the store, bypassing whatever the seeding context still tracks.
    private async Task<FileMetadata?> FindFileFreshAsync(Guid fileId)
    {
        await using ApplicationDbContext ctx = new(_options);
        return await ctx.FileMetadata.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId);
    }

    [TestInitialize]
    public void Setup()
    {
        _dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        _currentUserAccessorMock = new Mock<ICurrentUserAccessor>();
        _analysisQueueMock = new Mock<ISongAnalysisQueue>();
        _environmentMock = new Mock<IWebHostEnvironment>();
        _environmentMock.SetupGet(x => x.EnvironmentName).Returns(Environments.Development);
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new ApplicationDbContext(_options);
        // The service disposes every context it creates, so hand it a fresh one per call and keep
        // _dbContext (same named in-memory database) for seeding and assertions.
        _dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));
        _fileService = new FileService(_dbFactoryMock.Object, _currentUserAccessorMock.Object, _analysisQueueMock.Object, _environmentMock.Object);
    }

    [TestMethod]
    public async Task UploadFileAsync_WhenUploadingValidFileThenFileIsSaved()
    {
        var fileMock = new Mock<IFormFile>();
        var content = new MemoryStream(new byte[] { 1, 2, 3 });
        fileMock.Setup(f => f.FileName).Returns("test.txt");
        fileMock.Setup(f => f.Length).Returns(content.Length);
        fileMock.Setup(f => f.ContentType).Returns("text/plain");
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((Stream s, CancellationToken ct) => content.CopyToAsync(s, ct));

        _currentUserAccessorMock.Setup(s => s.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok("user1"));

        var result = await _fileService.UploadFileAsync(fileMock.Object, null, CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.Data);
        Assert.AreEqual("test.txt", result.Data.FileName);
        Assert.AreEqual("user1", result.Data.UploadedBy);
    }

    [TestMethod]
    public async Task DeleteFileAsync_WhenDeletingFileThenFileIsRemoved()
    {
        var fileId = Guid.NewGuid();
        var fileMeta = new FileMetadata
        {
            Id = fileId,
            FileName = "delete.txt",
            FilePath = Path.GetTempFileName(),
            FileSize = 10,
            ContentType = "text/plain",
            UploadedAt = DateTime.UtcNow,
            UploadedBy = "user1",
            IsActive = true
        };
        await _dbContext.FileMetadata.AddAsync(fileMeta);
        await _dbContext.SaveChangesAsync();
        File.WriteAllText(fileMeta.FilePath, "dummy");

        var result = await _fileService.DeleteFileAsync(fileId, CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Data);
        Assert.IsNull(await FindFileFreshAsync(fileId));
        Assert.IsFalse(File.Exists(fileMeta.FilePath));
    }

    private async Task<(Guid FileId, string FilePath, Guid ClipId)> SeedClipFileAsync(bool clipDeleted)
    {
        Guid fileId = Guid.NewGuid();
        string filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, "audio");

        await _dbContext.FileMetadata.AddAsync(new FileMetadata { Id = fileId, FileName = "closing.mp3", FilePath = filePath, FileSize = 5, UploadedAt = DateTime.UtcNow, UploadedBy = "user1", IsActive = true });

        AudioClip clip = new() { Id = Guid.NewGuid(), Name = "Closing time", FileMetadataId = fileId, CreateDate = DateTime.UtcNow, DeleteDate = clipDeleted ? DateTime.UtcNow : null };
        await _dbContext.AudioClips.AddAsync(clip);
        await _dbContext.SaveChangesAsync();

        return (fileId, filePath, clip.Id);
    }

    [TestMethod]
    public async Task DeleteFileAsync_WhenFileIsALiveAudioClip_FailsWithoutDeletingStorage()
    {
        (Guid fileId, string filePath, _) = await SeedClipFileAsync(clipDeleted: false);

        Result<bool> result = await _fileService.DeleteFileAsync(fileId, CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "audio clip");
        Assert.IsNotNull(await FindFileFreshAsync(fileId));
        Assert.IsTrue(File.Exists(filePath));
    }

    [TestMethod]
    public async Task DeleteFileAsync_WhenOnlyADeletedClipUsesTheFile_DeletesItAndDetachesTheClip()
    {
        (Guid fileId, string filePath, Guid clipId) = await SeedClipFileAsync(clipDeleted: true);

        Result<bool> result = await _fileService.DeleteFileAsync(fileId, CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNull(await FindFileFreshAsync(fileId));
        Assert.IsFalse(File.Exists(filePath));

        await using ApplicationDbContext ctx = new(_options);
        AudioClip clip = await ctx.AudioClips.AsNoTracking().SingleAsync(x => x.Id == clipId);
        Assert.IsNull(clip.FileMetadataId);
    }

    // A shared FileMetadata row (StaffDocuments, CompanyOnboardingStandingAttachments, and
    // ordinary File Manager entries all point at the same table) must not be deletable while
    // another feature still references it - the physical bytes are deleted before SaveChangesAsync
    // and can't be rolled back, so this has to be caught before storage is touched at all, not left
    // to the FK's Restrict behavior to reject only the DB half of the operation.
    [TestMethod]
    public async Task DeleteFileAsync_WhenFileIsReferencedByAStaffDocument_FailsWithoutDeletingStorage()
    {
        var fileId = Guid.NewGuid();
        var filePath = Path.GetTempFileName();
        var fileMeta = new FileMetadata
        {
            Id = fileId,
            FileName = "handbook.pdf",
            FilePath = filePath,
            FileSize = 10,
            ContentType = "application/pdf",
            UploadedAt = DateTime.UtcNow,
            UploadedBy = "user1",
            IsActive = true
        };
        await _dbContext.FileMetadata.AddAsync(fileMeta);
        await _dbContext.StaffDocuments.AddAsync(new StaffDocument
        {
            Id = Guid.NewGuid(),
            UserId = "staff-user",
            StaffDocumentTypeId = Guid.NewGuid(),
            FileMetadataId = fileId,
            UploadedByUserId = "user1",
            UploadedDate = DateTime.UtcNow,
            IsActive = true
        });
        await _dbContext.SaveChangesAsync();
        File.WriteAllText(filePath, "dummy");

        var result = await _fileService.DeleteFileAsync(fileId, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(await FindFileFreshAsync(fileId));
        Assert.IsTrue(File.Exists(filePath));

        File.Delete(filePath);
    }

    // Regression test: RemoveStandingAttachmentAsync only soft-deletes (IsActive = false), so once
    // a file has ever been used as a standing attachment or staff document it must become
    // deletable again as soon as that reference is inactive - the reference checks above must
    // filter on IsActive, not just existence, or a removed attachment permanently blocks deletion.
    [TestMethod]
    public async Task DeleteFileAsync_WhenOnlyReferencedByInactiveStaffDocument_Succeeds()
    {
        var fileId = Guid.NewGuid();
        var filePath = Path.GetTempFileName();
        var fileMeta = new FileMetadata
        {
            Id = fileId,
            FileName = "handbook.pdf",
            FilePath = filePath,
            FileSize = 10,
            ContentType = "application/pdf",
            UploadedAt = DateTime.UtcNow,
            UploadedBy = "user1",
            IsActive = true
        };
        await _dbContext.FileMetadata.AddAsync(fileMeta);
        await _dbContext.StaffDocuments.AddAsync(new StaffDocument
        {
            Id = Guid.NewGuid(),
            UserId = "staff-user",
            StaffDocumentTypeId = Guid.NewGuid(),
            FileMetadataId = fileId,
            UploadedByUserId = "user1",
            UploadedDate = DateTime.UtcNow,
            IsActive = false
        });
        await _dbContext.SaveChangesAsync();
        File.WriteAllText(filePath, "dummy");

        var result = await _fileService.DeleteFileAsync(fileId, CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNull(await FindFileFreshAsync(fileId));
        Assert.IsFalse(File.Exists(filePath));
    }

    [TestMethod]
    public async Task DeleteFileAsync_WhenOnlyReferencedByInactiveOnboardingStandingAttachment_Succeeds()
    {
        var fileId = Guid.NewGuid();
        var filePath = Path.GetTempFileName();
        var fileMeta = new FileMetadata
        {
            Id = fileId,
            FileName = "handbook.pdf",
            FilePath = filePath,
            FileSize = 10,
            ContentType = "application/pdf",
            UploadedAt = DateTime.UtcNow,
            UploadedBy = "user1",
            IsActive = true
        };
        await _dbContext.FileMetadata.AddAsync(fileMeta);
        await _dbContext.CompanyOnboardingStandingAttachments.AddAsync(new CompanyOnboardingStandingAttachment
        {
            Id = Guid.NewGuid(),
            CompanyId = 1,
            FileMetadataId = fileId,
            SortOrder = 0,
            IsActive = false
        });
        await _dbContext.SaveChangesAsync();
        File.WriteAllText(filePath, "dummy");

        var result = await _fileService.DeleteFileAsync(fileId, CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNull(await FindFileFreshAsync(fileId));
        Assert.IsFalse(File.Exists(filePath));
    }

    [TestMethod]
    public async Task DeleteFileAsync_WhenReferencedByActiveOnboardingStandingAttachment_FailsWithoutDeletingStorage()
    {
        var fileId = Guid.NewGuid();
        var filePath = Path.GetTempFileName();
        var fileMeta = new FileMetadata
        {
            Id = fileId,
            FileName = "handbook.pdf",
            FilePath = filePath,
            FileSize = 10,
            ContentType = "application/pdf",
            UploadedAt = DateTime.UtcNow,
            UploadedBy = "user1",
            IsActive = true
        };
        await _dbContext.FileMetadata.AddAsync(fileMeta);
        await _dbContext.CompanyOnboardingStandingAttachments.AddAsync(new CompanyOnboardingStandingAttachment
        {
            Id = Guid.NewGuid(),
            CompanyId = 1,
            FileMetadataId = fileId,
            SortOrder = 0,
            IsActive = true
        });
        await _dbContext.SaveChangesAsync();
        File.WriteAllText(filePath, "dummy");

        var result = await _fileService.DeleteFileAsync(fileId, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(await FindFileFreshAsync(fileId));
        Assert.IsTrue(File.Exists(filePath));

        File.Delete(filePath);
    }

    [TestMethod]
    public async Task RenameFileAsync_WhenRenamingFileThenNameIsUpdated()
    {
        var fileId = Guid.NewGuid();
        var fileMeta = new FileMetadata
        {
            Id = fileId,
            FileName = "oldname.txt",
            FilePath = Path.GetTempFileName(),
            FileSize = 10,
            ContentType = "text/plain",
            UploadedAt = DateTime.UtcNow,
            UploadedBy = "user1",
            IsActive = true
        };

        await _dbContext.FileMetadata.AddAsync(fileMeta);
        await _dbContext.SaveChangesAsync();

        var result = await _fileService.RenameFileAsync(fileId, "newname.txt", CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.Data);
        Assert.AreEqual("newname.txt", result.Data.FileName);
    }

    [TestMethod]
    public async Task GetFolderAsync_WhenFolderExistsThenReturnsIt()
    {
        var folderId = Guid.NewGuid();
        var folder = new Folder
        {
            Id = folderId,
            Name = "Nested",
            ParentFolderId = null,
            CreatedBy = "user1",
            IsActive = true
        };

        await _dbContext.Folders.AddAsync(folder);
        await _dbContext.SaveChangesAsync();

        var result = await _fileService.GetFolderAsync(folderId, CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.Data);
        Assert.AreEqual("Nested", result.Data.Name);
    }

    [TestMethod]
    public async Task GetFolderAsync_WhenFolderDoesNotExistThenFails()
    {
        var result = await _fileService.GetFolderAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task CreateFolderAsync_WhenUserIsResolvedThenFolderIsCreatedWithThatUser()
    {
        _currentUserAccessorMock.Setup(s => s.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok("user1"));

        var result = await _fileService.CreateFolderAsync("New Folder", null, CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.Data);
        Assert.AreEqual("New Folder", result.Data.Name);
        Assert.AreEqual("user1", result.Data.CreatedBy);
    }

    [TestMethod]
    public async Task MoveFileAsync_WhenFileAndDestinationExistThenFolderIdIsUpdated()
    {
        var fileId = Guid.NewGuid();
        var destinationFolderId = Guid.NewGuid();
        var fileMeta = new FileMetadata
        {
            Id = fileId,
            FileName = "move.txt",
            FilePath = Path.GetTempFileName(),
            FileSize = 10,
            ContentType = "text/plain",
            UploadedAt = DateTime.UtcNow,
            UploadedBy = "user1",
            FolderId = null,
            IsActive = true
        };
        var destinationFolder = new Folder
        {
            Id = destinationFolderId,
            Name = "Destination",
            CreatedBy = "user1",
            IsActive = true
        };

        await _dbContext.FileMetadata.AddAsync(fileMeta);
        await _dbContext.Folders.AddAsync(destinationFolder);
        await _dbContext.SaveChangesAsync();

        var result = await _fileService.MoveFileAsync(fileId, destinationFolderId, CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.Data);
        Assert.AreEqual(destinationFolderId, result.Data.FolderId);
    }

    [TestMethod]
    public async Task MoveFileAsync_WhenFileDoesNotExistThenFails()
    {
        var result = await _fileService.MoveFileAsync(Guid.NewGuid(), null, CancellationToken.None);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task MoveFileAsync_WhenDestinationFolderDoesNotExistThenFails()
    {
        var fileId = Guid.NewGuid();
        var fileMeta = new FileMetadata
        {
            Id = fileId,
            FileName = "move.txt",
            FilePath = Path.GetTempFileName(),
            FileSize = 10,
            ContentType = "text/plain",
            UploadedAt = DateTime.UtcNow,
            UploadedBy = "user1",
            IsActive = true
        };

        await _dbContext.FileMetadata.AddAsync(fileMeta);
        await _dbContext.SaveChangesAsync();

        var result = await _fileService.MoveFileAsync(fileId, Guid.NewGuid(), CancellationToken.None);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task ListFilesAsync_NullFolder_ReturnsOnlyRootFiles()
    {
        Guid folderId = Guid.NewGuid();
        await _dbContext.Folders.AddAsync(new Folder { Id = folderId, Name = "Sub", CreatedAt = DateTime.UtcNow, CreatedBy = "user1", IsActive = true });
        await _dbContext.FileMetadata.AddAsync(NewFile("root.txt", folderId: null));
        await _dbContext.FileMetadata.AddAsync(NewFile("nested.txt", folderId: folderId));
        await _dbContext.SaveChangesAsync();

        Result<IReadOnlyList<FileMetadata>> root = await _fileService.ListFilesAsync(null, CancellationToken.None);
        Result<IReadOnlyList<FileMetadata>> nested = await _fileService.ListFilesAsync(folderId, CancellationToken.None);

        Assert.IsTrue(root.Success);
        Assert.AreEqual(1, root.Data!.Count);
        Assert.AreEqual("root.txt", root.Data[0].FileName);
        Assert.IsTrue(nested.Success);
        Assert.AreEqual(1, nested.Data!.Count);
        Assert.AreEqual("nested.txt", nested.Data[0].FileName);
    }

    [TestMethod]
    public async Task ListFoldersAsync_NullParent_ReturnsOnlyTopLevelFolders()
    {
        Guid parentId = Guid.NewGuid();
        await _dbContext.Folders.AddAsync(new Folder { Id = parentId, Name = "Top", CreatedAt = DateTime.UtcNow, CreatedBy = "user1", IsActive = true });
        await _dbContext.Folders.AddAsync(new Folder { Id = Guid.NewGuid(), Name = "Child", ParentFolderId = parentId, CreatedAt = DateTime.UtcNow, CreatedBy = "user1", IsActive = true });
        await _dbContext.SaveChangesAsync();

        Result<IReadOnlyList<Folder>> top = await _fileService.ListFoldersAsync(null, CancellationToken.None);

        Assert.IsTrue(top.Success);
        Assert.AreEqual(1, top.Data!.Count);
        Assert.AreEqual("Top", top.Data[0].Name);
    }

    [TestMethod]
    public async Task OpenFileContentAsync_InDevelopment_ReturnsSeekableStreamWithMetadata()
    {
        FileMetadata fileMeta = NewFile("clip.bin", folderId: null);
        File.WriteAllBytes(fileMeta.FilePath, [1, 2, 3, 4, 5]);
        await _dbContext.FileMetadata.AddAsync(fileMeta);
        await _dbContext.SaveChangesAsync();

        Result<FileContent> result = await _fileService.OpenFileContentAsync(fileMeta.Id, new FileByteRange(1, 3), CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error);
        await using FileContent content = result.Data!;
        Assert.IsTrue(content.Stream.CanSeek, "Local files must stay seekable so ASP.NET's range processing can slice them.");
        Assert.AreEqual(5, content.TotalLength);
        Assert.IsFalse(content.IsPartial);
        Assert.AreEqual(fileMeta.Id, content.Metadata.Id);
        Assert.AreEqual("clip.bin", content.Metadata.FileName);
    }

    [TestMethod]
    public async Task OpenFileContentAsync_WhenFileDoesNotExist_Fails()
    {
        Result<FileContent> result = await _fileService.OpenFileContentAsync(Guid.NewGuid(), null, CancellationToken.None);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public void ParseContentRange_ReadsPartialAndFullResponses()
    {
        (long total, long? start, long? end) partial = FileService.ParseContentRange("bytes 10-19/100", 10, 0);
        Assert.AreEqual(100, partial.total);
        Assert.AreEqual(10, partial.start);
        Assert.AreEqual(19, partial.end);

        (long total, long? start, long? end) full = FileService.ParseContentRange(null, 250, 999);
        Assert.AreEqual(250, full.total);
        Assert.IsNull(full.start);
        Assert.IsNull(full.end);

        (long total, long? start, long? end) fallback = FileService.ParseContentRange(null, null, 999);
        Assert.AreEqual(999, fallback.total);
    }

    // A staff HR document is restricted: api/files/download admits every signed-in user and kiosk
    // in the company, so none of the File Manager paths may reach it.
    [TestMethod]
    public async Task RestrictedFile_IsInvisibleToEveryFileManagerPath()
    {
        FileMetadata restricted = NewFile("right-to-work.pdf", null);
        restricted.IsRestricted = true;
        File.WriteAllBytes(restricted.FilePath, [1, 2, 3, 4, 5]);
        _dbContext.FileMetadata.Add(restricted);
        await _dbContext.SaveChangesAsync();

        Result<IReadOnlyList<FileMetadata>> listed = await _fileService.ListFilesAsync(null, CancellationToken.None);
        Assert.IsFalse(listed.Data!.Any(x => x.Id == restricted.Id));

        Assert.IsFalse((await _fileService.OpenFileContentAsync(restricted.Id, null, CancellationToken.None)).Success);
        Assert.IsFalse((await _fileService.DownloadFileAsync(restricted.Id, CancellationToken.None)).Success);
        Assert.IsFalse((await _fileService.GetFileMetadataAsync(restricted.Id, CancellationToken.None)).Success);
        Assert.IsFalse((await _fileService.RenameFileAsync(restricted.Id, "renamed.pdf", CancellationToken.None)).Success);
        Assert.IsFalse((await _fileService.MoveFileAsync(restricted.Id, null, CancellationToken.None)).Success);
        Assert.IsFalse((await _fileService.DeleteFileAsync(restricted.Id, CancellationToken.None)).Success);

        FileMetadata? stillThere = await FindFileFreshAsync(restricted.Id);
        Assert.IsNotNull(stillThere);
        Assert.AreEqual("right-to-work.pdf", stillThere.FileName);
    }

    [TestMethod]
    public async Task RestrictedFile_OpensThroughTheRestrictedPathOnly()
    {
        FileMetadata restricted = NewFile("right-to-work.pdf", null);
        restricted.IsRestricted = true;
        File.WriteAllBytes(restricted.FilePath, [1, 2, 3, 4, 5]);
        FileMetadata ordinary = NewFile("menu.pdf", null);
        File.WriteAllBytes(ordinary.FilePath, [1, 2, 3, 4, 5]);
        _dbContext.FileMetadata.AddRange(restricted, ordinary);
        await _dbContext.SaveChangesAsync();

        Result<FileContent> opened = await _fileService.OpenRestrictedFileContentAsync(restricted.Id, null, CancellationToken.None);
        Assert.IsTrue(opened.Success, opened.Error);
        await opened.Data!.DisposeAsync();

        Assert.IsFalse((await _fileService.OpenRestrictedFileContentAsync(ordinary.Id, null, CancellationToken.None)).Success,
            "The restricted path isn't a way round an ordinary file's own rules either");
    }

    [TestMethod]
    public async Task UploadFileAsync_RestrictedOption_IsStoredOnTheRow()
    {
        Mock<IFormFile> fileMock = new();
        MemoryStream content = new("%PDF-1.7"u8.ToArray());
        fileMock.Setup(f => f.FileName).Returns("right-to-work.pdf");
        fileMock.Setup(f => f.Length).Returns(content.Length);
        fileMock.Setup(f => f.ContentType).Returns("application/pdf");
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((Stream s, CancellationToken ct) => content.CopyToAsync(s, ct));

        Result<FileMetadata> result = await _fileService.UploadFileAsync(fileMock.Object, null, "admin",
            new FileUploadOptions(AddAudioToMusicLibrary: false, IsRestricted: true), CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsTrue((await FindFileFreshAsync(result.Data!.Id))!.IsRestricted);
    }

    private static Mock<IFormFile> FormFile(string name, string claimedContentType, long? length = null)
    {
        Mock<IFormFile> fileMock = new();
        byte[] bytes = [1, 2, 3, 4, 5];
        fileMock.Setup(f => f.FileName).Returns(name);
        fileMock.Setup(f => f.Length).Returns(length ?? bytes.Length);
        fileMock.Setup(f => f.ContentType).Returns(claimedContentType);
        fileMock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(bytes));
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((Stream s, CancellationToken ct) => new MemoryStream(bytes).CopyToAsync(s, ct));
        return fileMock;
    }

    // A FileService whose contexts are tenant-filtered to one company, like a signed-in caller's.
    private FileService ServiceForCompany(int companyId)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options, new FixedTenantProvider(companyId)));
        return new FileService(factory.Object, _currentUserAccessorMock.Object, _analysisQueueMock.Object, _environmentMock.Object);
    }

    [TestMethod]
    [DataRow("payload.exe", "application/octet-stream")]
    [DataRow("page.html", "text/html")]
    [DataRow("script.js", "text/javascript")]
    [DataRow("no-extension", "image/png")]
    public async Task UploadFileAsync_RefusesTypesOutsideTheAllowList(string name, string claimedContentType)
    {
        Result<FileMetadata> result = await _fileService.UploadFileAsync(FormFile(name, claimedContentType).Object, null, "user1", CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsFalse(await _dbContext.FileMetadata.AnyAsync());
    }

    [TestMethod]
    public async Task UploadFileAsync_RefusesAFileOverTheSizeLimit()
    {
        Result<FileMetadata> result = await _fileService.UploadFileAsync(
            FormFile("huge.mp4", "video/mp4", FileUploadPolicy.MaxBytes + 1).Object, null, "user1", CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsFalse(await _dbContext.FileMetadata.AnyAsync());
    }

    [TestMethod]
    public async Task UploadFileAsync_StoresTheContentTypeOfTheExtension_NotWhatTheBrowserClaimed()
    {
        Result<FileMetadata> result = await _fileService.UploadFileAsync(FormFile("logo.png", "text/html").Object, null, "user1", CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("image/png", (await FindFileFreshAsync(result.Data!.Id))!.ContentType);
        File.Delete(result.Data.FilePath);
    }

    [TestMethod]
    public async Task UploadAndCreateFolder_RefuseAnotherCompanysFolder()
    {
        Folder foreign = new() { Id = Guid.NewGuid(), Name = "Theirs", CompanyId = 2, CreatedAt = DateTime.UtcNow, IsActive = true };
        Folder own = new() { Id = Guid.NewGuid(), Name = "Ours", CompanyId = 1, CreatedAt = DateTime.UtcNow, IsActive = true };
        _dbContext.Folders.AddRange(foreign, own);
        await _dbContext.SaveChangesAsync();

        FileService companyOne = ServiceForCompany(1);

        Assert.IsFalse((await companyOne.UploadFileAsync(FormFile("menu.pdf", "application/pdf").Object, foreign.Id, "user1", CancellationToken.None)).Success);
        Assert.IsFalse((await companyOne.CreateFolderAsync("Inside theirs", foreign.Id, "user1", CancellationToken.None)).Success);

        Result<FileMetadata> intoOwn = await companyOne.UploadFileAsync(FormFile("menu.pdf", "application/pdf").Object, own.Id, "user1", CancellationToken.None);
        Assert.IsTrue(intoOwn.Success, intoOwn.Error);
        File.Delete(intoOwn.Data!.FilePath);
    }

    [TestMethod]
    public async Task DeleteAndMove_CannotReachAnotherCompanysFileOrFolder_AndTheBytesStay()
    {
        FileMetadata theirs = NewFile("theirs.pdf", null);
        theirs.CompanyId = 2;
        File.WriteAllBytes(theirs.FilePath, [1, 2, 3]);
        FileMetadata ours = NewFile("ours.pdf", null);
        ours.CompanyId = 1;
        Folder theirFolder = new() { Id = Guid.NewGuid(), Name = "Theirs", CompanyId = 2, CreatedAt = DateTime.UtcNow, IsActive = true };
        _dbContext.FileMetadata.AddRange(theirs, ours);
        _dbContext.Folders.Add(theirFolder);
        await _dbContext.SaveChangesAsync();

        FileService companyOne = ServiceForCompany(1);

        Assert.IsFalse((await companyOne.DeleteFileAsync(theirs.Id, CancellationToken.None)).Success);
        Assert.IsFalse((await companyOne.MoveFileAsync(theirs.Id, null, CancellationToken.None)).Success);
        Assert.IsFalse((await companyOne.MoveFileAsync(ours.Id, theirFolder.Id, CancellationToken.None)).Success, "Can't move a file into another company's folder");
        Assert.IsFalse((await companyOne.DeleteFolderAsync(theirFolder.Id, CancellationToken.None)).Success);

        Assert.IsTrue(File.Exists(theirs.FilePath), "The other company's bytes must not be touched");
        Assert.IsNotNull(await FindFileFreshAsync(theirs.Id));
        File.Delete(theirs.FilePath);
    }

    [TestMethod]
    public async Task DeleteFolderAsync_RefusesAFolderWithContents()
    {
        Folder folder = new() { Id = Guid.NewGuid(), Name = "Full", CreatedAt = DateTime.UtcNow, IsActive = true };
        Folder empty = new() { Id = Guid.NewGuid(), Name = "Child", ParentFolderId = folder.Id, CreatedAt = DateTime.UtcNow, IsActive = true };
        _dbContext.Folders.AddRange(folder, empty);
        _dbContext.FileMetadata.Add(NewFile("inside.pdf", folder.Id));
        await _dbContext.SaveChangesAsync();

        Assert.IsFalse((await _fileService.DeleteFolderAsync(folder.Id, CancellationToken.None)).Success);
        Assert.IsTrue(await _dbContext.Folders.AnyAsync(x => x.Id == folder.Id));
    }

    // Moving a file changes its FolderId, not where its bytes are stored, so they still sit under
    // the old folder's storage path. Deleting that folder used to wipe the path, file and all.
    [TestMethod]
    public async Task DeleteFolderAsync_LeavesTheBytesOfAFileMovedOutOfIt()
    {
        Result<Folder> folder = await _fileService.CreateFolderAsync("Temporary", null, "user1", CancellationToken.None);
        Assert.IsTrue(folder.Success, folder.Error);

        Result<FileMetadata> uploaded = await _fileService.UploadFileAsync(FormFile("poster.png", "image/png").Object, folder.Data!.Id, "user1", CancellationToken.None);
        Assert.IsTrue(uploaded.Success, uploaded.Error);
        Assert.IsTrue((await _fileService.MoveFileAsync(uploaded.Data!.Id, null, CancellationToken.None)).Success);

        Result<bool> deleted = await _fileService.DeleteFolderAsync(folder.Data.Id, CancellationToken.None);

        Assert.IsTrue(deleted.Success, deleted.Error);
        Assert.IsTrue(File.Exists(uploaded.Data.FilePath), "The moved file's bytes must survive deleting its old folder");

        Result<FileContent> opened = await _fileService.OpenFileContentAsync(uploaded.Data.Id, null, CancellationToken.None);
        Assert.IsTrue(opened.Success, opened.Error);
        await opened.Data!.DisposeAsync();
        await _fileService.DeleteFileAsync(uploaded.Data.Id, CancellationToken.None);
    }

    private static FileMetadata NewFile(string name, Guid? folderId) => new()
    {
        Id = Guid.NewGuid(),
        FileName = name,
        FilePath = Path.GetTempFileName(),
        FileSize = 5,
        ContentType = "application/octet-stream",
        UploadedAt = DateTime.UtcNow,
        UploadedBy = "user1",
        FolderId = folderId,
        IsActive = true
    };
}
