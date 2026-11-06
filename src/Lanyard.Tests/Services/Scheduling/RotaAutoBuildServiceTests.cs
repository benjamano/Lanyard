using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class RotaAutoBuildServiceTests
{
    private const string ManagerId = "manager-user";

    // Monday 5 October 2026; "now" is the Thursday before.
    private static readonly DateOnly Week = new(2026, 10, 5);
    private static readonly DateOnly Saturday = Week.AddDays(5);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record World(DbContextOptions<ApplicationDbContext> Options, Location Ipswich, Location Wisbech, StaffPosition Host, UserProfile Amy, UserProfile Tom, UserProfile Priya)
    {
        public LocationScope ManagerScope => SchedulingTestHelpers.ManagerScopeFor(Ipswich);
    }

    // Ipswich has Amy and Tom, both Hosts; Priya is a Host at Wisbech only. Ipswich has a Saturday
    // AM template needing three Hosts.
    private static async Task<World> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        StaffPosition host = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Host");

        Location wisbech;

        await using (ApplicationDbContext ctx = new(options))
        {
            wisbech = new Location { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
            ctx.Locations.Add(wisbech);
            await ctx.SaveChangesAsync();
        }

        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Amy");
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Tom");
        UserProfile priya = await SchedulingTestHelpers.SeedUserAsync(options, wisbech, "Priya");

        await using (ApplicationDbContext ctx = new(options))
        {
            foreach (UserProfile user in new[] { amy, tom, priya })
            {
                ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = user.Id, StaffPositionId = host.Id, IsPrimary = true, CreateDate = DateTime.UtcNow });
            }

            ShiftTemplate template = new()
            {
                Id = Guid.NewGuid(),
                CompanyId = company.Id,
                LocationId = ipswich.Id,
                Name = "Saturday AM",
                DayOfWeek = DayOfWeek.Saturday,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(14, 0)
            };
            template.Slots.Add(new ShiftTemplateSlot { Id = Guid.NewGuid(), ShiftTemplateId = template.Id, StaffPositionId = host.Id, Headcount = 3 });
            ctx.ShiftTemplates.Add(template);

            await ctx.SaveChangesAsync();
        }

        return new World(options, ipswich, wisbech, host, amy, tom, priya);
    }

    private static RotaAutoBuildService GetService(DbContextOptions<ApplicationDbContext> options, DateTime? nowUtc = null)
    {
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        return new RotaAutoBuildService(factory, new ContractRequirementService(factory), NullLogger<RotaAutoBuildService>.Instance,
            new FixedClock(nowUtc ?? new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)));
    }

    [TestMethod]
    public async Task PreviewAsync_SavesNothing()
    {
        World w = await SeedAsync();

        Result<AutoBuildResult> preview = await GetService(w.Options).PreviewAsync(w.ManagerScope, w.Ipswich.Id, Week, Week.AddDays(6));

        Assert.IsTrue(preview.IsSuccess, preview.Error);
        Assert.AreEqual(2, preview.Data!.Assigned, "Amy and Tom");
        Assert.AreEqual(1, preview.Data.Open, "a third Host nobody can be");

        await using ApplicationDbContext ctx = new(w.Options);
        Assert.AreEqual(0, await ctx.Shifts.CountAsync());
    }

    [TestMethod]
    public async Task ApplyAsync_AddsDraftsOnlyForLocationMembers_AndRunningAgainAddsNothing()
    {
        World w = await SeedAsync();
        RotaAutoBuildService service = GetService(w.Options);

        Result<AutoBuildResult> applied = await service.ApplyAsync(w.ManagerScope, w.Ipswich.Id, Week, Week.AddDays(6), ManagerId);

        Assert.IsTrue(applied.IsSuccess, applied.Error);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            List<Shift> shifts = await ctx.Shifts.ToListAsync();

            Assert.AreEqual(3, shifts.Count);
            Assert.IsTrue(shifts.All(x => x.PublishedDateUtc is null && x.LocationId == w.Ipswich.Id && x.StaffPositionId == w.Host.Id));
            Assert.IsTrue(shifts.All(x => x.UserId != w.Priya.Id), "Priya isn't at Ipswich");
            Assert.AreEqual(1, shifts.Count(x => x.UserId is null));
            Assert.IsTrue(shifts.All(x => x.StartUtc == RotaTime.ToUtc(Saturday, new TimeOnly(9, 0))));
        }

        Result<AutoBuildResult> again = await service.ApplyAsync(w.ManagerScope, w.Ipswich.Id, Week, Week.AddDays(6), ManagerId);

        Assert.IsTrue(again.IsSuccess, again.Error);
        Assert.AreEqual(0, again.Data!.Lines.Count);
        Assert.AreEqual(3, again.Data.AlreadyCovered);
    }

    [TestMethod]
    public async Task ApplyAsync_SomeoneAlreadyWorkingElsewhereThen_NotPicked()
    {
        World w = await SeedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = w.Amy.Id, LocationId = w.Wisbech.Id, CreateDate = DateTime.UtcNow });
            ctx.Shifts.Add(new Shift
            {
                Id = Guid.NewGuid(),
                LocationId = w.Wisbech.Id,
                UserId = w.Amy.Id,
                StartUtc = RotaTime.ToUtc(Saturday, new TimeOnly(8, 0)),
                EndUtc = RotaTime.ToUtc(Saturday, new TimeOnly(12, 0)),
                CreateByUserId = ManagerId
            });
            await ctx.SaveChangesAsync();
        }

        Result<AutoBuildResult> preview = await GetService(w.Options).PreviewAsync(w.ManagerScope, w.Ipswich.Id, Week, Week.AddDays(6));

        Assert.AreEqual(1, preview.Data!.Assigned);
        Assert.AreEqual(w.Tom.Id, preview.Data.Lines.Single(x => !x.IsOpen).UserId);
    }

    [TestMethod]
    public async Task PreviewAsync_PastDaysSkipped()
    {
        World w = await SeedAsync();

        // Sunday after: Saturday is over.
        Result<AutoBuildResult> preview = await GetService(w.Options, new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc))
            .PreviewAsync(w.ManagerScope, w.Ipswich.Id, Week, Week.AddDays(6));

        Assert.IsTrue(preview.IsSuccess, preview.Error);
        Assert.AreEqual(0, preview.Data!.Lines.Count);
    }

    [TestMethod]
    public async Task PreviewAsync_ManagerOfAnotherLocation_Refused()
    {
        World w = await SeedAsync();

        Result<AutoBuildResult> preview = await GetService(w.Options).PreviewAsync(SchedulingTestHelpers.ManagerScopeFor(w.Wisbech), w.Ipswich.Id, Week, Week.AddDays(6));

        Assert.IsFalse(preview.IsSuccess);
    }

    [TestMethod]
    public async Task PreviewAsync_RangeTooLong_Refused()
    {
        World w = await SeedAsync();

        Result<AutoBuildResult> preview = await GetService(w.Options).PreviewAsync(w.ManagerScope, w.Ipswich.Id, Week, Week.AddDays(60));

        Assert.IsFalse(preview.IsSuccess);
    }
}
