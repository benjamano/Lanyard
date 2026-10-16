using Lanyard.Application.Services.AudioClips;
using Lanyard.Application.Services.Locations;
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
public class AudioClipScheduleServiceTests
{
    private const string UserId = "user-1";

    private sealed class Setup
    {
        public required DbContextOptions<ApplicationDbContext> Options { get; init; }
        public required AudioClipScheduleService Service { get; init; }
        public required Mock<IAudioClipPlaybackService> Playback { get; init; }
        public required Company Company { get; init; }
        public required Location Ipswich { get; init; }
        public required Location Norwich { get; init; }
        public required Guid EverywhereClipId { get; init; }
        public required Guid NorwichClipId { get; init; }

        public LocationScope Admin => new(true, Ipswich.Id, Company.Id, Ipswich.Name);
        public LocationScope IpswichManager => new(false, Ipswich.Id, Company.Id, Ipswich.Name, IsManager: true);
        public LocationScope IpswichStaff => new(false, Ipswich.Id, Company.Id, Ipswich.Name, IsManager: false);
    }

    private static async Task<Setup> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Location norwich = new() { CompanyId = company.Id, Name = "Norwich", IsActive = true };
        Guid everywhere = Guid.NewGuid();
        Guid norwichOnly = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Locations.Add(norwich);
            await ctx.SaveChangesAsync();

            ctx.AudioClips.AddRange(
                new AudioClip { Id = everywhere, CompanyId = company.Id, Name = "Closing", CreateDate = DateTime.UtcNow },
                new AudioClip { Id = norwichOnly, CompanyId = company.Id, Name = "Norwich jingle", LocationId = norwich.Id, CreateDate = DateTime.UtcNow });
            ctx.LocationOpeningHours.Add(new LocationOpeningHours { LocationId = ipswich.Id, DayOfWeek = DayOfWeek.Friday, OpensAt = new TimeOnly(10, 0), ClosesAt = new TimeOnly(22, 0), IsActive = true });
            await ctx.SaveChangesAsync();
        }

        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<IAudioClipPlaybackService> playback = new();

        // Friday 16 October 2026, 12:00 UK time.
        TestClock clock = new(RotaTime.ToUtc(new DateOnly(2026, 10, 16), new TimeOnly(12, 0)));

        return new Setup
        {
            Options = options,
            Service = new AudioClipScheduleService(factory.Object, playback.Object, clock, NullLogger<AudioClipScheduleService>.Instance),
            Playback = playback,
            Company = company,
            Ipswich = ipswich,
            Norwich = norwich,
            EverywhereClipId = everywhere,
            NorwichClipId = norwichOnly
        };
    }

    private static AudioClipSchedule ClosingCall(Setup s, int? locationId = null, Guid? clipId = null) => new()
    {
        Name = "Closing call",
        AudioClipId = clipId ?? s.EverywhereClipId,
        LocationId = locationId ?? s.Ipswich.Id,
        Anchor = AudioClipScheduleAnchor.BeforeClosing,
        OffsetMinutes = 15,
        DaysOfWeek = "0,1,2,3,4,5,6"
    };

    [TestMethod]
    public async Task Save_AddsTheSchedule_AndSendsItsClipToTheKiosksAhead()
    {
        Setup s = await SeedAsync();

        Result<AudioClipSchedule> result = await s.Service.SaveAsync(s.IpswichManager, ClosingCall(s), UserId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(result.Data!.IsEnabled);
        Assert.AreEqual(UserId, result.Data.CreatedByUserId);
        s.Playback.Verify(p => p.PrefetchScheduledClipsAsync(s.Ipswich.Id), Times.Once);
    }

    [TestMethod]
    public async Task Save_ForAnotherLocation_IsRefusedForAManager()
    {
        Setup s = await SeedAsync();

        Result<AudioClipSchedule> result = await s.Service.SaveAsync(s.IpswichManager, ClosingCall(s, s.Norwich.Id), UserId);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Save_ByStaff_IsRefused()
    {
        Setup s = await SeedAsync();

        Assert.IsFalse((await s.Service.SaveAsync(s.IpswichStaff, ClosingCall(s), UserId)).IsSuccess);
    }

    [TestMethod]
    public async Task Save_WithAClipLimitedToAnotherLocation_IsRefused()
    {
        Setup s = await SeedAsync();

        Result<AudioClipSchedule> result = await s.Service.SaveAsync(s.Admin, ClosingCall(s, s.Ipswich.Id, s.NorwichClipId), UserId);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "another location");
    }

    [TestMethod]
    public async Task Save_AnIncompleteSchedule_IsRefused()
    {
        Setup s = await SeedAsync();
        AudioClipSchedule schedule = ClosingCall(s);
        schedule.DaysOfWeek = string.Empty;

        Assert.IsFalse((await s.Service.SaveAsync(s.Admin, schedule, UserId)).IsSuccess);
    }

    [TestMethod]
    public async Task Save_AnEdit_UpdatesItAndDropsTheTimeForAnOpeningHoursSchedule()
    {
        Setup s = await SeedAsync();
        AudioClipSchedule added = (await s.Service.SaveAsync(s.Admin, ClosingCall(s), UserId)).Data!;

        AudioClipSchedule edit = ClosingCall(s);
        edit.Id = added.Id;
        edit.Name = "Last orders";
        edit.OffsetMinutes = 30;
        edit.TimeOfDay = new TimeOnly(9, 0);

        Result<AudioClipSchedule> result = await s.Service.SaveAsync(s.Admin, edit, UserId);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(s.Options);
        AudioClipSchedule saved = await ctx.AudioClipSchedules.SingleAsync();
        Assert.AreEqual("Last orders", saved.Name);
        Assert.AreEqual(30, saved.OffsetMinutes);
        Assert.IsNull(saved.TimeOfDay);
        Assert.IsNotNull(saved.UpdateDate);
    }

    [TestMethod]
    public async Task List_ShowsTheNextPlays_AndWhetherTheLocationHasHours()
    {
        Setup s = await SeedAsync();
        await s.Service.SaveAsync(s.Admin, ClosingCall(s), UserId);
        await s.Service.SaveAsync(s.Admin, ClosingCall(s, s.Norwich.Id), UserId);

        List<AudioClipScheduleSummary> all = (await s.Service.ListAsync(s.Admin)).Data!;
        List<AudioClipScheduleSummary> mine = (await s.Service.ListAsync(s.IpswichManager)).Data!;

        Assert.AreEqual(2, all.Count);
        Assert.AreEqual(1, mine.Count);

        AudioClipScheduleSummary ipswich = all.Single(x => x.Schedule.LocationId == s.Ipswich.Id);
        Assert.IsTrue(ipswich.LocationHasHours);
        Assert.AreEqual(new DateTime(2026, 10, 16, 21, 45, 0), ipswich.NextPlaysLocal.First());
        Assert.IsFalse(all.Single(x => x.Schedule.LocationId == s.Norwich.Id).LocationHasHours);
    }

    [TestMethod]
    public async Task SetEnabled_AndDelete_ChangeTheSchedule()
    {
        Setup s = await SeedAsync();
        AudioClipSchedule added = (await s.Service.SaveAsync(s.Admin, ClosingCall(s), UserId)).Data!;

        Assert.IsTrue((await s.Service.SetEnabledAsync(s.IpswichManager, added.Id, false)).IsSuccess);
        Assert.IsTrue((await s.Service.DeleteAsync(s.IpswichManager, added.Id)).IsSuccess);

        await using ApplicationDbContext ctx = new(s.Options);
        AudioClipSchedule saved = await ctx.AudioClipSchedules.SingleAsync();
        Assert.IsFalse(saved.IsEnabled);
        Assert.IsNotNull(saved.DeleteDate);
        Assert.AreEqual(0, (await s.Service.ListAsync(s.Admin)).Data!.Count);
    }

    [TestMethod]
    public async Task SetEnabled_OnAnotherLocationsSchedule_IsRefusedForAManager()
    {
        Setup s = await SeedAsync();
        AudioClipSchedule norwich = (await s.Service.SaveAsync(s.Admin, ClosingCall(s, s.Norwich.Id), UserId)).Data!;

        Assert.IsFalse((await s.Service.SetEnabledAsync(s.IpswichManager, norwich.Id, false)).IsSuccess);
        Assert.IsFalse((await s.Service.DeleteAsync(s.IpswichManager, norwich.Id)).IsSuccess);
    }
}
