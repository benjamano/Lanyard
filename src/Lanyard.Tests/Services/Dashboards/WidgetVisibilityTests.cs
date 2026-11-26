using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Dashboards;

// "Show only when…" on dashboard widgets: the automation conditions, judged for the dashboard's
// company at venue-local time.
[TestClass]
public class WidgetVisibilityTests
{
    private const int CompanyId = 1;

    // Thursday 19 Nov 2026 (GMT, so UTC = venue time).
    private static DateTime At(int hour, int minute = 0) => new(2026, 11, 19, hour, minute, 0, DateTimeKind.Utc);

    private static readonly Guid LaserKiosk = Guid.NewGuid();
    private static readonly Guid OtherCompanyKiosk = Guid.NewGuid();

    private static readonly WidgetVisibilityLookups Lookups = new(
        new Dictionary<Guid, AutomationClientInfo>
        {
            [LaserKiosk] = new(CompanyId, "Laser"),
            [OtherCompanyKiosk] = new(2, "Someone else's")
        },
        new Dictionary<int, AutomationLocationInfo>
        {
            [10] = new(CompanyId, "Ipswich", [new LocationOpeningHours { LocationId = 10, DayOfWeek = DayOfWeek.Thursday, OpensAt = new TimeOnly(10, 0), ClosesAt = new TimeOnly(18, 0), IsActive = true }])
        });

    private static DashboardWidget Widget(params AutomationRuleCondition[] conditions) => new TextAreaWidget
    {
        ShowConditionsJson = WidgetShowConditions.Serialize(conditions.Select((c, i) => { c.SortOrder = i; c.IsActive = true; return c; }))
    };

    private static AutomationRuleCondition Condition<T>(AutomationConditionType type, T parameters) where T : class => new()
    {
        ConditionType = type,
        ParametersJson = AutomationConditionParameters.Serialize(parameters)
    };

    private static AutomationRuleCondition Between(int from, int until) =>
        Condition(AutomationConditionType.TimeWindow, new TimeWindowConditionParameters(new TimeOnly(from, 0), new TimeOnly(until, 0)));

    private static bool Shown(DashboardWidget widget, DateTime now, bool laserInGame = false) =>
        WidgetVisibility.IsShown(widget, CompanyId, Lookups, now, id => laserInGame && id == LaserKiosk);

    [TestMethod]
    public void NoConditions_AlwaysShown()
    {
        Assert.IsTrue(Shown(new TextAreaWidget(), At(3)));
    }

    [TestMethod]
    public void TimeWindow_ShownOnlyInside_IncludingPastMidnight()
    {
        DashboardWidget evening = Widget(Between(18, 23));
        Assert.IsFalse(Shown(evening, At(17, 59)));
        Assert.IsTrue(Shown(evening, At(18, 0)));
        Assert.IsTrue(Shown(evening, At(22, 30)));

        DashboardWidget lateNight = Widget(Between(22, 2));
        Assert.IsTrue(Shown(lateNight, At(23, 30)));
        Assert.IsTrue(Shown(lateNight, At(1, 0)));
        Assert.IsFalse(Shown(lateNight, At(12, 0)));
    }

    [TestMethod]
    public void AllConditionsMustHold()
    {
        DashboardWidget thursdayEvening = Widget(
            Between(18, 23),
            Condition(AutomationConditionType.DaysOfWeek, new DaysOfWeekConditionParameters(AutomationScheduleDays.Serialize([DayOfWeek.Thursday]))));
        DashboardWidget fridayEvening = Widget(
            Between(18, 23),
            Condition(AutomationConditionType.DaysOfWeek, new DaysOfWeekConditionParameters(AutomationScheduleDays.Serialize([DayOfWeek.Friday]))));

        Assert.IsTrue(Shown(thursdayEvening, At(19)));
        Assert.IsFalse(Shown(fridayEvening, At(19)));
    }

    [TestMethod]
    public void LocationClosed_And_KioskInGame()
    {
        DashboardWidget whenClosed = Widget(Condition(AutomationConditionType.DuringOpeningHours, new OpeningHoursConditionParameters(10, false)));
        Assert.IsFalse(Shown(whenClosed, At(12)));
        Assert.IsTrue(Shown(whenClosed, At(18, 30)));

        DashboardWidget betweenGames = Widget(Condition(AutomationConditionType.ClientGameStatus, new ClientGameStatusConditionParameters(LaserKiosk, false)));
        Assert.IsTrue(Shown(betweenGames, At(12)));
        Assert.IsFalse(Shown(betweenGames, At(12), laserInGame: true));
    }

    [TestMethod]
    public void AnotherCompanysKiosk_OrAMissingLocation_HidesTheWidget()
    {
        Assert.IsFalse(Shown(Widget(Condition(AutomationConditionType.ClientGameStatus, new ClientGameStatusConditionParameters(OtherCompanyKiosk, false))), At(12)));
        Assert.IsFalse(Shown(Widget(Condition(AutomationConditionType.DuringOpeningHours, new OpeningHoursConditionParameters(99, true))), At(12)));
    }

    [TestMethod]
    public void UnreadableSettings_HideTheWidget()
    {
        Assert.IsFalse(Shown(new TextAreaWidget { ShowConditionsJson = "{not json" }, At(12)));
    }

    [TestMethod]
    public void Serialize_RoundTrips_AndNoConditionsStoresNull()
    {
        AutomationRuleCondition window = Between(18, 23);
        window.IsActive = true;

        string? json = WidgetShowConditions.Serialize([window]);
        List<AutomationRuleCondition> back = WidgetShowConditions.Parse(json);

        Assert.AreEqual(1, back.Count);
        Assert.AreEqual(AutomationConditionType.TimeWindow, back[0].ConditionType);
        Assert.AreEqual(window.ParametersJson, back[0].ParametersJson);
        Assert.IsNull(WidgetShowConditions.Serialize([]));
        Assert.AreEqual("18:00–23:00", WidgetShowConditions.Summary(json));
    }

    [TestMethod]
    public async Task LoadAsync_CarriesTodaysClosedHoliday_SoAnOpenWidgetIsHidden()
    {
        DbContextOptions<ApplicationDbContext> options = Lanyard.Tests.Services.Scheduling.SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await Lanyard.Tests.Services.Scheduling.SchedulingTestHelpers.SeedCompanyAsync(options);
        DateOnly today = RotaTime.Today(DateTime.UtcNow);

        await using (ApplicationDbContext ctx = new(options))
        {
            foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
            {
                ctx.LocationOpeningHours.Add(new LocationOpeningHours { LocationId = location.Id, DayOfWeek = day, OpensAt = new TimeOnly(0, 0), ClosesAt = new TimeOnly(23, 59), IsActive = true });
            }

            ctx.RotaHolidays.Add(new RotaHoliday
            {
                Id = Guid.NewGuid(), CompanyId = company.Id, LocationId = location.Id, Name = "Refit",
                Kind = RotaHolidayKind.Closed, StartDate = today, EndDate = today
            });
            await ctx.SaveChangesAsync();
        }

        WidgetVisibilityService service = new(Lanyard.Tests.Services.Scheduling.SchedulingTestHelpers.GetFactory(options), new LaserGameStatusStore(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WidgetVisibilityService>.Instance);
        DashboardWidget whileOpen = Widget(Condition(AutomationConditionType.DuringOpeningHours, new OpeningHoursConditionParameters(location.Id, true)));

        WidgetVisibilityLookups lookups = (await service.LoadAsync([whileOpen])).Data!;

        Assert.AreEqual("Refit", lookups.Locations[location.Id].HolidayList.Single().Name);
        Assert.IsFalse(AutomationConditionEvaluator.Evaluate(
            WidgetShowConditions.Parse(whileOpen.ShowConditionsJson), company.Id, RotaTime.ToUtc(today, new TimeOnly(12, 0)),
            _ => null, _ => false, id => lookups.Locations.GetValueOrDefault(id)).Passed);
    }

    [TestMethod]
    public async Task DashboardSave_KeepsConditions_OnNewAndExistingWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));
        Mock<ISecurityService> security = new();
        security.Setup(x => x.IsCurrentUserInRoleAsync(It.IsAny<string>())).ReturnsAsync(true);
        DashboardService service = new(factory.Object, security.Object);

        Dashboard dashboard = new() { Id = Guid.NewGuid(), Name = "Laser", IsActive = true, CreateDate = DateTime.UtcNow };
        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Dashboards.Add(dashboard);
            await ctx.SaveChangesAsync();
        }

        string? evening = WidgetShowConditions.Serialize([Between(18, 23)]);
        TextAreaWidget widget = new() { ShowConditionsJson = evening, GridW = 2, GridH = 2 };
        dashboard.Widgets = [widget];

        Assert.IsTrue((await service.SaveDashboardAsync(dashboard)).IsSuccess);

        widget.ShowConditionsJson = null;
        Assert.IsTrue((await service.SaveDashboardAsync(dashboard)).IsSuccess, "Clearing the conditions is saved too.");

        await using (ApplicationDbContext ctx = new(options))
        {
            Assert.IsNull((await ctx.DashboardWidgets.SingleAsync()).ShowConditionsJson);
        }

        widget.ShowConditionsJson = evening;
        await service.SaveDashboardAsync(dashboard);

        await using (ApplicationDbContext ctx = new(options))
        {
            Assert.AreEqual(evening, (await ctx.DashboardWidgets.SingleAsync()).ShowConditionsJson);
        }
    }
}
