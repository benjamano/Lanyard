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
public class TimeOffServiceTests
{
    // Thursday 24 September 2026, midday. Holiday year 2026/27 runs 6 April 2026 - 5 April 2027.
    private static readonly DateTime Now = new(2026, 9, 24, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 9, 24);
    private const string Manager = "manager-user";

    private sealed record Setup(
        DbContextOptions<ApplicationDbContext> Options,
        Company Company,
        Location Location,
        UserProfile User,
        TimeOffService Service,
        TimeOffPolicyService Policy,
        TimeOffType Holiday,
        TimeOffType Unpaid,
        TimeOffType Sickness);

    private static async Task<Setup> SetupAsync(decimal? holidayHours = 224m)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");

        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        TimeOffPolicyService policy = new(factory, NullLogger<TimeOffPolicyService>.Instance);
        TimeOffService service = new(factory, new SchedulingSettingsService(factory), new TimeOffEventBus(), new TestClock(Now), NullLogger<TimeOffService>.Instance);

        List<TimeOffType> types = (await policy.GetTypesAsync(company.Id)).Data!;
        TimeOffType holiday = types.Single(x => x.Name == "Paid holiday");

        if (holidayHours is decimal hours)
        {
            await policy.SaveAllowanceAsync(SchedulingTestHelpers.AdminScope, new TimeOffAllowance
            {
                CompanyId = company.Id,
                TimeOffTypeId = holiday.Id,
                AllowanceHours = hours
            });
        }

        return new Setup(options, company, location, user, service, policy, holiday,
            types.Single(x => x.Name == "Unpaid leave"), types.Single(x => x.Name == "Sickness"));
    }

    private static TimeOffRequestDraft Draft(TimeOffType type, DateOnly start, DateOnly end, decimal hours, string? notes = null) =>
        new(type.Id, start, end, hours, notes);

    private static async Task<TimeOffRequest> SeedRequestAsync(Setup setup, TimeOffType type, DateOnly start, DateOnly end, decimal hours, TimeOffStatus status, string? userId = null)
    {
        await using ApplicationDbContext ctx = new(setup.Options);

        TimeOffRequest request = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? setup.User.Id,
            TimeOffTypeId = type.Id,
            LocationId = setup.Location.Id,
            StartDate = start,
            EndDate = end,
            Hours = hours,
            Status = status,
            RequestedDateUtc = Now.AddDays(-7)
        };

        ctx.TimeOffRequests.Add(request);
        await ctx.SaveChangesAsync();

        return request;
    }

    [TestMethod]
    public void HolidayYear_StartsOnTheSixthOfApril()
    {
        HolidayYear before = HolidayYear.For(4, 6, new DateOnly(2027, 4, 5));
        HolidayYear on = HolidayYear.For(4, 6, new DateOnly(2027, 4, 6));

        Assert.AreEqual(new DateOnly(2026, 4, 6), before.Start);
        Assert.AreEqual(new DateOnly(2027, 4, 5), before.End);
        Assert.AreEqual("2026/27", before.Label);
        Assert.AreEqual(new DateOnly(2027, 4, 6), on.Start);
        Assert.AreEqual("2027", HolidayYear.For(1, 1, new DateOnly(2027, 6, 1)).Label);
    }

    [TestMethod]
    public async Task GetBalancesAsync_TakesOffApprovedAndShowsPendingSeparately()
    {
        Setup setup = await SetupAsync();
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 8, 3), new(2026, 8, 7), 40, TimeOffStatus.Approved);
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 11, 2), new(2026, 11, 2), 8, TimeOffStatus.Pending);
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 12, 1), new(2026, 12, 1), 8, TimeOffStatus.Rejected);
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 3, 30), new(2026, 3, 31), 16, TimeOffStatus.Approved); // last holiday year
        await SeedRequestAsync(setup, setup.Sickness, new(2026, 9, 1), new(2026, 9, 1), 8, TimeOffStatus.Approved);

        Result<TimeOffBalances> result = await setup.Service.GetBalancesAsync(setup.User.Id, setup.Company.Id, Today);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(new DateOnly(2026, 4, 6), result.Data!.Year.Start);
        Assert.IsFalse(result.Data.Balances.Any(b => b.Type.Id == setup.Sickness.Id), "Sickness never counts against an allowance.");

        TimeOffBalance holiday = result.Data.Balances.Single(b => b.Type.Id == setup.Holiday.Id);
        Assert.AreEqual(40m, holiday.ApprovedHours);
        Assert.AreEqual(8m, holiday.PendingHours);
        Assert.AreEqual(184m, holiday.RemainingHours);
        Assert.AreEqual(176m, holiday.RemainingAfterPendingHours);
    }

    [TestMethod]
    public async Task SubmitAsync_SavesAPendingRequest()
    {
        Setup setup = await SetupAsync();

        Result<TimeOffSubmitResult> result = await setup.Service.SubmitAsync(setup.User.Id, setup.Location.Id,
            Draft(setup.Holiday, new(2026, 10, 12), new(2026, 10, 14), 24, "  Family wedding "));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(TimeOffStatus.Pending, result.Data!.Request.Status);
        Assert.AreEqual("Family wedding", result.Data.Request.Notes);
        Assert.AreEqual(setup.User.Id, result.Data.Request.RequestedByUserId);
        Assert.AreEqual(0, result.Data.Warnings.Count);
    }

    [TestMethod]
    public async Task SubmitAsync_RefusesAStartInThePastButAManagerCanRecordIt()
    {
        Setup setup = await SetupAsync();
        TimeOffRequestDraft yesterday = Draft(setup.Sickness, Today.AddDays(-1), Today.AddDays(-1), 8);

        Result<TimeOffSubmitResult> staff = await setup.Service.SubmitAsync(setup.User.Id, setup.Location.Id, yesterday);
        Result<TimeOffSubmitResult> manager = await setup.Service.RecordForUserAsync(
            SchedulingTestHelpers.ManagerScopeFor(setup.Location), setup.User.Id, setup.Location.Id, yesterday, Manager);

        Assert.IsFalse(staff.IsSuccess);
        Assert.IsTrue(manager.IsSuccess, manager.Error);
        Assert.AreEqual(TimeOffStatus.Approved, manager.Data!.Request.Status);
        Assert.AreEqual(Manager, manager.Data.Request.DecidedByUserId);
    }

    [TestMethod]
    public async Task RecordForUserAsync_RefusesAManagerAtAnotherLocation()
    {
        Setup setup = await SetupAsync();
        (_, Location elsewhere) = await SchedulingTestHelpers.SeedCompanyAsync(setup.Options, "Other Co", "Elsewhere");

        Result<TimeOffSubmitResult> result = await setup.Service.RecordForUserAsync(
            SchedulingTestHelpers.ManagerScopeFor(elsewhere), setup.User.Id, setup.Location.Id, Draft(setup.Sickness, Today, Today, 8), Manager);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SubmitAsync_RefusesOverlapWithALiveRequestButNotACancelledOne()
    {
        Setup setup = await SetupAsync();
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 12), new(2026, 10, 16), 40, TimeOffStatus.Cancelled);
        await SeedRequestAsync(setup, setup.Unpaid, new(2026, 10, 20), new(2026, 10, 20), 8, TimeOffStatus.Approved);

        Result<TimeOffSubmitResult> overCancelled = await setup.Service.SubmitAsync(setup.User.Id, setup.Location.Id,
            Draft(setup.Holiday, new(2026, 10, 13), new(2026, 10, 13), 8));
        Result<TimeOffSubmitResult> overApproved = await setup.Service.SubmitAsync(setup.User.Id, setup.Location.Id,
            Draft(setup.Holiday, new(2026, 10, 19), new(2026, 10, 21), 24));

        Assert.IsTrue(overCancelled.IsSuccess, overCancelled.Error);
        Assert.IsFalse(overApproved.IsSuccess);
        StringAssert.Contains(overApproved.Error, "already have time off booked");
    }

    [TestMethod]
    public async Task SubmitAsync_RefusesARequestThatRunsIntoTheNextHolidayYear()
    {
        Setup setup = await SetupAsync();

        Result<TimeOffSubmitResult> result = await setup.Service.SubmitAsync(setup.User.Id, setup.Location.Id,
            Draft(setup.Holiday, new(2027, 4, 1), new(2027, 4, 9), 56));

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "6 April");
    }

    [TestMethod]
    public async Task SubmitAsync_RefusesMoreHoursThanTheDaysHold()
    {
        Setup setup = await SetupAsync();

        Result<TimeOffSubmitResult> result = await setup.Service.SubmitAsync(setup.User.Id, setup.Location.Id,
            Draft(setup.Holiday, new(2026, 10, 12), new(2026, 10, 12), 25));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SubmitAsync_RefusesSomeoneWhoIsNotAMemberOfTheLocation()
    {
        Setup setup = await SetupAsync();
        Location other = new() { CompanyId = setup.Company.Id, Name = "Wisbech", IsActive = true };

        await using (ApplicationDbContext ctx = new(setup.Options))
        {
            ctx.Locations.Add(other);
            await ctx.SaveChangesAsync();
        }

        Result<TimeOffSubmitResult> result = await setup.Service.SubmitAsync(setup.User.Id, other.Id,
            Draft(setup.Holiday, new(2026, 10, 12), new(2026, 10, 12), 8));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SubmitAsync_WarnsButStillSavesWhenItGoesOverTheAllowance()
    {
        Setup setup = await SetupAsync(holidayHours: 16m);
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 1), new(2026, 10, 1), 8, TimeOffStatus.Approved);

        Result<TimeOffSubmitResult> result = await setup.Service.SubmitAsync(setup.User.Id, setup.Location.Id,
            Draft(setup.Holiday, new(2026, 10, 12), new(2026, 10, 13), 16));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(1, result.Data!.Warnings.Count);
        StringAssert.Contains(result.Data.Warnings[0], "1 day (8 h) more than you have left");
    }

    [TestMethod]
    public async Task SubmitAsync_NoAllowanceWarningWhenUnlimited()
    {
        Setup setup = await SetupAsync();
        await setup.Policy.SaveAllowanceAsync(SchedulingTestHelpers.AdminScope, new TimeOffAllowance
        {
            CompanyId = setup.Company.Id,
            TimeOffTypeId = setup.Unpaid.Id,
            IsUnlimited = true
        });

        Result<TimeOffSubmitResult> result = await setup.Service.SubmitAsync(setup.User.Id, setup.Location.Id,
            Draft(setup.Unpaid, new(2026, 10, 12), new(2026, 10, 30), 152));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(0, result.Data!.Warnings.Count);
    }

    [TestMethod]
    public async Task SubmitAsync_WarnsWhenNoAllowanceIsConfigured()
    {
        Setup setup = await SetupAsync(holidayHours: null);

        Result<TimeOffSubmitResult> result = await setup.Service.SubmitAsync(setup.User.Id, setup.Location.Id,
            Draft(setup.Holiday, new(2026, 10, 12), new(2026, 10, 12), 8));

        Assert.IsTrue(result.IsSuccess, result.Error);
        StringAssert.Contains(result.Data!.Warnings.Single(), "No paid holiday allowance");
    }

    [TestMethod]
    public async Task SubmitAsync_WarnsWhenThePersonIsAlreadyOnThePublishedRota()
    {
        Setup setup = await SetupAsync();
        DateOnly saturday = new(2026, 10, 17);

        await using (ApplicationDbContext ctx = new(setup.Options))
        {
            ctx.Shifts.Add(new Shift
            {
                Id = Guid.NewGuid(),
                LocationId = setup.Location.Id,
                UserId = setup.User.Id,
                StartUtc = RotaTime.ToUtc(saturday, new TimeOnly(9, 0)),
                EndUtc = RotaTime.ToUtc(saturday, new TimeOnly(17, 0)),
                PublishedDateUtc = Now.AddDays(-2),
                IsActive = true,
                CreateDate = Now.AddDays(-3),
                CreateByUserId = Manager
            });
            await ctx.SaveChangesAsync();
        }

        Result<TimeOffSubmitResult> result = await setup.Service.SubmitAsync(setup.User.Id, setup.Location.Id,
            Draft(setup.Holiday, new(2026, 10, 16), new(2026, 10, 18), 24));

        Assert.IsTrue(result.IsSuccess, result.Error);
        StringAssert.Contains(result.Data!.Warnings.Single(), "Sat 17 Oct");
    }

    [TestMethod]
    public async Task PreviewAsync_DefaultsHoursFromTheCompanyDay()
    {
        Setup setup = await SetupAsync();

        await using (ApplicationDbContext ctx = new(setup.Options))
        {
            ctx.CompanySchedulingSettings.Add(new CompanySchedulingSettings { Id = Guid.NewGuid(), CompanyId = setup.Company.Id, HoursPerDay = 7.5m });
            await ctx.SaveChangesAsync();
        }

        Result<TimeOffPreview> result = await setup.Service.PreviewAsync(setup.User.Id, setup.Company.Id,
            Draft(setup.Holiday, new(2026, 10, 12), new(2026, 10, 15), 30));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(30m, result.Data!.DefaultHours);
        Assert.AreEqual(7.5m, result.Data.HoursPerDay);
    }

    [TestMethod]
    public async Task CancelAsync_AllowsPendingAndFutureApprovedOnly()
    {
        Setup setup = await SetupAsync();
        TimeOffRequest pending = await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 1), new(2026, 10, 1), 8, TimeOffStatus.Pending);
        TimeOffRequest future = await SeedRequestAsync(setup, setup.Holiday, new(2026, 11, 1), new(2026, 11, 1), 8, TimeOffStatus.Approved);
        TimeOffRequest started = await SeedRequestAsync(setup, setup.Holiday, Today, Today.AddDays(2), 24, TimeOffStatus.Approved);

        Assert.IsTrue((await setup.Service.CancelAsync(setup.User.Id, pending.Id)).IsSuccess);
        Assert.IsTrue((await setup.Service.CancelAsync(setup.User.Id, future.Id)).IsSuccess);
        Assert.IsFalse((await setup.Service.CancelAsync(setup.User.Id, started.Id)).IsSuccess);

        await using ApplicationDbContext ctx = new(setup.Options);
        Assert.AreEqual(TimeOffStatus.Cancelled, (await ctx.TimeOffRequests.FindAsync(pending.Id))!.Status);
        Assert.AreEqual(TimeOffStatus.Approved, (await ctx.TimeOffRequests.FindAsync(started.Id))!.Status);
    }

    [TestMethod]
    public async Task CancelAsync_RefusesSomeoneElsesRequest()
    {
        Setup setup = await SetupAsync();
        TimeOffRequest pending = await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 1), new(2026, 10, 1), 8, TimeOffStatus.Pending);

        Assert.IsFalse((await setup.Service.CancelAsync("someone-else", pending.Id)).IsSuccess);
    }

    [TestMethod]
    public async Task DecideAsync_ApprovesAndRecordsWhoDecided()
    {
        Setup setup = await SetupAsync();
        TimeOffRequest pending = await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 1), new(2026, 10, 1), 8, TimeOffStatus.Pending);

        Result<TimeOffRequest> result = await setup.Service.DecideAsync(SchedulingTestHelpers.ManagerScopeFor(setup.Location), pending.Id, true, null, Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(TimeOffStatus.Approved, result.Data!.Status);
        Assert.AreEqual(Manager, result.Data.DecidedByUserId);
        Assert.AreEqual(Now, result.Data.DecidedDateUtc);
    }

    [TestMethod]
    public async Task DecideAsync_RejectingNeedsAReason()
    {
        Setup setup = await SetupAsync();
        TimeOffRequest pending = await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 1), new(2026, 10, 1), 8, TimeOffStatus.Pending);
        LocationScope scope = SchedulingTestHelpers.ManagerScopeFor(setup.Location);

        Result<TimeOffRequest> noReason = await setup.Service.DecideAsync(scope, pending.Id, false, "  ", Manager);
        Result<TimeOffRequest> withReason = await setup.Service.DecideAsync(scope, pending.Id, false, "Short-staffed that day", Manager);

        Assert.IsFalse(noReason.IsSuccess);
        Assert.IsTrue(withReason.IsSuccess, withReason.Error);
        Assert.AreEqual("Short-staffed that day", withReason.Data!.DecisionReason);
    }

    [TestMethod]
    public async Task DecideAsync_CanWithdrawAnApprovalButNotApproveTwice()
    {
        Setup setup = await SetupAsync();
        TimeOffRequest approved = await SeedRequestAsync(setup, setup.Holiday, new(2026, 11, 1), new(2026, 11, 1), 8, TimeOffStatus.Approved);
        LocationScope scope = SchedulingTestHelpers.ManagerScopeFor(setup.Location);

        Assert.IsFalse((await setup.Service.DecideAsync(scope, approved.Id, true, null, Manager)).IsSuccess);

        Result<TimeOffRequest> withdrawn = await setup.Service.DecideAsync(scope, approved.Id, false, "Party booked, sorry", Manager);

        Assert.IsTrue(withdrawn.IsSuccess, withdrawn.Error);
        Assert.AreEqual(TimeOffStatus.Rejected, withdrawn.Data!.Status);
    }

    [TestMethod]
    public async Task DecideAsync_RefusesAManagerAtAnotherLocation()
    {
        Setup setup = await SetupAsync();
        (_, Location elsewhere) = await SchedulingTestHelpers.SeedCompanyAsync(setup.Options, "Other Co", "Elsewhere");
        TimeOffRequest pending = await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 1), new(2026, 10, 1), 8, TimeOffStatus.Pending);

        Result<TimeOffRequest> result = await setup.Service.DecideAsync(SchedulingTestHelpers.ManagerScopeFor(elsewhere), pending.Id, true, null, Manager);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetRequestsForLocationAsync_ShowsOverAllowanceWarningAndWhoElseIsOff()
    {
        Setup setup = await SetupAsync(holidayHours: 8m);
        UserProfile colleague = await SchedulingTestHelpers.SeedUserAsync(setup.Options, setup.Location, "Tom");
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 12), new(2026, 10, 13), 16, TimeOffStatus.Pending);
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 13), new(2026, 10, 13), 8, TimeOffStatus.Approved, colleague.Id);

        Result<List<TimeOffRequestView>> result = await setup.Service.GetRequestsForLocationAsync(
            SchedulingTestHelpers.ManagerScopeFor(setup.Location), setup.Location.Id, TimeOffListFilter.Pending);

        Assert.IsTrue(result.IsSuccess, result.Error);
        TimeOffRequestView view = result.Data!.Single();
        Assert.AreEqual("Amy Tester", view.DisplayName);
        StringAssert.Contains(view.Warnings.Single(), "over their paid holiday allowance");
        CollectionAssert.AreEqual(new[] { "Tom Tester" }, view.OthersOff);
    }

    [TestMethod]
    public async Task GetRequestsForLocationAsync_UpcomingListsOnlyApprovedThatHaveNotEnded()
    {
        Setup setup = await SetupAsync();
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 9, 1), new(2026, 9, 2), 16, TimeOffStatus.Approved);
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 1), new(2026, 10, 1), 8, TimeOffStatus.Approved);
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 5), new(2026, 10, 5), 8, TimeOffStatus.Pending);

        Result<List<TimeOffRequestView>> result = await setup.Service.GetRequestsForLocationAsync(
            SchedulingTestHelpers.ManagerScopeFor(setup.Location), setup.Location.Id, TimeOffListFilter.Upcoming);

        Assert.AreEqual(new DateOnly(2026, 10, 1), result.Data!.Single().Request.StartDate);
    }
    [TestMethod]
    public async Task DecideAsync_AManagerCannotDecideTheirOwnRequestButAnAdminCan()
    {
        Setup setup = await SetupAsync();
        TimeOffRequest own = await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 1), new(2026, 10, 1), 8, TimeOffStatus.Pending);

        Result<TimeOffRequest> asManager = await setup.Service.DecideAsync(SchedulingTestHelpers.ManagerScopeFor(setup.Location), own.Id, true, null, setup.User.Id);
        Result<TimeOffRequest> asAdmin = await setup.Service.DecideAsync(SchedulingTestHelpers.AdminScope, own.Id, true, null, setup.User.Id);

        Assert.IsFalse(asManager.IsSuccess);
        StringAssert.Contains(asManager.Error, "your own time off");
        Assert.IsTrue(asAdmin.IsSuccess, asAdmin.Error);
    }

    [TestMethod]
    public async Task RecordForUserAsync_AManagerCannotRecordTheirOwnTimeOff()
    {
        Setup setup = await SetupAsync();

        Result<TimeOffSubmitResult> result = await setup.Service.RecordForUserAsync(
            SchedulingTestHelpers.ManagerScopeFor(setup.Location), setup.User.Id, setup.Location.Id, Draft(setup.Holiday, Today, Today, 8), setup.User.Id);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task DecideAsync_WithdrawingLeaveThatHasStartedKeepsTheDaysAlreadyTaken()
    {
        Setup setup = await SetupAsync();
        TimeOffRequest started = await SeedRequestAsync(setup, setup.Holiday, Today.AddDays(-2), Today.AddDays(2), 40, TimeOffStatus.Approved);

        Result<TimeOffRequest> result = await setup.Service.DecideAsync(
            SchedulingTestHelpers.ManagerScopeFor(setup.Location), started.Id, false, "Needed back for the half-term rush", Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(setup.Options);
        List<TimeOffRequest> rows = await ctx.TimeOffRequests.OrderBy(x => x.StartDate).ToListAsync();

        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(TimeOffStatus.Approved, rows[0].Status);
        Assert.AreEqual(Today.AddDays(-2), rows[0].StartDate);
        Assert.AreEqual(Today, rows[0].EndDate);
        Assert.AreEqual(24m, rows[0].Hours);

        Assert.AreEqual(TimeOffStatus.Rejected, rows[1].Status);
        Assert.AreEqual(Today.AddDays(1), rows[1].StartDate);
        Assert.AreEqual(Today.AddDays(2), rows[1].EndDate);
        Assert.AreEqual(16m, rows[1].Hours);
        Assert.AreEqual("Needed back for the half-term rush", rows[1].DecisionReason);

        TimeOffBalance balance = (await setup.Service.GetBalancesAsync(setup.User.Id, setup.Company.Id, Today)).Data!.Balances.Single(b => b.Type.Id == setup.Holiday.Id);
        Assert.AreEqual(24m, balance.ApprovedHours);
    }

    [TestMethod]
    public async Task DecideAsync_NothingToWithdrawOnceOnlyTodayIsLeft()
    {
        Setup setup = await SetupAsync();
        TimeOffRequest endsToday = await SeedRequestAsync(setup, setup.Holiday, Today.AddDays(-2), Today, 24, TimeOffStatus.Approved);

        Result<TimeOffRequest> result = await setup.Service.DecideAsync(
            SchedulingTestHelpers.ManagerScopeFor(setup.Location), endsToday.Id, false, "Too late", Manager);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "already been taken");
    }

    [TestMethod]
    public async Task RecordForUserAsync_OverAllowanceWarningIgnoresOtherPendingRequests()
    {
        Setup setup = await SetupAsync(holidayHours: 224m);
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 5, 4), new(2026, 5, 29), 160, TimeOffStatus.Approved);
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 11, 2), new(2026, 11, 10), 56, TimeOffStatus.Pending);
        TimeOffRequestDraft threeDays = Draft(setup.Holiday, new(2026, 10, 12), new(2026, 10, 14), 24);

        Result<TimeOffPreview> staff = await setup.Service.PreviewAsync(setup.User.Id, setup.Company.Id, threeDays);
        Result<TimeOffPreview> manager = await setup.Service.PreviewAsync(setup.User.Id, setup.Company.Id, threeDays, forManager: true);

        Assert.AreEqual(1, staff.Data!.Warnings.Count, "Staff are warned against pending requests too.");
        Assert.AreEqual(0, manager.Data!.Warnings.Count, "Approved total would be 23 of 28 days.");
    }

    [TestMethod]
    public async Task GetBalancesAsync_CreatesTheDefaultTypesIfNoneExistYet()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        TimeOffService service = new(factory, new SchedulingSettingsService(factory), new TimeOffEventBus(), new TestClock(Now), NullLogger<TimeOffService>.Instance);

        Result<TimeOffBalances> result = await service.GetBalancesAsync(user.Id, company.Id, Today);

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEqual(new[] { "Paid holiday", "Unpaid leave" }, result.Data!.Balances.Select(b => b.Type.Name).ToArray());
    }

    [TestMethod]
    public async Task GetRequestsForLocationAsync_WorksOutEachPersonsBalanceSeparately()
    {
        Setup setup = await SetupAsync(holidayHours: 40m);
        UserProfile colleague = await SchedulingTestHelpers.SeedUserAsync(setup.Options, setup.Location, "Tom");
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 8, 3), new(2026, 8, 5), 24, TimeOffStatus.Approved);
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 12), new(2026, 10, 13), 16, TimeOffStatus.Pending);
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 19), new(2026, 10, 20), 16, TimeOffStatus.Pending, colleague.Id);

        Result<List<TimeOffRequestView>> result = await setup.Service.GetRequestsForLocationAsync(
            SchedulingTestHelpers.ManagerScopeFor(setup.Location), setup.Location.Id, TimeOffListFilter.Pending);

        TimeOffRequestView amy = result.Data!.Single(v => v.Request.UserId == setup.User.Id);
        TimeOffRequestView tom = result.Data!.Single(v => v.Request.UserId == colleague.Id);
        Assert.AreEqual(16m, amy.Balance!.RemainingHours);
        Assert.AreEqual(40m, tom.Balance!.RemainingHours);
    }
    [TestMethod]
    public async Task CountPendingForNavAsync_CountsTheManagersOwnLocationOnly()
    {
        Setup setup = await SetupAsync();
        Location elsewhere = new() { CompanyId = setup.Company.Id, Name = "Wisbech", IsActive = true };

        await using (ApplicationDbContext ctx = new(setup.Options))
        {
            ctx.Locations.Add(elsewhere);
            await ctx.SaveChangesAsync();
        }

        await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 1), new(2026, 10, 1), 8, TimeOffStatus.Pending);
        await SeedRequestAsync(setup, setup.Holiday, new(2026, 10, 5), new(2026, 10, 5), 8, TimeOffStatus.Approved);

        await using (ApplicationDbContext ctx = new(setup.Options))
        {
            ctx.TimeOffRequests.Add(new TimeOffRequest
            {
                Id = Guid.NewGuid(), UserId = "someone-else", TimeOffTypeId = setup.Holiday.Id, LocationId = elsewhere.Id,
                StartDate = new(2026, 10, 1), EndDate = new(2026, 10, 1), Hours = 8, Status = TimeOffStatus.Pending
            });
            await ctx.SaveChangesAsync();
        }

        Assert.AreEqual(1, (await setup.Service.CountPendingForNavAsync(SchedulingTestHelpers.ManagerScopeFor(setup.Location), Manager)).Data);
        Assert.AreEqual(0, (await setup.Service.CountPendingForNavAsync(SchedulingTestHelpers.ManagerScopeFor(setup.Location), setup.User.Id)).Data, "A manager's own request doesn't count.");
        Assert.AreEqual(0, (await setup.Service.CountPendingForNavAsync(SchedulingTestHelpers.StaffScopeFor(setup.Location), Manager)).Data);
        Assert.AreEqual(2, (await setup.Service.CountPendingForNavAsync(SchedulingTestHelpers.AdminScope, Manager)).Data, "An admin with no location sees every location.");
    }

    [TestMethod]
    public async Task RequestsAndDecisionsTellOpenPagesThatTimeOffChanged()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        TimeOffEventBus bus = new();
        List<int> published = [];
        bus.OnChanged += published.Add;
        TimeOffService service = new(factory, new SchedulingSettingsService(factory), bus, new TestClock(Now), NullLogger<TimeOffService>.Instance);
        TimeOffType holiday = (await new TimeOffPolicyService(factory, NullLogger<TimeOffPolicyService>.Instance).GetTypesAsync(company.Id)).Data!.First();

        Result<TimeOffSubmitResult> submitted = await service.SubmitAsync(user.Id, location.Id, Draft(holiday, new(2026, 10, 12), new(2026, 10, 12), 8));
        await service.DecideAsync(SchedulingTestHelpers.ManagerScopeFor(location), submitted.Data!.Request.Id, true, null, Manager);

        CollectionAssert.AreEqual(new[] { location.Id, location.Id }, published);
    }
}
