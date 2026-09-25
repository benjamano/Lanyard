using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class ContractComplianceTests
{
    private static readonly DateOnly Week = new(2026, 10, 5);

    private static Shift ShiftOf(int dayOffset, int startHour, int endHour, int breakMinutes = 0) => new()
    {
        LocationId = 1,
        UserId = "u1",
        CreateByUserId = "m",
        StartUtc = RotaTime.ToUtc(Week.AddDays(dayOffset), new TimeOnly(startHour, 0)),
        EndUtc = RotaTime.ToUtc(Week.AddDays(dayOffset), new TimeOnly(endHour, 0)),
        BreakMinutes = breakMinutes
    };

    private static ResolvedContract Contract(int? minShifts = null, decimal? minLength = null, decimal? minHours = null, decimal? maxHours = null) => new(
        new ContractValue<int>(minShifts, minShifts is null ? ContractTier.None : ContractTier.Company),
        new ContractValue<decimal>(minLength, minLength is null ? ContractTier.None : ContractTier.Company),
        new ContractValue<decimal>(minHours, minHours is null ? ContractTier.None : ContractTier.Company),
        new ContractValue<decimal>(maxHours, maxHours is null ? ContractTier.None : ContractTier.Company));

    [TestMethod]
    public void EvaluateWeek_NoContract_NoWarnings()
    {
        List<ContractWarning> warnings = ContractCompliance.EvaluateWeek("u1", Week, ResolvedContract.Empty, []);

        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    public void EvaluateWeek_NoShifts_WarnsBelowMinimumShifts()
    {
        List<ContractWarning> warnings = ContractCompliance.EvaluateWeek("u1", Week, Contract(minShifts: 1, minLength: 2), []);

        Assert.AreEqual(1, warnings.Count);
        Assert.AreEqual(ContractWarningKind.BelowMinShifts, warnings[0].Kind);
        Assert.AreEqual(Week, warnings[0].WeekStart);
        StringAssert.Contains(warnings[0].Message, "2 h or more");
    }

    [TestMethod]
    public void EvaluateWeek_ShiftShorterThanMinimumLengthDoesNotCount()
    {
        List<ContractWarning> warnings = ContractCompliance.EvaluateWeek("u1", Week, Contract(minShifts: 1, minLength: 2), [ShiftOf(0, 10, 11)]);

        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0].Message, "has 0");
    }

    [TestMethod]
    public void EvaluateWeek_BreakCountsAgainstShiftLength()
    {
        // 2 h on the clock with a 30 minute unpaid break is 1.5 paid hours - not a 2 h shift.
        List<ContractWarning> warnings = ContractCompliance.EvaluateWeek("u1", Week, Contract(minShifts: 1, minLength: 2), [ShiftOf(0, 10, 12, breakMinutes: 30)]);

        Assert.AreEqual(1, warnings.Count);
    }

    [TestMethod]
    public void EvaluateWeek_QualifyingShiftSatisfiesPlay2DayDefault()
    {
        List<ContractWarning> warnings = ContractCompliance.EvaluateWeek("u1", Week, Contract(minShifts: 1, minLength: 2), [ShiftOf(2, 10, 12)]);

        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    public void EvaluateWeek_WarnsBelowMinimumHours()
    {
        List<ContractWarning> warnings = ContractCompliance.EvaluateWeek("u1", Week, Contract(minHours: 16), [ShiftOf(0, 9, 17)]);

        Assert.AreEqual(ContractWarningKind.BelowMinHours, warnings.Single().Kind);
    }

    [TestMethod]
    public void EvaluateWeek_WarnsAboveMaximumHours()
    {
        List<Shift> shifts = Enumerable.Range(0, 5).Select(d => ShiftOf(d, 8, 18)).ToList();

        List<ContractWarning> warnings = ContractCompliance.EvaluateWeek("u1", Week, Contract(maxHours: 48), shifts);

        Assert.AreEqual(ContractWarningKind.AboveMaxHours, warnings.Single().Kind);
        StringAssert.Contains(warnings[0].Message, "50 h");
    }
}
