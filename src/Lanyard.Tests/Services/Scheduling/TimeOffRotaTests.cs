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

// How time off shows up on the rota: beside the shifts, as a warning on a shift that clashes with
// it, and as a reason not to nag about contract minimums in a week someone is away.
[TestClass]
public class TimeOffRotaTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private const string Manager = "manager-user";

    private static RotaService GetRotaService(DbContextOptions<ApplicationDbContext> options)
    {
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        return new RotaService(factory, new ContractRequirementService(factory), new RecordingNotificationDispatcher(), NullLogger<RotaService>.Instance);
    }

    private static async Task<(DbContextOptions<ApplicationDbContext> Options, Company Company, Location Location, UserProfile User, TimeOffType Holiday)> SetupAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        StaffPosition position = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Customer Service Advisor");

        await using ApplicationDbContext ctx = new(options);

        ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = user.Id, StaffPositionId = position.Id, IsPrimary = true, CreateDate = DateTime.UtcNow });
        ctx.ContractRequirements.Add(new ContractRequirement { Id = Guid.NewGuid(), CompanyId = company.Id, MinShiftsPerWeek = 1, MinShiftLengthHours = 2, UpdateDate = DateTime.UtcNow });

        TimeOffType holiday = new() { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Paid holiday", IsPaid = true, DeductsFromAllowance = true };
        ctx.TimeOffTypes.Add(holiday);
        await ctx.SaveChangesAsync();

        return (options, company, location, user, holiday);
    }

    private static async Task AddTimeOffAsync(DbContextOptions<ApplicationDbContext> options, Location location, UserProfile user, TimeOffType type, DateOnly start, DateOnly end, TimeOffStatus status)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.TimeOffRequests.Add(new TimeOffRequest
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TimeOffTypeId = type.Id,
            LocationId = location.Id,
            StartDate = start,
            EndDate = end,
            Hours = (end.DayNumber - start.DayNumber + 1) * 8,
            Status = status,
            RequestedDateUtc = DateTime.UtcNow
        });

        await ctx.SaveChangesAsync();
    }

    private static async Task AddShiftAsync(DbContextOptions<ApplicationDbContext> options, Location location, UserProfile user, DateOnly day)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.Shifts.Add(new Shift
        {
            Id = Guid.NewGuid(),
            LocationId = location.Id,
            UserId = user.Id,
            StartUtc = RotaTime.ToUtc(day, new TimeOnly(9, 0)),
            EndUtc = RotaTime.ToUtc(day, new TimeOnly(17, 0)),
            IsActive = true,
            CreateDate = DateTime.UtcNow,
            CreateByUserId = Manager
        });

        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task GetRangeViewAsync_PutsPendingAndApprovedTimeOffOnTheRow()
    {
        var (options, _, location, user, holiday) = await SetupAsync();
        await AddTimeOffAsync(options, location, user, holiday, Monday.AddDays(1), Monday.AddDays(1), TimeOffStatus.Approved);
        await AddTimeOffAsync(options, location, user, holiday, Monday.AddDays(3), Monday.AddDays(3), TimeOffStatus.Pending);
        await AddTimeOffAsync(options, location, user, holiday, Monday.AddDays(4), Monday.AddDays(4), TimeOffStatus.Rejected);

        Result<RotaRangeView> result = await GetRotaService(options).GetRangeViewAsync(SchedulingTestHelpers.AdminScope, location.Id, Monday, Monday.AddDays(6));

        RotaStaffRow row = result.Data!.Rows.Single();
        Assert.AreEqual(2, row.TimeOff.Count, "Rejected time off isn't shown.");
        Assert.AreEqual(TimeOffStatus.Approved, row.TimeOffOn(Monday.AddDays(1))!.Status);
        Assert.AreEqual(TimeOffStatus.Pending, row.TimeOffOn(Monday.AddDays(3))!.Status);
        Assert.IsNull(row.TimeOffOn(Monday.AddDays(4)));
    }

    [TestMethod]
    public async Task GetRangeViewAsync_NoMinimumShiftWarningForAWholeWeekOff()
    {
        var (options, _, location, user, holiday) = await SetupAsync();
        await AddTimeOffAsync(options, location, user, holiday, Monday.AddDays(-2), Monday.AddDays(8), TimeOffStatus.Approved);

        Result<RotaRangeView> result = await GetRotaService(options).GetRangeViewAsync(SchedulingTestHelpers.AdminScope, location.Id, Monday, Monday.AddDays(6));

        Assert.AreEqual(0, result.Data!.Rows.Single().Warnings.Count);
    }

    [TestMethod]
    public async Task GetRangeViewAsync_StillWarnsWhenOnlyPartOfTheWeekIsOff()
    {
        var (options, _, location, user, holiday) = await SetupAsync();
        await AddTimeOffAsync(options, location, user, holiday, Monday, Monday.AddDays(4), TimeOffStatus.Approved);

        Result<RotaRangeView> result = await GetRotaService(options).GetRangeViewAsync(SchedulingTestHelpers.AdminScope, location.Id, Monday, Monday.AddDays(6));

        Assert.AreEqual(ContractWarningKind.BelowMinShifts, result.Data!.Rows.Single().Warnings.Single().Kind);
    }

    [TestMethod]
    public async Task GetRangeViewAsync_WarnsAboutAShiftOnADayOfApprovedTimeOff()
    {
        var (options, _, location, user, holiday) = await SetupAsync();
        await AddTimeOffAsync(options, location, user, holiday, Monday.AddDays(2), Monday.AddDays(2), TimeOffStatus.Approved);
        await AddShiftAsync(options, location, user, Monday.AddDays(2));

        Result<RotaRangeView> result = await GetRotaService(options).GetRangeViewAsync(SchedulingTestHelpers.AdminScope, location.Id, Monday, Monday.AddDays(6));

        ContractWarning warning = result.Data!.Rows.Single().Warnings.Single();
        Assert.AreEqual(ContractWarningKind.ShiftDuringTimeOff, warning.Kind);
        StringAssert.Contains(warning.Message, "Wed 7 Oct");
    }

    [TestMethod]
    public async Task SaveShiftAsync_WarnsButSavesOnADayOfTimeOff()
    {
        var (options, _, location, user, holiday) = await SetupAsync();
        await AddTimeOffAsync(options, location, user, holiday, Monday, Monday, TimeOffStatus.Pending);

        Result<ShiftSaveResult> result = await GetRotaService(options).SaveShiftAsync(SchedulingTestHelpers.AdminScope, new Shift
        {
            LocationId = location.Id,
            UserId = user.Id,
            StartUtc = RotaTime.ToUtc(Monday, new TimeOnly(9, 0)),
            EndUtc = RotaTime.ToUtc(Monday, new TimeOnly(13, 0)),
            CreateByUserId = Manager
        }, Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        StringAssert.Contains(result.Data!.Warnings.Single(), "has asked for paid holiday");
    }

    [TestMethod]
    public void IsWholeWeekOff_NeedsEveryDayCovered()
    {
        TimeOffRequest weekdays = new() { UserId = "u", TimeOffTypeId = Guid.NewGuid(), LocationId = 1, StartDate = Monday, EndDate = Monday.AddDays(4) };
        TimeOffRequest weekend = new() { UserId = "u", TimeOffTypeId = Guid.NewGuid(), LocationId = 1, StartDate = Monday.AddDays(5), EndDate = Monday.AddDays(6) };

        Assert.IsFalse(ContractCompliance.IsWholeWeekOff(Monday, [weekdays]));
        Assert.IsTrue(ContractCompliance.IsWholeWeekOff(Monday, [weekdays, weekend]));
        Assert.IsFalse(ContractCompliance.IsWholeWeekOff(Monday, []));
    }
}
