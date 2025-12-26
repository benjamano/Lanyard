using Lanyard.Application.Services;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DataAccess;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using NAudio.Wave;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace LanyardTests;

[TestClass]
public sealed class MusicPlayerTests
{
    private Mock<IHubContext<MusicControlHub>> _mockHubContext = null!;
    private Mock<IDbContextFactory<ApplicationDbContext>> _mockContextFactory = null!;
    private MusicPlayerService _musicPlayerService = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockHubContext = new Mock<IHubContext<MusicControlHub>>();
        _mockContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        
        _musicPlayerService = new MusicPlayerService(
            _mockHubContext.Object,
            _mockContextFactory.Object);
    }

    [TestMethod]
    public async Task TestPlaySong_ShouldSetQueueAndUpdateState()
    {
        // Arrange
        var testSong = new Song
        {
            Id = Guid.NewGuid(),
            Name = "Test Song",
            AlbumName = "Test Album",
            FilePath = Path.Combine(Path.GetTempPath(), "test.mp3"),
            DurationSeconds = 180,
            CreateDate = DateTime.UtcNow,
            IsDownloaded = true,
            IsActive = true
        };

        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);

        try
        {
            // Act
            await _musicPlayerService.Play(testSong);

            // Assert
            Assert.IsNotNull(_musicPlayerService.CurrentSong, "Current song should be set");
            Assert.AreEqual(testSong.Id, _musicPlayerService.CurrentSong.Id, "Current song ID should match");

            var queue = _musicPlayerService.GetQueue();
            Assert.HasCount(1, queue, "Queue should contain one song");
            Assert.AreEqual(testSong.Id, queue[0].Id, "Queue should contain the test song");

            mockClientProxy.Verify(
                c => c.SendCoreAsync("Load", It.Is<object[]>(o => (Guid)o[0] == testSong.Id), default),
                Times.Once,
                "Should send Load command to clients");

            mockClientProxy.Verify(
                c => c.SendCoreAsync("Play", It.IsAny<object[]>(), default),
                Times.Once,
                "Should send Play command to clients");
        }
        finally
        {
            if (File.Exists(testSong.FilePath))
            {
                File.Delete(testSong.FilePath);
            }
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
    }
}