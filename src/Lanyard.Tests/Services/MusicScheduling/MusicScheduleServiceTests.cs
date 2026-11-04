using Lanyard.Application.Services;
using Lanyard.Application.Services.Locations;
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

namespace Lanyard.Tests.Services.MusicScheduling;

[TestClass]
public class MusicScheduleServiceTests
{
    private const string UserId = "user-1";

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    // Signed in at a company, so the tenant filters apply exactly as they do for a real user.
    private static MusicScheduleService GetService(DbContextOptions<ApplicationDbContext> options, int companyId)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options, new FixedTenantProvider(companyId)));

        return new MusicScheduleService(factoryMock.Object, TimeProvider.System, NullLogger<MusicScheduleService>.Instance);
    }

    private static LocationScope AdminScopeFor(Location location) =>
        new(true, location.Id, location.CompanyId, location.Name, IsManager: true);

    private static async Task<Playlist> SeedPlaylistAsync(DbContextOptions<ApplicationDbContext> options, int companyId, string name = "Lobby")
    {
        await using ApplicationDbContext ctx = new(options);

        Playlist playlist = new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = name, CreateDate = DateTime.UtcNow };
        ctx.Playlists.Add(playlist);
        await ctx.SaveChangesAsync();

        return playlist;
    }

    private static MusicDaypart NewDaypart(string name, string start, string end, Guid? playlistId, params DayOfWeek[] days) => new()
    {
        Name = name,
        DaysOfWeek = AutomationScheduleDays.Serialize(days.Length == 0 ? [DayOfWeek.Monday] : days),
        StartTime = TimeOnly.Parse(start),
        EndTime = TimeOnly.Parse(end),
        PlaylistId = playlistId
    };

    [TestMethod]
    public async Task SaveDaypart_ThenGetSchedule_ReturnsIt()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Playlist playlist = await SeedPlaylistAsync(options, company.Id);
        MusicScheduleService service = GetService(options, company.Id);
        LocationScope scope = SchedulingTestHelpers.ManagerScopeFor(location);

        Result<MusicDaypart> saved = await service.SaveDaypartAsync(scope, location.Id,
            NewDaypart("  Before opening ", "08:00", "10:00", playlist.Id, DayOfWeek.Saturday, DayOfWeek.Monday), UserId);

        Assert.IsTrue(saved.IsSuccess, saved.Error);

        Result<MusicSchedule> schedule = await service.GetScheduleAsync(scope, location.Id);

        Assert.IsTrue(schedule.IsSuccess, schedule.Error);
        Assert.IsFalse(schedule.Data!.IsEnabled, "A new schedule starts switched off.");
        MusicDaypart daypart = schedule.Data.Dayparts.Single();
        Assert.AreEqual("Before opening", daypart.Name);
        Assert.AreEqual("1,6", daypart.DaysOfWeek);
        Assert.AreEqual("Lobby", daypart.Playlist?.Name);
    }

    [TestMethod]
    public async Task SaveDaypart_AddsASecondDaypartToAnExistingSchedule()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        MusicScheduleService service = GetService(options, company.Id);
        LocationScope scope = SchedulingTestHelpers.ManagerScopeFor(location);

        Result<MusicDaypart> first = await service.SaveDaypartAsync(scope, location.Id, NewDaypart("Morning", "08:00", "10:00", null), UserId);
        Result<MusicDaypart> second = await service.SaveDaypartAsync(scope, location.Id, NewDaypart("Daytime", "10:00", "17:00", null), UserId);

        Assert.IsTrue(first.IsSuccess, first.Error);
        Assert.IsTrue(second.IsSuccess, second.Error);
        Assert.AreEqual(2, (await service.GetScheduleAsync(scope, location.Id)).Data!.Dayparts.Count);
    }

    [TestMethod]
    public async Task GetSchedule_ForALocationNeverSaved_IsEmptyAndOff()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<MusicSchedule> schedule = await GetService(options, company.Id)
            .GetScheduleAsync(SchedulingTestHelpers.ManagerScopeFor(location), location.Id);

        Assert.IsTrue(schedule.IsSuccess, schedule.Error);
        Assert.AreEqual(0, schedule.Data!.Dayparts.Count);
        Assert.IsFalse(schedule.Data.IsEnabled);
    }

    [TestMethod]
    public async Task SaveDaypart_RejectsAnOverlapWithAnotherDaypart()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        MusicScheduleService service = GetService(options, company.Id);
        LocationScope scope = SchedulingTestHelpers.ManagerScopeFor(location);

        await service.SaveDaypartAsync(scope, location.Id, NewDaypart("Late night", "22:00", "02:00", null, DayOfWeek.Friday), UserId);

        Result<MusicDaypart> result = await service.SaveDaypartAsync(scope, location.Id,
            NewDaypart("Early", "01:00", "03:00", null, DayOfWeek.Saturday), UserId);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "Late night");
    }

    [TestMethod]
    public async Task SaveDaypart_CanMoveAnExistingDaypartWithoutClashingWithItself()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        MusicScheduleService service = GetService(options, company.Id);
        LocationScope scope = SchedulingTestHelpers.ManagerScopeFor(location);

        MusicDaypart created = (await service.SaveDaypartAsync(scope, location.Id, NewDaypart("Morning", "08:00", "10:00", null), UserId)).Data!;

        MusicDaypart moved = NewDaypart("Morning", "08:30", "10:30", null);
        moved.Id = created.Id;
        moved.Volume = 40;

        Result<MusicDaypart> result = await service.SaveDaypartAsync(scope, location.Id, moved, UserId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        await using ApplicationDbContext ctx = new(options);
        MusicDaypart stored = await ctx.MusicDayparts.SingleAsync();
        Assert.AreEqual(new TimeOnly(8, 30), stored.StartTime);
        Assert.AreEqual(40, stored.Volume);
    }

    [TestMethod]
    public async Task DeleteDaypart_SoftDeletesIt()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        MusicScheduleService service = GetService(options, company.Id);
        LocationScope scope = SchedulingTestHelpers.ManagerScopeFor(location);

        MusicDaypart created = (await service.SaveDaypartAsync(scope, location.Id, NewDaypart("Morning", "08:00", "10:00", null), UserId)).Data!;

        Result<bool> deleted = await service.DeleteDaypartAsync(scope, location.Id, created.Id, UserId);

        Assert.IsTrue(deleted.IsSuccess, deleted.Error);
        Assert.AreEqual(0, (await service.GetScheduleAsync(scope, location.Id)).Data!.Dayparts.Count);

        await using ApplicationDbContext ctx = new(options);
        Assert.IsFalse((await ctx.MusicDayparts.SingleAsync()).IsActive, "The row stays, marked inactive.");
    }

    [TestMethod]
    public async Task SaveDaypart_ManagerCannotEditAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Company company, Location home) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Location elsewhere;
        await using (ApplicationDbContext ctx = new(options))
        {
            elsewhere = new Location { CompanyId = company.Id, Name = "Colchester", IsActive = true };
            ctx.Locations.Add(elsewhere);
            await ctx.SaveChangesAsync();
        }

        Result<MusicDaypart> result = await GetService(options, company.Id).SaveDaypartAsync(
            SchedulingTestHelpers.ManagerScopeFor(home), elsewhere.Id, NewDaypart("Morning", "08:00", "10:00", null), UserId);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveDaypart_StaffWithoutTheManagerRoleCannotEdit()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<MusicDaypart> result = await GetService(options, company.Id).SaveDaypartAsync(
            SchedulingTestHelpers.StaffScopeFor(location), location.Id, NewDaypart("Morning", "08:00", "10:00", null), UserId);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveDaypart_AdminCanEditAnyLocationInTheirCompanyButNotAnotherCompanys()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Company company, Location home) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherCompanyLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Partyman", "Colchester");

        Location secondOwn;
        await using (ApplicationDbContext ctx = new(options))
        {
            secondOwn = new Location { CompanyId = company.Id, Name = "Norwich", IsActive = true };
            ctx.Locations.Add(secondOwn);
            await ctx.SaveChangesAsync();
        }

        MusicScheduleService service = GetService(options, company.Id);
        LocationScope admin = AdminScopeFor(home);

        Result<MusicDaypart> own = await service.SaveDaypartAsync(admin, secondOwn.Id, NewDaypart("Morning", "08:00", "10:00", null), UserId);
        Result<MusicDaypart> other = await service.SaveDaypartAsync(admin, otherCompanyLocation.Id, NewDaypart("Morning", "08:00", "10:00", null), UserId);

        Assert.IsTrue(own.IsSuccess, own.Error);
        Assert.IsFalse(other.IsSuccess, "An Admin must not create a schedule at another company's location.");

        await using ApplicationDbContext check = new(options);
        Assert.IsFalse(await check.MusicSchedules.AnyAsync(x => x.LocationId == otherCompanyLocation.Id));
    }

    [TestMethod]
    public async Task SaveDaypart_RejectsAnotherCompanysPlaylist()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (Company otherCompany, _) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Partyman", "Colchester");
        Playlist theirs = await SeedPlaylistAsync(options, otherCompany.Id, "Their playlist");

        Result<MusicDaypart> result = await GetService(options, company.Id).SaveDaypartAsync(
            SchedulingTestHelpers.ManagerScopeFor(location), location.Id, NewDaypart("Morning", "08:00", "10:00", theirs.Id), UserId);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SetEnabled_CreatesTheScheduleAndTurnsItOn()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        MusicScheduleService service = GetService(options, company.Id);
        LocationScope scope = SchedulingTestHelpers.ManagerScopeFor(location);

        Result<bool> result = await service.SetEnabledAsync(scope, location.Id, true, UserId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue((await service.GetScheduleAsync(scope, location.Id)).Data!.IsEnabled);
    }

    [TestMethod]
    public async Task GetLocationKiosks_OnlyListsKiosksAtThatLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Clients.AddRange(
                new Client { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Lobby", LocationId = location.Id },
                new Client { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Spare" });
            await ctx.SaveChangesAsync();
        }

        Result<List<MusicScheduleKiosk>> kiosks = await GetService(options, company.Id)
            .GetLocationKiosksAsync(SchedulingTestHelpers.ManagerScopeFor(location), location.Id);

        Assert.IsTrue(kiosks.IsSuccess, kiosks.Error);
        Assert.AreEqual("Lobby", kiosks.Data!.Single().Name);
    }

    [TestMethod]
    public async Task GetConflictingAutomationRules_NamesEnabledMusicRulesAimedAtThisLocationsKiosks()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Guid lobby = Guid.NewGuid();
        Guid spare = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Clients.AddRange(
                new Client { Id = lobby, CompanyId = company.Id, Name = "Lobby", LocationId = location.Id },
                new Client { Id = spare, CompanyId = company.Id, Name = "Spare" });

            ctx.AutomationRules.AddRange(
                MusicRule(company.Id, "Open up", lobby, isEnabled: true),
                MusicRule(company.Id, "Old rule", lobby, isEnabled: false),
                MusicRule(company.Id, "Spare kiosk rule", spare, isEnabled: true));

            await ctx.SaveChangesAsync();
        }

        Result<List<string>> names = await GetService(options, company.Id)
            .GetConflictingAutomationRulesAsync(SchedulingTestHelpers.ManagerScopeFor(location), location.Id);

        Assert.IsTrue(names.IsSuccess, names.Error);
        CollectionAssert.AreEqual(new[] { "Open up" }, names.Data);
    }

    private static AutomationRule MusicRule(int companyId, string name, Guid targetClientId, bool isEnabled) => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = companyId,
        Name = name,
        TriggerClientId = targetClientId,
        IsActive = true,
        IsEnabled = isEnabled,
        CreateDate = DateTime.UtcNow,
        Actions =
        [
            new AutomationRuleAction
            {
                Id = Guid.NewGuid(),
                ActionType = AutomationActionTypes.MusicControl,
                ParametersJson = $$"""{"TargetClientId":"{{targetClientId}}","Operation":"Play"}""",
                IsActive = true
            }
        ]
    };
}
