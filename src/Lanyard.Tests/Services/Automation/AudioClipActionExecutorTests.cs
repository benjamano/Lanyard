using System.Text.Json;
using Lanyard.Application.Services;
using Lanyard.Application.Services.AudioClips;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation;

[TestClass]
public class AudioClipActionExecutorTests
{
    private static async Task<(AudioClipActionExecutor Executor, Mock<IAudioClipPlaybackService> Playback, Guid KioskId, int CompanyId)> SetupAsync(
        AudioClipPlayStatus outcome = AudioClipPlayStatus.Sent)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Guid kioskId = Guid.NewGuid();
        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Clients.Add(new Client { Id = kioskId, CompanyId = company.Id, Name = "Arena", LocationId = location.Id });
            await ctx.SaveChangesAsync();
        }

        Mock<IAudioClipPlaybackService> playback = new();
        playback.Setup(p => p.PlayAsync(It.IsAny<AudioClipPlayRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<List<AudioClipKioskOutcome>>.Ok([new AudioClipKioskOutcome(kioskId, "Arena", outcome, outcome == AudioClipPlayStatus.Sent ? null : "Kiosk isn't connected.", Guid.NewGuid())]));

        AudioClipActionExecutor executor = new(playback.Object, SchedulingTestHelpers.GetFactory(options), NullLogger<AudioClipActionExecutor>.Instance);

        return (executor, playback, kioskId, company.Id);
    }

    private static AutomationRuleAction Action(Guid kioskId, Guid clipId) => new()
    {
        Id = Guid.NewGuid(),
        ActionType = AutomationActionTypes.PlayAudioClip,
        ParametersJson = JsonSerializer.Serialize(new AudioClipActionExecutor.AudioClipActionParameters { TargetClientId = kioskId, AudioClipId = clipId })
    };

    [TestMethod]
    public async Task CanHandle_OnlyPlayAudioClip()
    {
        (AudioClipActionExecutor executor, _, _, _) = await SetupAsync();

        Assert.IsTrue(executor.CanHandle(AutomationActionTypes.PlayAudioClip));
        Assert.IsFalse(executor.CanHandle(AutomationActionTypes.MusicControl));
    }

    [TestMethod]
    public async Task Execute_PlaysTheClipOnTheKiosk_AsAnAutomation()
    {
        (AudioClipActionExecutor executor, Mock<IAudioClipPlaybackService> playback, Guid kioskId, int companyId) = await SetupAsync();
        Guid clipId = Guid.NewGuid();

        (bool success, string? error) = await executor.ExecuteAsync(Action(kioskId, clipId), Guid.NewGuid());

        Assert.IsTrue(success, error);
        playback.Verify(p => p.PlayAsync(It.Is<AudioClipPlayRequest>(r =>
            r.ClipId == clipId && r.ClientIds.SequenceEqual(new[] { kioskId }) && r.CompanyId == companyId
            && r.Source == AudioClipPlaySource.Automation && r.TriggeredByUserId == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Execute_ReportsWhyTheKioskDidntGetIt()
    {
        (AudioClipActionExecutor executor, _, Guid kioskId, _) = await SetupAsync(AudioClipPlayStatus.Offline);

        (bool success, string? error) = await executor.ExecuteAsync(Action(kioskId, Guid.NewGuid()), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Kiosk isn't connected.", error);
    }

    [TestMethod]
    public async Task Execute_FailsWithoutAClipOrKiosk()
    {
        (AudioClipActionExecutor executor, Mock<IAudioClipPlaybackService> playback, Guid kioskId, _) = await SetupAsync();

        Assert.IsFalse((await executor.ExecuteAsync(Action(kioskId, Guid.Empty), Guid.NewGuid())).Success);
        Assert.IsFalse((await executor.ExecuteAsync(Action(Guid.Empty, Guid.NewGuid()), Guid.NewGuid())).Success);
        Assert.IsFalse((await executor.ExecuteAsync(Action(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid())).Success);
        playback.Verify(p => p.PlayAsync(It.IsAny<AudioClipPlayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
