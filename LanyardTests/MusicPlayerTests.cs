using LanyardAPI.Services;
using LanyardData.Models;
using LanyardData.DataAccess;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using NAudio.Wave;
using System.Threading;

namespace LanyardTests
{
    [TestClass]
    public sealed class MusicPlayerTests
    {
        private MusicPlayer _musicPlayer;
        private Mock<MusicRepository> _mockRepository;
        private MusicPlayerService _musicPlayerService;

        [TestInitialize]
        public void Setup()
        {
            _musicPlayer = new MusicPlayer();
            _mockRepository = new Mock<MusicRepository>(MockBehavior.Loose, null!);
            _musicPlayerService = new MusicPlayerService(_musicPlayer, _mockRepository.Object);
        }

        [TestMethod]
        public async Task TestPlaySong_ShouldSetQueueAndLoadSong()
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

            CreateTestAudioFile(testSong.FilePath);

            try
            {
                // Act
                await _musicPlayerService.Play(testSong);

                // Assert
                Assert.IsNotNull(_musicPlayerService.CurrentSong, "Current song should be set");
                Assert.AreEqual(testSong.Id, _musicPlayerService.CurrentSong.Id, "Current song ID should match");
                Assert.AreEqual(PlaybackState.Playing, _musicPlayerService.CurrentPlaybackState, "Playback state should be Playing");

                var queue = _musicPlayerService.GetQueue();
                Assert.HasCount(1, queue, "Queue should contain one song");
                Assert.AreEqual(testSong.Id, queue[0].Id, "Queue should contain the test song");
            }
            finally
            {
                // Cleanup
                _musicPlayer.Stop();
                _musicPlayer.Dispose();
                if (File.Exists(testSong.FilePath))
                {
                    File.Delete(testSong.FilePath);
                }
            }
        }

        private void CreateTestAudioFile(string filePath)
        {
            using var writer = new WaveFileWriter(filePath, new WaveFormat(44100, 1));
            var silence = new byte[44100];
            writer.Write(silence, 0, silence.Length);
        }

        [TestCleanup]
        public void Cleanup()
        {
            _musicPlayer?.Dispose();
        }
    }
}