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

namespace Lanyard.Tests.Services.Files;


[TestClass]
public class FileServiceTests
{
    private Mock<IDbContextFactory<ApplicationDbContext>> _dbFactoryMock = null!;
    private Mock<ISecurityService> _securityServiceMock = null!;
    private FileService _fileService = null!;
    private ApplicationDbContext _dbContext = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        _securityServiceMock = new Mock<ISecurityService>();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new ApplicationDbContext(options);
        _dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_dbContext);
        _fileService = new FileService(_dbFactoryMock.Object, _securityServiceMock.Object);
    }

    [TestMethod]
    public async Task WhenUploadingValidFileThenFileIsSaved()
    {
        var fileMock = new Mock<IFormFile>();
        var content = new MemoryStream(new byte[] { 1, 2, 3 });
        fileMock.Setup(f => f.FileName).Returns("test.txt");
        fileMock.Setup(f => f.Length).Returns(content.Length);
        fileMock.Setup(f => f.ContentType).Returns("text/plain");
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((Stream s, CancellationToken ct) => content.CopyToAsync(s, ct));

        _securityServiceMock.Setup(s => s.GetCurrentUserIdAsync())
            .ReturnsAsync("user1");

        var result = await _fileService.UploadFileAsync(fileMock.Object, null, CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.Data);
        Assert.AreEqual("test.txt", result.Data.FileName);
        Assert.AreEqual("user1", result.Data.UploadedBy);
    }

    [TestMethod]
    public async Task WhenDeletingFileThenFileIsRemoved()
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
    public async Task WhenRenamingFileThenNameIsUpdated()
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
}