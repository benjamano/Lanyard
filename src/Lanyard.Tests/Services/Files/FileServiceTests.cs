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

    [TestInitialize]
    public void Setup()
    {
        _dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        _currentUserAccessorMock = new Mock<ICurrentUserAccessor>();
        _analysisQueueMock = new Mock<ISongAnalysisQueue>();
        _environmentMock = new Mock<IWebHostEnvironment>();
        _environmentMock.SetupGet(x => x.EnvironmentName).Returns(Environments.Development);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new ApplicationDbContext(options);
        _dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_dbContext);
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
        Assert.IsNull(await _dbContext.FileMetadata.FindAsync(fileId));
        Assert.IsFalse(File.Exists(fileMeta.FilePath));
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
}