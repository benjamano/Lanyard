using Lanyard.Application.Services.Parties;
using Lanyard.Application.Services.Projection.Bindings;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Projection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Projection.Bindings;

/// <summary>
/// Party live text: which party a screen picks, the words it shows, the company's name switch,
/// and that one room's party is never served to another room's screen.
/// </summary>
[TestClass]
public class PartyBindingSourceTests
{
    private const int CompanyId = 1;
    private const int LocationId = 10;

    // November, so UK time == UTC and the times read naturally.
    private static readonly DateTime Now = new(2026, 11, 21, 14, 0, 0, DateTimeKind.Utc);

    private static PartySlot Party(string name, DateTime start, string? room = null, int? age = 7, DateTime? laser = null, DateTime? eat = null, DateTime? booked = null) =>
        new(Guid.NewGuid(), LocationId, start, start.AddHours(2), eat, laser, room, name, age, booked ?? new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

    // --- Which party ---

    [TestMethod]
    public void Choose_ShowsFrom15MinutesBeforeTheStartUntilTheEnd()
    {
        PartySlot party = Party("Ava", Now);

        Assert.IsNull(PartyScreen.Choose([party], null, Now.AddMinutes(-16)));
        Assert.AreEqual(party, PartyScreen.Choose([party], null, Now.AddMinutes(-15)));
        Assert.AreEqual(party, PartyScreen.Choose([party], null, Now.AddMinutes(119)));
        Assert.IsNull(PartyScreen.Choose([party], null, Now.AddHours(2)));
    }

    [TestMethod]
    public void Choose_PrefersTheKiosksRoom_ElseFallsBackToTheLocation()
    {
        PartySlot room1 = Party("Leo", Now.AddMinutes(-30), room: "Room 1");
        PartySlot room2 = Party("Ava", Now.AddMinutes(-60), room: "Room 2");

        Assert.AreEqual(room2, PartyScreen.Choose([room1, room2], " room 2", Now));
        Assert.AreEqual(room1, PartyScreen.Choose([room1, room2], "Room 1", Now));
        Assert.AreEqual(room1, PartyScreen.Choose([room1, room2], "Room 9", Now), "Nothing in Room 9: the location's most recent party.");
        Assert.AreEqual(room1, PartyScreen.Choose([room1, room2], null, Now));
    }

    [TestMethod]
    public void Choose_MostRecentlyStarted_ElseTheNextToStart_TiesToTheFirstBooked()
    {
        PartySlot earlier = Party("Mia", Now.AddMinutes(-90));
        PartySlot later = Party("Noah", Now.AddMinutes(-10));
        Assert.AreEqual(later, PartyScreen.Choose([earlier, later], null, Now));

        PartySlot soon = Party("Zoe", Now.AddMinutes(5));
        PartySlot sooner = Party("Eli", Now.AddMinutes(2));
        Assert.AreEqual(sooner, PartyScreen.Choose([soon, sooner], null, Now));

        PartySlot bookedFirst = Party("Ivy", Now.AddMinutes(-5), booked: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        PartySlot bookedSecond = Party("Kai", Now.AddMinutes(-5), booked: new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc));
        Assert.AreEqual(bookedFirst, PartyScreen.Choose([bookedSecond, bookedFirst], null, Now));
    }

    // --- Words ---

    [TestMethod]
    [DataRow("Ava Smith", "Ava")]
    [DataRow("  ava  ", "ava")]
    [DataRow("Ava and Leo Smith", "Ava & Leo")]
    [DataRow("Ava & Leo", "Ava & Leo")]
    [DataRow("Ava + Leo", "Ava & Leo")]
    [DataRow("Ava\nSmith", "Ava")]
    [DataRow("Andrew Jones", "Andrew")]
    [DataRow("", null)]
    [DataRow("   ", null)]
    public void FirstName_KeepsOnlyFirstNames(string input, string? expected)
    {
        Assert.AreEqual(expected, PartyScreen.FirstName(input));
    }

    [TestMethod]
    public void FirstName_IsCappedAt40Characters()
    {
        Assert.AreEqual(40, PartyScreen.FirstName(new string('A', 60))!.Length);
    }

    [TestMethod]
    public void Welcome_WithAndWithoutAnAge()
    {
        Assert.AreEqual("Happy 7th birthday, Ava!", PartyScreen.Welcome(Party("Ava Smith", Now)));
        Assert.AreEqual("Happy 1st birthday, Ava!", PartyScreen.Welcome(Party("Ava", Now, age: 1)));
        Assert.AreEqual("Happy 12th birthday, Ava!", PartyScreen.Welcome(Party("Ava", Now, age: 12)));
        Assert.AreEqual("Happy 22nd birthday, Ava!", PartyScreen.Welcome(Party("Ava", Now, age: 22)));
        Assert.AreEqual("Happy birthday, Ava!", PartyScreen.Welcome(Party("Ava", Now, age: null)));
        Assert.IsNull(PartyScreen.Welcome(Party("", Now)));
    }

    [TestMethod]
    public void NextUp_IsTheNextLaserOrFoodTimeStillToCome()
    {
        PartySlot party = Party("Ava", Now.AddHours(-1), laser: Now.AddMinutes(30), eat: Now.AddMinutes(60));

        Assert.AreEqual("Laser tag at 14:30", PartyScreen.NextUp(party, Now));
        Assert.AreEqual("Food at 15:00", PartyScreen.NextUp(party, Now.AddMinutes(31)));
        Assert.IsNull(PartyScreen.NextUp(party, Now.AddMinutes(61)));
        Assert.IsNull(PartyScreen.NextUp(Party("Ava", Now), Now), "A party with neither time.");
    }

    // --- Sources ---

    private sealed class FakeSchedule(params PartySlot[] parties) : IPartyScheduleProvider
    {
        public Task<Result<IReadOnlyList<PartySlot>>> GetDayAsync(int companyId, int locationId, DateTime nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<PartySlot>>.Ok(companyId == CompanyId && locationId == LocationId ? parties : []));

        public void Invalidate(int locationId)
        {
        }
    }

    private static ISystemDbContextFactory CompanyWithSwitch(bool showNames)
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        using (ApplicationDbContext ctx = new(options))
        {
            ctx.Companies.Add(new Company { Id = CompanyId, Name = "Play2Day", IsActive = true, ShowPartyNamesOnScreens = showNames });
            ctx.SaveChanges();
        }

        Mock<ISystemDbContextFactory> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));
        return factory.Object;
    }

    private static ProjectionBindingContext Context(string? room = null, int companyId = CompanyId, int? locationId = LocationId) =>
        new(companyId, locationId, Guid.NewGuid(), Now, room);

    [TestMethod]
    public async Task Welcome_ShowsTheNameOnlyWhenTheCompanyAllowsIt()
    {
        FakeSchedule schedule = new(Party("Ava Smith", Now.AddMinutes(-10), laser: Now.AddMinutes(30)));

        Result<string?> on = await new PartyWelcomeBindingSource(schedule, CompanyWithSwitch(true)).ResolveAsync(Context(), CancellationToken.None);
        Result<string?> off = await new PartyWelcomeBindingSource(schedule, CompanyWithSwitch(false)).ResolveAsync(Context(), CancellationToken.None);
        Result<string?> nameOff = await new PartyChildNameBindingSource(schedule, CompanyWithSwitch(false)).ResolveAsync(Context(), CancellationToken.None);
        Result<string?> nextUpOff = await new PartyNextUpBindingSource(schedule, CompanyWithSwitch(false)).ResolveAsync(Context(), CancellationToken.None);

        Assert.AreEqual("Happy 7th birthday, Ava!", on.Data);
        Assert.IsTrue(off.IsSuccess);
        Assert.IsNull(off.Data, "Switch off: the fallback shows instead of a name.");
        Assert.IsNull(nameOff.Data);
        Assert.AreEqual("Laser tag at 14:30", nextUpOff.Data, "What's next has no name, so it shows either way.");
    }

    [TestMethod]
    public async Task ChildName_IsTheFirstNameOfTheRoomsParty()
    {
        FakeSchedule schedule = new(Party("Leo Jones", Now.AddMinutes(-5), room: "Room 1"), Party("Ava Smith", Now.AddMinutes(-60), room: "Room 2"));
        PartyChildNameBindingSource source = new(schedule, CompanyWithSwitch(true));

        Assert.AreEqual("Ava", (await source.ResolveAsync(Context("Room 2"), CancellationToken.None)).Data);
        Assert.AreEqual("Leo", (await source.ResolveAsync(Context("Room 1"), CancellationToken.None)).Data);
    }

    [TestMethod]
    public async Task NoLocation_NoParty_OrAnotherCompany_ShowTheFallback()
    {
        FakeSchedule schedule = new(Party("Ava", Now.AddMinutes(-5)));
        PartyWelcomeBindingSource source = new(schedule, CompanyWithSwitch(true));

        Assert.IsNull((await source.ResolveAsync(Context(locationId: null), CancellationToken.None)).Data);
        Assert.IsNull((await source.ResolveAsync(Context(companyId: 2), CancellationToken.None)).Data);
        Assert.IsNull((await new PartyWelcomeBindingSource(new FakeSchedule(), CompanyWithSwitch(true)).ResolveAsync(Context(), CancellationToken.None)).Data);
    }

    [TestMethod]
    public void EveryPartyKey_IsInTheCatalogue_WithAFallbackThatHasNoName()
    {
        foreach (string key in new[] { ProjectionBindingKeys.PartyWelcome, ProjectionBindingKeys.PartyChildName, ProjectionBindingKeys.PartyNextUp })
        {
            ProjectionBindingDescriptor? descriptor = ProjectionBindingCatalogue.Find(key);
            Assert.IsNotNull(descriptor, key);
            Assert.IsFalse(string.IsNullOrWhiteSpace(descriptor.DefaultFallback));
        }
    }

    [TestMethod]
    public async Task Resolver_NeverServesOneRoomsValueToAnotherRoomsScreen()
    {
        FakeSchedule schedule = new(Party("Leo Jones", Now.AddMinutes(-5), room: "Room 1"), Party("Ava Smith", Now.AddMinutes(-60), room: "Room 2"));
        ProjectionBindingResolver resolver = new([new PartyChildNameBindingSource(schedule, CompanyWithSwitch(true))], Mock.Of<ILogger<ProjectionBindingResolver>>())
        {
            CacheDuration = TimeSpan.FromMinutes(10)
        };

        Guid valueId = Guid.NewGuid();
        ProjectionProgramStep step = new()
        {
            Id = Guid.NewGuid(),
            Template = new ProjectionProgramStepTemplate { Name = ProjectionTemplateNames.ShowText },
            ParameterValues =
            [
                new ProjectionProgramParameterValue
                {
                    Id = valueId,
                    Parameter = new ProjectionProgramStepTemplateParameter { TemplateId = Guid.Empty, Name = ProjectionParameterNames.Text, DataType = "String" },
                    BindingKey = ProjectionBindingKeys.PartyChildName,
                    Value = "Birthday star"
                }
            ]
        };

        IReadOnlyDictionary<Guid, string> room2 = await resolver.ResolveStepAsync(Context("Room 2"), step, CancellationToken.None);
        IReadOnlyDictionary<Guid, string> room1 = await resolver.ResolveStepAsync(Context("Room 1"), step, CancellationToken.None);
        IReadOnlyDictionary<Guid, string> room2Again = await resolver.ResolveStepAsync(Context("room 2 "), step, CancellationToken.None);

        Assert.AreEqual("Ava", room2[valueId]);
        Assert.AreEqual("Leo", room1[valueId], "Room 1 must not get Room 2's cached name.");
        Assert.AreEqual("Ava", room2Again[valueId]);
    }
}
