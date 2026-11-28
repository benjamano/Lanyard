using Lanyard.Application.Services;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.DTO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.SignalR;

// Security Issue 9.12: a kiosk's cached-song report used to reach every open Clients page in every
// company, with names looked up across all companies.
[TestClass]
public class CachedSongsEventTests
{
    [TestMethod]
    public async Task RaiseReceiveCachedSongs_LooksUpNamesInTheKiosksCompany_AndSaysWhichKioskReported()
    {
        Guid songId = Guid.NewGuid();
        Guid kioskId = Guid.NewGuid();

        Mock<IMusicService> music = new();
        music.Setup(m => m.GetSongNamesAsync(It.IsAny<IReadOnlyCollection<Guid>>(), 7))
            .ReturnsAsync(Result<IReadOnlyDictionary<Guid, string>>.Ok(new Dictionary<Guid, string> { [songId] = "Arena anthem" }));

        ServiceCollection services = new();
        services.AddSingleton(music.Object);
        SignalRProjectionControlHubEvents events = new(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        Guid? reportedBy = null;
        string? reportedName = null;
        events.OnReceiveCachedSongs += (clientId, result) =>
        {
            reportedBy = clientId;
            reportedName = result.Data!.Single().Name;
        };

        await events.RaiseReceiveCachedSongs(kioskId, 7, Result<IEnumerable<CachedSongDTO>>.Ok([new CachedSongDTO { Id = songId }]));

        music.Verify(m => m.GetSongNamesAsync(It.IsAny<IReadOnlyCollection<Guid>>(), 7), Times.Once);
        Assert.AreEqual(kioskId, reportedBy);
        Assert.AreEqual("Arena anthem", reportedName);
    }
}
