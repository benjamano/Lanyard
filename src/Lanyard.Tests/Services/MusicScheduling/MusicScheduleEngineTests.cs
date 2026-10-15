using Lanyard.Application.Services;
using Lanyard.Application.Services.MusicScheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using NAudio.Wave;

namespace Lanyard.Tests.Services.MusicScheduling;

[TestClass]
public class MusicScheduleEngineTests
{
    // Monday 5 January 2026: winter, so UK time is UTC and the times read the same either way.
    private static readonly DateTime MondayMorning = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);

    private const string ConnectionId = "kiosk-connection";

    // SignalRControlHub's connection set is process-static; the engine exposes a seam instead.
    private sealed class TestEngine(ISystemDbContextFactory factory, IMusicPlayerService player, TimeProvider clock)
        : MusicScheduleEngine(factory, player, clock, NullLogger<MusicScheduleEngine>.Instance)
    {
        public HashSet<string> Connected { get; } = [ConnectionId];

        protected override bool IsConnected(string? connectionId) => connectionId is not null && Connected.Contains(connectionId);
    }

    private sealed class Setup
    {
        public required DbContextOptions<ApplicationDbContext> Options { get; init; }
        public required Location Location { get; init; }
        public required Guid KioskId { get; init; }
        public required Guid MorningPlaylistId { get; init; }
        public required Guid DaytimePlaylistId { get; init; }
        public required Mock<IMusicPlayerService> Player { get; init; }
        public required TestEngine Engine { get; init; }
        public required TestClock Clock { get; init; }
    }

    // A location with one connected kiosk and an enabled schedule:
    //   Morning  every day 08:00-10:00, "Morning" playlist at 30%
    //   Daytime  every day 10:00-17:00, "Daytime" playlist, kiosk volume
    //   Late     Fri 22:00-02:00, music off
    private static async Task<Setup> SeedAsync(PlaybackState kioskState = PlaybackState.Stopped, Guid? kioskPlaylistId = null, int kioskVolume = 50)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Guid kioskId = Guid.NewGuid();
        Guid morning = Guid.NewGuid();
        Guid daytime = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Clients.Add(new Client { Id = kioskId, CompanyId = company.Id, Name = "Lobby", LocationId = location.Id, MostRecentConnectionId = ConnectionId });
            ctx.Playlists.AddRange(
                new Playlist { Id = morning, CompanyId = company.Id, Name = "Morning", CreateDate = DateTime.UtcNow },
                new Playlist { Id = daytime, CompanyId = company.Id, Name = "Daytime", CreateDate = DateTime.UtcNow });

            ctx.MusicSchedules.Add(new MusicSchedule
            {
                Id = Guid.NewGuid(),
                LocationId = location.Id,
                IsEnabled = true,
                Dayparts =
                [
                    Daypart("Morning", "08:00", "10:00", morning, 30, "0,1,2,3,4,5,6"),
                    Daypart("Daytime", "10:00", "17:00", daytime, null, "0,1,2,3,4,5,6"),
                    Daypart("Late", "22:00", "02:00", null, null, "5")
                ]
            });

            await ctx.SaveChangesAsync();
        }

        Mock<IMusicPlayerService> player = new();
        player.Setup(x => x.GetCurrentPlaybackState(kioskId)).Returns(kioskState);
        player.Setup(x => x.GetCurrentPlaylist(kioskId)).Returns(kioskPlaylistId is Guid id ? new Playlist { Id = id, Name = "Current" } : null);
        player.Setup(x => x.GetCurrentVolume(kioskId)).Returns(kioskVolume);
        player.Setup(x => x.Play(kioskId, It.IsAny<Guid>(), It.IsAny<MusicCommandSource>())).ReturnsAsync(Result<bool>.Ok(true));

        TestClock clock = new(MondayMorning);
        TestEngine engine = new(SchedulingTestHelpers.GetFactory(options), player.Object, clock) { RampStepDelay = TimeSpan.Zero };

        return new Setup
        {
            Options = options,
            Location = location,
            KioskId = kioskId,
            MorningPlaylistId = morning,
            DaytimePlaylistId = daytime,
            Player = player,
            Engine = engine,
            Clock = clock
        };
    }

    private static MusicDaypart Daypart(string name, string start, string end, Guid? playlistId, int? volume, string days) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DaysOfWeek = days,
        StartTime = TimeOnly.Parse(start),
        EndTime = TimeOnly.Parse(end),
        PlaylistId = playlistId,
        Volume = volume,
        IsActive = true,
        UpdateDate = DateTime.UtcNow
    };

    [TestMethod]
    public async Task Tick_StartsTheCurrentPlaylistOnAStoppedKiosk()
    {
        Setup s = await SeedAsync();

        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);

        s.Player.Verify(x => x.Play(s.KioskId, s.MorningPlaylistId, It.IsAny<MusicCommandSource>()), Times.Once);
    }

    [TestMethod]
    public async Task Tick_RampsTheVolumeInStepsToTheDaypartsVolume()
    {
        Setup s = await SeedAsync(kioskVolume: 50);
        List<int> volumes = [];
        s.Player.Setup(x => x.SetVolume(s.KioskId, It.IsAny<int>(), MusicCommandSource.Schedule)).Callback<Guid, int, MusicCommandSource>((_, v, _) => volumes.Add(v)).Returns(Task.CompletedTask);

        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { 46, 42, 38, 34, 30 }, volumes);
    }

    [TestMethod]
    public async Task Tick_DoesNotApplyTheSamePartOfTheDayTwice()
    {
        Setup s = await SeedAsync();

        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);
        await s.Engine.TickAsync(MondayMorning.AddMinutes(30), CancellationToken.None);

        s.Player.Verify(x => x.Play(s.KioskId, It.IsAny<Guid>(), It.IsAny<MusicCommandSource>()), Times.Once);
    }

    [TestMethod]
    public async Task Tick_AppliesTheNextPartOfTheDayWhenItStarts()
    {
        Setup s = await SeedAsync();

        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);
        await s.Engine.TickAsync(MondayMorning.AddHours(1).AddSeconds(10), CancellationToken.None);

        s.Player.Verify(x => x.Play(s.KioskId, s.DaytimePlaylistId, It.IsAny<MusicCommandSource>()), Times.Once);
    }

    [TestMethod]
    public async Task Tick_WaitsForTheSongToEndWhenTheKioskIsPlayingSomethingElse()
    {
        Setup s = await SeedAsync(PlaybackState.Playing, kioskPlaylistId: Guid.NewGuid());

        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);

        s.Player.Verify(x => x.SetPendingPlaylist(s.KioskId, s.MorningPlaylistId), Times.Once);
        s.Player.Verify(x => x.Play(s.KioskId, It.IsAny<Guid>(), It.IsAny<MusicCommandSource>()), Times.Never, "A playlist change mustn't cut off the song.");
    }

    [TestMethod]
    public async Task Tick_SwitchesAnywayWhenTheSongIsStillPlayingAfterTheLimit()
    {
        Setup s = await SeedAsync(PlaybackState.Playing, kioskPlaylistId: Guid.NewGuid());
        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);

        // Still queued: the song hasn't ended.
        s.Player.Setup(x => x.GetPendingPlaylist(s.KioskId)).Returns((s.MorningPlaylistId, MondayMorning));

        await s.Engine.TickAsync(MondayMorning + MusicScheduleEngine.SwitchAfterAtMost - TimeSpan.FromSeconds(30), CancellationToken.None);
        s.Player.Verify(x => x.Play(s.KioskId, It.IsAny<Guid>(), It.IsAny<MusicCommandSource>()), Times.Never);

        await s.Engine.TickAsync(MondayMorning + MusicScheduleEngine.SwitchAfterAtMost, CancellationToken.None);
        s.Player.Verify(x => x.Play(s.KioskId, s.MorningPlaylistId, It.IsAny<MusicCommandSource>()), Times.Once);
    }

    [TestMethod]
    public async Task Tick_LeavesAKioskAloneThatIsAlreadyPlayingThePlaylist()
    {
        // e.g. the server restarted half way through the morning.
        Setup s = await SeedAsync(PlaybackState.Playing, kioskPlaylistId: null);
        s.Player.Setup(x => x.GetCurrentPlaylist(s.KioskId)).Returns(new Playlist { Id = s.MorningPlaylistId, Name = "Morning" });

        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);

        s.Player.Verify(x => x.Play(s.KioskId, It.IsAny<Guid>(), It.IsAny<MusicCommandSource>()), Times.Never);
        s.Player.Verify(x => x.SetPendingPlaylist(s.KioskId, It.Is<Guid?>(g => g != null)), Times.Never);
    }

    [TestMethod]
    public async Task Tick_PausesAPlayingKioskForMusicOff()
    {
        Setup s = await SeedAsync(PlaybackState.Playing, kioskPlaylistId: Guid.NewGuid());

        // Friday 9 January 23:00: inside "Late", which is music off.
        await s.Engine.TickAsync(new DateTime(2026, 1, 9, 23, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        s.Player.Verify(x => x.Pause(s.KioskId, It.IsAny<MusicCommandSource>()), Times.Once);
    }

    [TestMethod]
    public async Task Tick_KeepsMusicOffAfterMidnightForADaypartThatStartedTheDayBefore()
    {
        Setup s = await SeedAsync(PlaybackState.Playing, kioskPlaylistId: Guid.NewGuid());

        // Saturday 01:00 is still Friday's "Late".
        await s.Engine.TickAsync(new DateTime(2026, 1, 10, 1, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        s.Player.Verify(x => x.Pause(s.KioskId, It.IsAny<MusicCommandSource>()), Times.Once);
    }

    [TestMethod]
    public async Task Tick_LeavesTheMusicAloneInAGap()
    {
        Setup s = await SeedAsync(PlaybackState.Playing, kioskPlaylistId: Guid.NewGuid());

        // Monday 18:00: nothing scheduled.
        await s.Engine.TickAsync(new DateTime(2026, 1, 5, 18, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        s.Player.Verify(x => x.Play(s.KioskId, It.IsAny<Guid>(), It.IsAny<MusicCommandSource>()), Times.Never);
        s.Player.Verify(x => x.Pause(s.KioskId, It.IsAny<MusicCommandSource>()), Times.Never);
        s.Player.Verify(x => x.SetVolume(s.KioskId, It.IsAny<int>(), It.IsAny<MusicCommandSource>()), Times.Never);
    }

    [TestMethod]
    public async Task Tick_SkipsADisconnectedKioskAndCatchesItUpWhenItReconnects()
    {
        Setup s = await SeedAsync();
        s.Engine.Connected.Clear();

        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);
        s.Player.Verify(x => x.Play(s.KioskId, It.IsAny<Guid>(), It.IsAny<MusicCommandSource>()), Times.Never);

        s.Engine.Connected.Add(ConnectionId);
        await s.Engine.TickAsync(MondayMorning.AddMinutes(1), CancellationToken.None);

        s.Player.Verify(x => x.Play(s.KioskId, s.MorningPlaylistId, It.IsAny<MusicCommandSource>()), Times.Once);
    }

    [TestMethod]
    public async Task Tick_ReappliesAfterAKioskDropsOffAndComesBack()
    {
        Setup s = await SeedAsync();

        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);
        s.Engine.Connected.Clear();
        await s.Engine.TickAsync(MondayMorning.AddMinutes(1), CancellationToken.None);
        s.Engine.Connected.Add(ConnectionId);
        await s.Engine.TickAsync(MondayMorning.AddMinutes(2), CancellationToken.None);

        s.Player.Verify(x => x.Play(s.KioskId, s.MorningPlaylistId, It.IsAny<MusicCommandSource>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task Tick_DoesNothingWhileTheScheduleIsOff()
    {
        Setup s = await SeedAsync();

        await using (ApplicationDbContext ctx = new(s.Options))
        {
            (await ctx.MusicSchedules.SingleAsync()).IsEnabled = false;
            await ctx.SaveChangesAsync();
        }

        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);

        s.Player.Verify(x => x.Play(s.KioskId, It.IsAny<Guid>(), It.IsAny<MusicCommandSource>()), Times.Never);
    }

    [TestMethod]
    public async Task Tick_IgnoresKiosksAtOtherLocations()
    {
        Setup s = await SeedAsync();

        await using (ApplicationDbContext ctx = new(s.Options))
        {
            Client kiosk = await ctx.Clients.SingleAsync();
            kiosk.LocationId = null;
            await ctx.SaveChangesAsync();
        }

        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);

        s.Player.Verify(x => x.Play(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<MusicCommandSource>()), Times.Never);
    }

    [TestMethod]
    public async Task Tick_AppliesAnEditToTheRunningPartOfTheDayStraightAway()
    {
        Setup s = await SeedAsync();
        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);

        await using (ApplicationDbContext ctx = new(s.Options))
        {
            MusicDaypart morning = await ctx.MusicDayparts.SingleAsync(x => x.Name == "Morning");
            morning.PlaylistId = s.DaytimePlaylistId;
            morning.UpdateDate = DateTime.UtcNow.AddMinutes(1);
            await ctx.SaveChangesAsync();
        }

        await s.Engine.TickAsync(MondayMorning.AddMinutes(1), CancellationToken.None);

        s.Player.Verify(x => x.Play(s.KioskId, s.DaytimePlaylistId, It.IsAny<MusicCommandSource>()), Times.Once);
    }

    [TestMethod]
    public async Task Tick_SkipsADeletedPlaylist()
    {
        Setup s = await SeedAsync();

        await using (ApplicationDbContext ctx = new(s.Options))
        {
            (await ctx.Playlists.SingleAsync(x => x.Id == s.MorningPlaylistId)).DeleteDate = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);

        s.Player.Verify(x => x.Play(s.KioskId, It.IsAny<Guid>(), It.IsAny<MusicCommandSource>()), Times.Never);
    }

    [TestMethod]
    public async Task Tick_CancelsAQueuedSwitchWhenTheScheduleIsTurnedOff()
    {
        Setup s = await SeedAsync(PlaybackState.Playing, kioskPlaylistId: Guid.NewGuid());
        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);

        await using (ApplicationDbContext ctx = new(s.Options))
        {
            (await ctx.MusicSchedules.SingleAsync()).IsEnabled = false;
            await ctx.SaveChangesAsync();
        }

        await s.Engine.TickAsync(MondayMorning.AddMinutes(1), CancellationToken.None);

        s.Player.Verify(x => x.SetPendingPlaylist(s.KioskId, null), Times.AtLeast(2));
    }

    private static async Task<DateTime?> OverrideUntilAsync(Setup s)
    {
        await using ApplicationDbContext ctx = new(s.Options);
        return (await ctx.Clients.SingleAsync()).MusicScheduleOverrideUntilUtc;
    }

    [TestMethod]
    public async Task RecordManualChange_HoldsTheScheduleOffUntilTheNextPartOfTheDay()
    {
        Setup s = await SeedAsync();
        s.Clock.UtcNow = MondayMorning.AddMinutes(10);

        Result<bool> result = await s.Engine.RecordManualChangeAsync(s.KioskId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc), await OverrideUntilAsync(s));
    }

    [TestMethod]
    public async Task ManualChange_SurvivesARestartAndEndsAtTheNextPartOfTheDay()
    {
        Setup s = await SeedAsync();
        s.Clock.UtcNow = MondayMorning;
        await s.Engine.RecordManualChangeAsync(s.KioskId);

        // A fresh engine knows nothing of what it applied before, just like after a restart.
        TestEngine restarted = new(SchedulingTestHelpers.GetFactory(s.Options), s.Player.Object, s.Clock) { RampStepDelay = TimeSpan.Zero };

        await restarted.TickAsync(MondayMorning.AddMinutes(30), CancellationToken.None);
        s.Player.Verify(x => x.Play(s.KioskId, It.IsAny<Guid>(), It.IsAny<MusicCommandSource>()), Times.Never, "The change by hand must stand.");

        await restarted.TickAsync(new DateTime(2026, 1, 5, 10, 0, 10, DateTimeKind.Utc), CancellationToken.None);
        s.Player.Verify(x => x.Play(s.KioskId, s.DaytimePlaylistId, MusicCommandSource.Schedule), Times.Once);
    }

    [TestMethod]
    public async Task ManualChange_IsNotRecordedWhenTheScheduleIsOff()
    {
        Setup s = await SeedAsync();

        await using (ApplicationDbContext ctx = new(s.Options))
        {
            (await ctx.MusicSchedules.SingleAsync()).IsEnabled = false;
            await ctx.SaveChangesAsync();
        }

        await s.Engine.RecordManualChangeAsync(s.KioskId);

        Assert.IsNull(await OverrideUntilAsync(s));
    }

    [TestMethod]
    public async Task ManualChange_FromThePlayerIsRecorded()
    {
        Setup s = await SeedAsync();

        s.Player.Raise(x => x.OnManualMusicChange += null, s.KioskId);

        // The player's event is synchronous, so the engine writes the hold in the background.
        for (int i = 0; i < 50 && await OverrideUntilAsync(s) is null; i++)
        {
            await Task.Delay(20);
        }

        Assert.IsNotNull(await OverrideUntilAsync(s));
    }

    [TestMethod]
    public async Task ManualChange_CancelsASwitchTheScheduleQueued()
    {
        Setup s = await SeedAsync(PlaybackState.Playing, kioskPlaylistId: Guid.NewGuid());
        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);
        s.Player.Invocations.Clear();

        await s.Engine.RecordManualChangeAsync(s.KioskId);

        s.Player.Verify(x => x.SetPendingPlaylist(s.KioskId, null), Times.Once);
    }

    [TestMethod]
    public async Task ResumeSchedule_ClearsTheHoldAndAppliesTheCurrentPartOfTheDayStraightAway()
    {
        Setup s = await SeedAsync();
        s.Clock.UtcNow = MondayMorning;
        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);
        await s.Engine.RecordManualChangeAsync(s.KioskId);

        s.Clock.UtcNow = MondayMorning.AddMinutes(20);
        Result<bool> result = await s.Engine.ResumeScheduleAsync(s.KioskId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsNull(await OverrideUntilAsync(s));
        s.Player.Verify(x => x.Play(s.KioskId, s.MorningPlaylistId, MusicCommandSource.Schedule), Times.Exactly(2));
    }

    [TestMethod]
    public async Task ScheduleCommands_AreNotTakenForChangesByHand()
    {
        Setup s = await SeedAsync();

        await s.Engine.TickAsync(MondayMorning, CancellationToken.None);

        s.Player.Verify(x => x.Play(s.KioskId, It.IsAny<Guid>(), MusicCommandSource.Manual), Times.Never);
        s.Player.Verify(x => x.SetVolume(s.KioskId, It.IsAny<int>(), MusicCommandSource.Manual), Times.Never);
    }

    [TestMethod]
    public async Task GetStatus_ShowsThePartOfTheDayAndWhenItChanges()
    {
        Setup s = await SeedAsync();
        s.Clock.UtcNow = MondayMorning;

        MusicScheduleKioskStatus? status = (await s.Engine.GetStatusAsync(s.KioskId)).Data;

        Assert.IsNotNull(status);
        Assert.AreEqual("Morning", status.DaypartName);
        Assert.IsFalse(status.IsMusicOff);
        Assert.AreEqual(new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc), status.NextChangeUtc);
        Assert.IsFalse(status.IsOverridden);
    }

    [TestMethod]
    public async Task GetStatus_ShowsAChangeByHand()
    {
        Setup s = await SeedAsync();
        s.Clock.UtcNow = MondayMorning;
        await s.Engine.RecordManualChangeAsync(s.KioskId);

        MusicScheduleKioskStatus? status = (await s.Engine.GetStatusAsync(s.KioskId)).Data;

        Assert.IsTrue(status?.IsOverridden);
        Assert.AreEqual(new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc), status?.OverrideUntilUtc);
    }

    [TestMethod]
    public async Task GetStatus_IsNullForAKioskWithoutASchedule()
    {
        Setup s = await SeedAsync();

        await using (ApplicationDbContext ctx = new(s.Options))
        {
            (await ctx.Clients.SingleAsync()).LocationId = null;
            await ctx.SaveChangesAsync();
        }

        Result<MusicScheduleKioskStatus?> status = await s.Engine.GetStatusAsync(s.KioskId);

        Assert.IsTrue(status.IsSuccess, status.Error);
        Assert.IsNull(status.Data);
    }

    [TestMethod]
    public async Task GetStatus_ReportsTheNextChangeInUtcDuringSummerTime()
    {
        Setup s = await SeedAsync();
        // Monday 6 July 2026, 09:00 UK (BST) = 08:00 UTC: "Morning" ends at 10:00 UK, 09:00 UTC.
        s.Clock.UtcNow = new DateTime(2026, 7, 6, 8, 0, 0, DateTimeKind.Utc);

        MusicScheduleKioskStatus? status = (await s.Engine.GetStatusAsync(s.KioskId)).Data;

        Assert.AreEqual("Morning", status?.DaypartName);
        Assert.AreEqual(new DateTime(2026, 7, 6, 9, 0, 0, DateTimeKind.Utc), status?.NextChangeUtc);
    }
}
