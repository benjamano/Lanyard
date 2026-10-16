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

namespace Lanyard.Tests.Services.AudioClips;

[TestClass]
public class AudioClipScheduleEngineTests
{
    // Friday 16 October 2026, 21:45 UK time (British Summer Time, so 20:45 UTC): 15 minutes
    // before a 22:00 close.
    private static readonly DateTime ClosingCallUtc = RotaTime.ToUtc(new DateOnly(2026, 10, 16), new TimeOnly(21, 45));

    private sealed class Setup
    {
        public required DbContextOptions<ApplicationDbContext> Options { get; init; }
        public required Mock<IAudioClipPlaybackService> Playback { get; init; }
        public required Guid ScheduleId { get; init; }
        public required Guid ClipId { get; init; }
        public required Guid LobbyId { get; init; }
        public required int CompanyId { get; init; }

        public AudioClipScheduleEngine NewEngine() =>
            new(SchedulingTestHelpers.GetFactory(Options), Playback.Object, NullLogger<AudioClipScheduleEngine>.Instance);
    }

    private static async Task<Setup> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Guid clipId = Guid.NewGuid();
        Guid scheduleId = Guid.NewGuid();
        Guid lobby = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            Location norwich = new() { CompanyId = company.Id, Name = "Norwich", IsActive = true };
            ctx.Locations.Add(norwich);
            await ctx.SaveChangesAsync();

            ctx.LocationOpeningHours.Add(new LocationOpeningHours { LocationId = ipswich.Id, DayOfWeek = DayOfWeek.Friday, OpensAt = new TimeOnly(10, 0), ClosesAt = new TimeOnly(22, 0), IsActive = true });
            ctx.AudioClips.Add(new AudioClip { Id = clipId, CompanyId = company.Id, Name = "Closing", DurationSeconds = 10, CreateDate = DateTime.UtcNow });
            ctx.Clients.AddRange(
                new Client { Id = lobby, CompanyId = company.Id, Name = "Lobby", LocationId = ipswich.Id },
                new Client { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Elsewhere", LocationId = norwich.Id });
            ctx.AudioClipSchedules.Add(new AudioClipSchedule
            {
                Id = scheduleId,
                CompanyId = company.Id,
                Name = "Closing call",
                AudioClipId = clipId,
                LocationId = ipswich.Id,
                Anchor = AudioClipScheduleAnchor.BeforeClosing,
                OffsetMinutes = 15,
                DaysOfWeek = "0,1,2,3,4,5,6",
                IsEnabled = true,
                CreateDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await ctx.SaveChangesAsync();
        }

        Mock<IAudioClipPlaybackService> playback = new();
        playback.Setup(p => p.PlayAsync(It.IsAny<AudioClipPlayRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<List<AudioClipKioskOutcome>>.Ok([]));

        return new Setup { Options = options, Playback = playback, ScheduleId = scheduleId, ClipId = clipId, LobbyId = lobby, CompanyId = company.Id };
    }

    private static void VerifyPlayed(Setup s, Times times) =>
        s.Playback.Verify(p => p.PlayAsync(It.Is<AudioClipPlayRequest>(r =>
            r.ClipId == s.ClipId && r.ClientIds.SequenceEqual(new[] { s.LobbyId }) && r.CompanyId == s.CompanyId
            && r.Source == AudioClipPlaySource.Schedule && r.ScheduleId == s.ScheduleId && r.TriggeredByUserId == null), It.IsAny<CancellationToken>()), times);

    [TestMethod]
    public async Task Tick_PlaysTheClipOnTheLocationsKiosks_WhenDue()
    {
        Setup s = await SeedAsync();

        await s.NewEngine().TickAsync(ClosingCallUtc.AddSeconds(10), CancellationToken.None);

        VerifyPlayed(s, Times.Once());
    }

    [TestMethod]
    public async Task Tick_BeforeItsTime_DoesNothing()
    {
        Setup s = await SeedAsync();

        await s.NewEngine().TickAsync(ClosingCallUtc.AddSeconds(-30), CancellationToken.None);

        VerifyPlayed(s, Times.Never());
    }

    [TestMethod]
    public async Task Tick_PlaysEachOccurrenceOnce_EvenAfterARestart()
    {
        Setup s = await SeedAsync();

        await s.NewEngine().TickAsync(ClosingCallUtc.AddSeconds(5), CancellationToken.None);
        await s.NewEngine().TickAsync(ClosingCallUtc.AddSeconds(35), CancellationToken.None);
        await s.NewEngine().TickAsync(ClosingCallUtc.AddSeconds(65), CancellationToken.None);

        VerifyPlayed(s, Times.Once());
    }

    [TestMethod]
    public async Task Tick_SkipsAPlayMissedByMoreThanTwoMinutes()
    {
        Setup s = await SeedAsync();

        await s.NewEngine().TickAsync(ClosingCallUtc.AddMinutes(3), CancellationToken.None);

        VerifyPlayed(s, Times.Never());
    }

    [TestMethod]
    public async Task Tick_LeavesASwitchedOffScheduleAlone()
    {
        Setup s = await SeedAsync();
        await using (ApplicationDbContext ctx = new(s.Options))
        {
            (await ctx.AudioClipSchedules.SingleAsync()).IsEnabled = false;
            await ctx.SaveChangesAsync();
        }

        await s.NewEngine().TickAsync(ClosingCallUtc.AddSeconds(10), CancellationToken.None);

        VerifyPlayed(s, Times.Never());
    }

    [TestMethod]
    public async Task Tick_LeavesAScheduleForADeletedClipAlone()
    {
        Setup s = await SeedAsync();
        await using (ApplicationDbContext ctx = new(s.Options))
        {
            (await ctx.AudioClips.SingleAsync()).DeleteDate = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await s.NewEngine().TickAsync(ClosingCallUtc.AddSeconds(10), CancellationToken.None);

        VerifyPlayed(s, Times.Never());
    }

    [TestMethod]
    public async Task Tick_RecordsThePlayBeforeSendingIt()
    {
        Setup s = await SeedAsync();
        string? keyWhenSent = null;
        s.Playback.Setup(p => p.PlayAsync(It.IsAny<AudioClipPlayRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                using ApplicationDbContext ctx = new(s.Options);
                keyWhenSent = ctx.AudioClipSchedules.AsNoTracking().Single().LastFiredOccurrenceKey;
            })
            .ReturnsAsync(Result<List<AudioClipKioskOutcome>>.Ok([]));

        await s.NewEngine().TickAsync(ClosingCallUtc.AddSeconds(10), CancellationToken.None);

        Assert.IsNotNull(keyWhenSent);
    }
}
