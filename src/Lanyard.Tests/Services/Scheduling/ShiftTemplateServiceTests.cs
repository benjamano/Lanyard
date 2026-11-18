using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class ShiftTemplateServiceTests
{
    private const string ManagerId = "manager-user";

    private sealed record World(DbContextOptions<ApplicationDbContext> Options, Company Company, Location Ipswich, Location Wisbech, StaffPosition Host, StaffPosition DutyManager)
    {
        public LocationScope ManagerScope => SchedulingTestHelpers.ManagerScopeFor(Ipswich);
    }

    private static async Task<World> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Location wisbech;
        await using (ApplicationDbContext ctx = new(options))
        {
            wisbech = new Location { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
            ctx.Locations.Add(wisbech);
            await ctx.SaveChangesAsync();
        }

        StaffPosition host = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Host", 1);
        StaffPosition duty = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Duty Manager", 0);

        return new World(options, company, ipswich, wisbech, host, duty);
    }

    private static ShiftTemplateService GetService(DbContextOptions<ApplicationDbContext> options) =>
        new(SchedulingTestHelpers.GetFactory(options), NullLogger<ShiftTemplateService>.Instance);

    private static ShiftTemplate Template(Location location, params (StaffPosition Position, int Count)[] slots) => new()
    {
        CompanyId = 0,
        LocationId = location.Id,
        Name = " Saturday AM ",
        DayOfWeek = DayOfWeek.Saturday,
        StartTime = new TimeOnly(9, 0),
        EndTime = new TimeOnly(14, 0),
        Slots = slots.Select(s => new ShiftTemplateSlot { ShiftTemplateId = Guid.Empty, StaffPositionId = s.Position.Id, Headcount = s.Count }).ToList()
    };

    [TestMethod]
    public async Task SaveTemplateAsync_New_SavedWithSlotsUnderTheLocationsCompany()
    {
        World w = await SeedAsync();

        Result<ShiftTemplate> saved = await GetService(w.Options).SaveTemplateAsync(w.ManagerScope, Template(w.Ipswich, (w.DutyManager, 2), (w.Host, 4)), ManagerId);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.AreEqual("Saturday AM", saved.Data!.Name);
        Assert.AreEqual(w.Company.Id, saved.Data.CompanyId);
        Assert.AreEqual(ManagerId, saved.Data.CreateByUserId);

        Result<List<ShiftTemplate>> loaded = await GetService(w.Options).GetTemplatesAsync(w.ManagerScope, w.Ipswich.Id);

        Assert.AreEqual(1, loaded.Data!.Count);
        Assert.AreEqual(6, loaded.Data[0].TotalHeadcount);
        Assert.IsTrue(loaded.Data[0].Slots.All(x => x.StaffPosition is not null));
    }

    [TestMethod]
    public async Task SaveTemplateAsync_Edit_ReplacesSlots()
    {
        World w = await SeedAsync();
        ShiftTemplateService service = GetService(w.Options);

        ShiftTemplate first = (await service.SaveTemplateAsync(w.ManagerScope, Template(w.Ipswich, (w.DutyManager, 2), (w.Host, 4)), ManagerId)).Data!;

        ShiftTemplate edit = Template(w.Ipswich, (w.Host, 3));
        edit.Id = first.Id;
        edit.EndTime = new TimeOnly(15, 0);

        Result<ShiftTemplate> saved = await service.SaveTemplateAsync(w.ManagerScope, edit, ManagerId);

        Assert.IsTrue(saved.IsSuccess, saved.Error);

        await using ApplicationDbContext ctx = new(w.Options);
        List<ShiftTemplateSlot> slots = await ctx.ShiftTemplateSlots.Where(x => x.ShiftTemplateId == first.Id).ToListAsync();

        Assert.AreEqual(1, slots.Count);
        Assert.AreEqual(3, slots[0].Headcount);
        Assert.AreEqual(new TimeOnly(15, 0), (await ctx.ShiftTemplates.SingleAsync()).EndTime);
    }

    [TestMethod]
    public async Task SaveTemplateAsync_ManagerOfAnotherLocation_Refused()
    {
        World w = await SeedAsync();

        Result<ShiftTemplate> saved = await GetService(w.Options).SaveTemplateAsync(w.ManagerScope, Template(w.Wisbech, (w.Host, 1)), ManagerId);

        Assert.IsFalse(saved.IsSuccess);
    }

    [TestMethod]
    public async Task SaveTemplateAsync_PlainStaff_Refused()
    {
        World w = await SeedAsync();

        Result<ShiftTemplate> saved = await GetService(w.Options).SaveTemplateAsync(SchedulingTestHelpers.StaffScopeFor(w.Ipswich), Template(w.Ipswich, (w.Host, 1)), "staff");

        Assert.IsFalse(saved.IsSuccess);
    }

    [TestMethod]
    public async Task SaveTemplateAsync_AnotherCompanysPosition_Refused()
    {
        World w = await SeedAsync();
        (Company other, _) = await SchedulingTestHelpers.SeedCompanyAsync(w.Options, "Other", "Elsewhere");
        StaffPosition theirs = await SchedulingTestHelpers.SeedPositionAsync(w.Options, other, "Host");

        Result<ShiftTemplate> saved = await GetService(w.Options).SaveTemplateAsync(w.ManagerScope, Template(w.Ipswich, (theirs, 1)), ManagerId);

        Assert.IsFalse(saved.IsSuccess);
    }

    [TestMethod]
    public async Task SaveTemplateAsync_ArchivedPosition_RefusedForNewButKeptWhenAlreadyUsed()
    {
        World w = await SeedAsync();
        ShiftTemplateService service = GetService(w.Options);

        ShiftTemplate saved = (await service.SaveTemplateAsync(w.ManagerScope, Template(w.Ipswich, (w.Host, 2)), ManagerId)).Data!;

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            (await ctx.StaffPositions.SingleAsync(x => x.Id == w.Host.Id)).IsActive = false;
            await ctx.SaveChangesAsync();
        }

        Assert.IsFalse((await service.SaveTemplateAsync(w.ManagerScope, Template(w.Ipswich, (w.Host, 1)), ManagerId)).IsSuccess, "new template");

        ShiftTemplate edit = Template(w.Ipswich, (w.Host, 3));
        edit.Id = saved.Id;
        Result<ShiftTemplate> edited = await service.SaveTemplateAsync(w.ManagerScope, edit, ManagerId);

        Assert.IsTrue(edited.IsSuccess, edited.Error);
    }

    [TestMethod]
    public async Task SetArchivedAsync_HiddenUnlessAskedForThenRestored()
    {
        World w = await SeedAsync();
        ShiftTemplateService service = GetService(w.Options);

        ShiftTemplate saved = (await service.SaveTemplateAsync(w.ManagerScope, Template(w.Ipswich, (w.Host, 2)), ManagerId)).Data!;

        Assert.IsTrue((await service.SetArchivedAsync(w.ManagerScope, saved.Id, true, ManagerId)).IsSuccess);
        Assert.AreEqual(0, (await service.GetTemplatesAsync(w.ManagerScope, w.Ipswich.Id)).Data!.Count);
        Assert.AreEqual(1, (await service.GetTemplatesAsync(w.ManagerScope, w.Ipswich.Id, includeArchived: true)).Data!.Count);

        Assert.IsTrue((await service.SetArchivedAsync(w.ManagerScope, saved.Id, false, ManagerId)).IsSuccess);
        Assert.AreEqual(1, (await service.GetTemplatesAsync(w.ManagerScope, w.Ipswich.Id)).Data!.Count);
    }

    [TestMethod]
    public async Task GetTemplatesAsync_OrderedMondayFirstThenByTime()
    {
        World w = await SeedAsync();
        ShiftTemplateService service = GetService(w.Options);

        ShiftTemplate sundayPm = Template(w.Ipswich, (w.Host, 1));
        sundayPm.Name = "Sunday PM";
        sundayPm.DayOfWeek = DayOfWeek.Sunday;
        sundayPm.StartTime = new TimeOnly(14, 0);
        sundayPm.EndTime = new TimeOnly(19, 0);

        ShiftTemplate sundayAm = Template(w.Ipswich, (w.Host, 1));
        sundayAm.Name = "Sunday AM";
        sundayAm.DayOfWeek = DayOfWeek.Sunday;

        ShiftTemplate monday = Template(w.Ipswich, (w.Host, 1));
        monday.Name = "Monday";
        monday.DayOfWeek = DayOfWeek.Monday;

        foreach (ShiftTemplate t in new[] { sundayPm, sundayAm, monday })
        {
            Assert.IsTrue((await service.SaveTemplateAsync(w.ManagerScope, t, ManagerId)).IsSuccess);
        }

        List<string> names = (await service.GetTemplatesAsync(w.ManagerScope, w.Ipswich.Id)).Data!.Select(x => x.Name).ToList();

        CollectionAssert.AreEqual(new[] { "Monday", "Sunday AM", "Sunday PM" }, names);
    }

    [TestMethod]
    public async Task SaveTemplateAsync_Regulars_SavedInOrderAndMustWorkHere()
    {
        World w = await SeedAsync();
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(w.Options, w.Ipswich, "Amy");
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(w.Options, w.Ipswich, "Tom");
        UserProfile priya = await SchedulingTestHelpers.SeedUserAsync(w.Options, w.Wisbech, "Priya");
        ShiftTemplateService service = GetService(w.Options);

        ShiftTemplate template = Template(w.Ipswich, (w.Host, 2));
        template.Slots[0].Regulars =
        [
            new ShiftTemplateSlotRegular { ShiftTemplateSlotId = Guid.Empty, UserId = tom.Id },
            new ShiftTemplateSlotRegular { ShiftTemplateSlotId = Guid.Empty, UserId = amy.Id }
        ];

        Result<ShiftTemplate> saved = await service.SaveTemplateAsync(w.ManagerScope, template, ManagerId);
        Assert.IsTrue(saved.IsSuccess, saved.Error);

        ShiftTemplate loaded = (await service.GetTemplatesAsync(w.ManagerScope, w.Ipswich.Id)).Data!.Single();
        CollectionAssert.AreEqual(new[] { tom.Id, amy.Id }, loaded.Slots.Single().Regulars.OrderBy(x => x.SortOrder).Select(x => x.UserId).ToList());

        ShiftTemplate elsewhere = Template(w.Ipswich, (w.Host, 1));
        elsewhere.Slots[0].Regulars = [new ShiftTemplateSlotRegular { ShiftTemplateSlotId = Guid.Empty, UserId = priya.Id }];
        Assert.IsFalse((await service.SaveTemplateAsync(w.ManagerScope, elsewhere, ManagerId)).IsSuccess, "Priya works at Wisbech");
    }
}
