using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Parties;
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
public class TemplateLearningServiceTests
{
    private const string ManagerId = "manager-user";

    // "Now" is Wednesday 2 December 2026; the last 8 whole weeks run 5 October to 29 November.
    private static readonly DateTime Now = new(2026, 12, 2, 9, 0, 0, DateTimeKind.Utc);
    private static readonly List<DateOnly> Saturdays = Enumerable.Range(0, 8).Select(i => new DateOnly(2026, 10, 10).AddDays(7 * i)).ToList();

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed record World(DbContextOptions<ApplicationDbContext> Options, Location Location, Dictionary<string, UserProfile> People)
    {
        public LocationScope Scope => SchedulingTestHelpers.ManagerScopeFor(Location);
    }

    private static async Task<World> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");
        StaffPosition manager = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Manager");

        Dictionary<string, UserProfile> people = [];
        foreach (string name in new[] { "Ben", "Rupert", "Ryan", "James" })
        {
            people[name] = await SchedulingTestHelpers.SeedUserAsync(options, location, name);
        }

        await using ApplicationDbContext ctx = new(options);

        void Holds(string name, StaffPosition position) =>
            ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = people[name].Id, StaffPositionId = position.Id, IsPrimary = true, CreateDate = DateTime.UtcNow });

        Holds("Ben", csa);
        Holds("James", csa);
        Holds("Rupert", manager);
        Holds("Ryan", manager);

        void Worked(DateOnly day, string start, string end, StaffPosition position, string name) => ctx.Shifts.Add(new Shift
        {
            Id = Guid.NewGuid(),
            LocationId = location.Id,
            UserId = people[name].Id,
            StaffPositionId = position.Id,
            StartUtc = RotaTime.ToUtc(day, TimeOnly.Parse(start)),
            EndUtc = RotaTime.ToUtc(day, TimeOnly.Parse(end)),
            PublishedDateUtc = DateTime.UtcNow,
            IsActive = true,
            CreateByUserId = ManagerId
        });

        for (int i = 0; i < Saturdays.Count; i++)
        {
            DateOnly day = Saturdays[i];
            Worked(day, "09:00", "11:00", csa, "Ben");
            Worked(day, "11:00", "14:00", csa, "James");
            Worked(day, "08:30", "18:30", manager, "Rupert");

            if (i % 3 == 0)
            {
                ctx.PartyBookings.Add(new PartyBooking
                {
                    Id = Guid.NewGuid(),
                    CompanyId = company.Id,
                    LocationId = location.Id,
                    HireKind = PrivateHireKind.PlayOnly,
                    StartUtc = RotaTime.ToUtc(day, new TimeOnly(18, 30)),
                    EndUtc = RotaTime.ToUtc(day, new TimeOnly(21, 30)),
                    Status = PartyBookingStatus.Booked
                });
                Worked(day, "08:30", "21:30", manager, "Ryan");
            }
        }

        await ctx.SaveChangesAsync();

        return new World(options, location, people);
    }

    private static TemplateLearningService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        return new TemplateLearningService(factory, new ContractRequirementService(factory),
            new ShiftTemplateService(factory, NullLogger<ShiftTemplateService>.Instance),
            new PartyStaffingService(factory, NullLogger<PartyStaffingService>.Instance),
            NullLogger<TemplateLearningService>.Instance, new FixedClock(Now));
    }

    [TestMethod]
    public async Task LearnAsync_LearnsThePatternAndAutoBuildWouldMatchIt()
    {
        World w = await SeedAsync();

        Result<TemplateLearningResult> learned = await GetService(w.Options).LearnAsync(w.Scope, w.Location.Id, 8);

        Assert.IsTrue(learned.IsSuccess, learned.Error);
        TemplateLearningResult result = learned.Data!;
        Assert.AreEqual(new DateOnly(2026, 10, 5), result.From);
        Assert.AreEqual(new DateOnly(2026, 11, 29), result.To);
        Assert.AreEqual(4, result.Templates.Count, "three every-week shifts and the hire-only manager shift");
        Assert.AreEqual(1, result.HireStaffing.Single().Count);

        LearnedCheck check = result.Check!;
        Assert.AreEqual(27, check.ActualShifts);
        Assert.AreEqual(100, check.MatchPercent, string.Join(" | ", check.Examples));
        Assert.AreEqual(0, check.Extra);
    }

    [TestMethod]
    public async Task LearnAsync_LongerPeriodThanTheRota_StartsAtTheFirstWeekWithARota()
    {
        World w = await SeedAsync();

        TemplateLearningResult result = (await GetService(w.Options).LearnAsync(w.Scope, w.Location.Id, 26)).Data!;

        Assert.AreEqual(new DateOnly(2026, 10, 5), result.From, "the rota starts the week of 5 October, not in June");
        Assert.AreEqual(100, result.Check!.MatchPercent);
        Assert.AreEqual(0, result.Check.Extra, "no shifts built for weeks nobody worked");
    }

    [TestMethod]
    public async Task ApplyAsync_SavesTemplatesAndHireStaffing_ThenEverythingIsAlreadySetUp()
    {
        World w = await SeedAsync();
        TemplateLearningService service = GetService(w.Options);
        TemplateLearningResult result = (await service.LearnAsync(w.Scope, w.Location.Id, 8)).Data!;

        Result<int> saved = await service.ApplyAsync(w.Scope, w.Location.Id, result.Templates, result.HireStaffing, ManagerId);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.AreEqual(5, saved.Data);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            Assert.AreEqual(4, await ctx.ShiftTemplates.CountAsync());
            Assert.AreEqual(PrivateHireKind.PlayOnly, (await ctx.PartyStaffingRules.SingleAsync()).HireKind);
        }

        TemplateLearningResult again = (await service.LearnAsync(w.Scope, w.Location.Id, 8)).Data!;
        Assert.AreEqual(0, again.Suggestions, "learning again suggests nothing new");
    }

    [TestMethod]
    public async Task LearnAsync_OtherLocationsManager_Refused()
    {
        World w = await SeedAsync();
        Location other = new() { Id = 999, CompanyId = w.Location.CompanyId, Name = "Elsewhere" };

        Assert.IsFalse((await GetService(w.Options).LearnAsync(SchedulingTestHelpers.ManagerScopeFor(other), w.Location.Id, 8)).IsSuccess);
    }

    [TestMethod]
    public async Task LearnAsync_PeriodOutOfRange_Refused()
    {
        World w = await SeedAsync();

        Assert.IsFalse((await GetService(w.Options).LearnAsync(w.Scope, w.Location.Id, 1)).IsSuccess);
        Assert.IsFalse((await GetService(w.Options).LearnAsync(w.Scope, w.Location.Id, 60)).IsSuccess);
    }

    // --- Monthly suggestions ---

    [TestMethod]
    public async Task RunMonthlyAsync_NoTemplatesButARotaBuiltByHand_OffersFirstSetup()
    {
        World w = await SeedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            TemplateSuggestionRun? run = await GetService(w.Options).RunMonthlyAsync(ctx, w.Location, Now);

            Assert.IsNotNull(run);
            Assert.IsTrue(run.FirstSetup);
            Assert.AreEqual(5, run.Suggestions, "four templates and the hire staffing");
            Assert.AreEqual(100, run.MatchPercent);
        }

        TemplateSuggestionRun? open = (await GetService(w.Options).GetOpenSuggestionsAsync(w.Scope, w.Location.Id)).Data;
        Assert.IsTrue(open is { FirstSetup: true });
    }

    [TestMethod]
    public async Task RunMonthlyAsync_NoTemplatesAndTooLittleRota_NothingYet()
    {
        World w = await SeedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            // Keep only the last three weeks of the rota.
            DateTime cutoff = RotaTime.StartOfDayUtc(new DateOnly(2026, 11, 9));
            ctx.Shifts.RemoveRange(await ctx.Shifts.Where(x => x.StartUtc < cutoff).ToListAsync());
            await ctx.SaveChangesAsync();
        }

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            Assert.IsNull(await GetService(w.Options).RunMonthlyAsync(ctx, w.Location, Now));
            Assert.AreEqual(0, await ctx.TemplateSuggestionRuns.CountAsync());
        }
    }

    [TestMethod]
    public async Task RunMonthlyAsync_TemplatesDifferFromTheRota_OpenSuggestionsUntilDismissed()
    {
        World w = await SeedAsync();
        TemplateLearningService service = GetService(w.Options);
        TemplateLearningResult learned = (await service.LearnAsync(w.Scope, w.Location.Id, 8)).Data!;

        // Only the early CSA template is set up, so the rest are suggested.
        await service.ApplyAsync(w.Scope, w.Location.Id, learned.Templates.Where(x => x.StartTime == new TimeOnly(9, 0)).ToList(), [], ManagerId);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            TemplateSuggestionRun? run = await service.RunMonthlyAsync(ctx, w.Location, Now);
            Assert.AreEqual(4, run!.Suggestions, "two templates, the hire-only one and its hire staffing");
            Assert.IsFalse(run.FirstSetup);
            Assert.IsNull(run.MatchPercent);
        }

        TemplateSuggestionRun? open = (await service.GetOpenSuggestionsAsync(w.Scope, w.Location.Id)).Data;
        Assert.IsNotNull(open);
        Assert.AreEqual(new DateOnly(2026, 10, 5), open.From);

        Assert.IsTrue((await service.DismissSuggestionsAsync(w.Scope, w.Location.Id, ManagerId)).IsSuccess);
        Assert.IsNull((await service.GetOpenSuggestionsAsync(w.Scope, w.Location.Id)).Data);
    }

    [TestMethod]
    public async Task RunMonthlyAsync_TemplatesMatchTheRota_NoBanner()
    {
        World w = await SeedAsync();
        TemplateLearningService service = GetService(w.Options);
        TemplateLearningResult learned = (await service.LearnAsync(w.Scope, w.Location.Id, 8)).Data!;
        await service.ApplyAsync(w.Scope, w.Location.Id, learned.Templates, learned.HireStaffing, ManagerId);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            Assert.AreEqual(0, (await service.RunMonthlyAsync(ctx, w.Location, Now))!.Suggestions);
        }

        Assert.IsNull((await service.GetOpenSuggestionsAsync(w.Scope, w.Location.Id)).Data);
    }
}
