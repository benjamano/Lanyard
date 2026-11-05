using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class ShiftTemplateRulesTests
{
    private static readonly Guid Host = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();

    private static ShiftTemplate Template(int startHour = 9, int endHour = 14, params (Guid Position, int Count)[] slots) => new()
    {
        CompanyId = 1,
        LocationId = 1,
        Name = "Saturday AM",
        DayOfWeek = DayOfWeek.Saturday,
        StartTime = new TimeOnly(startHour, 0),
        EndTime = new TimeOnly(endHour, 0),
        Slots = (slots.Length == 0 ? [(Host, 4)] : slots)
            .Select(s => new ShiftTemplateSlot { ShiftTemplateId = Guid.Empty, StaffPositionId = s.Position, Headcount = s.Count })
            .ToList()
    };

    [TestMethod]
    public void Validate_ValidTemplate_Passes()
    {
        Assert.IsNull(ShiftTemplateRules.Validate(Template(9, 14, (Manager, 2), (Host, 4))));
    }

    [TestMethod]
    public void Validate_NoName_Refused()
    {
        ShiftTemplate template = Template();
        template.Name = "  ";

        StringAssert.Contains(ShiftTemplateRules.Validate(template), "name");
    }

    [TestMethod]
    public void Validate_SameStartAndEnd_Refused()
    {
        StringAssert.Contains(ShiftTemplateRules.Validate(Template(9, 9)), "different start and end");
    }

    [TestMethod]
    public void Validate_PastMidnight_Passes()
    {
        ShiftTemplate template = Template(18, 2);

        Assert.IsNull(ShiftTemplateRules.Validate(template));
        Assert.IsTrue(template.RunsPastMidnight);
        Assert.AreEqual(TimeSpan.FromHours(8), template.Length);
    }

    [TestMethod]
    public void Validate_BreakAsLongAsShift_Refused()
    {
        ShiftTemplate template = Template(9, 10);
        template.BreakMinutes = 60;

        StringAssert.Contains(ShiftTemplateRules.Validate(template), "break");
    }

    [TestMethod]
    public void Validate_NoPositions_Refused()
    {
        ShiftTemplate template = Template();
        template.Slots.Clear();

        StringAssert.Contains(ShiftTemplateRules.Validate(template), "position");
    }

    [TestMethod]
    public void Validate_SamePositionTwice_Refused()
    {
        StringAssert.Contains(ShiftTemplateRules.Validate(Template(9, 14, (Host, 1), (Host, 2))), "once");
    }

    [TestMethod]
    public void Validate_ZeroHeadcount_Refused()
    {
        StringAssert.Contains(ShiftTemplateRules.Validate(Template(9, 14, (Host, 0))), "between 1 and");
    }

    [TestMethod]
    public void Validate_UntilBeforeStarting_Refused()
    {
        ShiftTemplate template = Template();
        template.EffectiveFrom = new DateOnly(2026, 12, 1);
        template.EffectiveTo = new DateOnly(2026, 11, 1);

        StringAssert.Contains(ShiftTemplateRules.Validate(template), "end date");
    }

    [TestMethod]
    public void AppliesOn_RespectsDayAndDates()
    {
        ShiftTemplate template = Template();
        template.EffectiveFrom = new DateOnly(2026, 11, 7);
        template.EffectiveTo = new DateOnly(2026, 11, 14);

        Assert.IsFalse(template.AppliesOn(new DateOnly(2026, 10, 31)), "Saturday before it starts");
        Assert.IsTrue(template.AppliesOn(new DateOnly(2026, 11, 7)), "first day");
        Assert.IsFalse(template.AppliesOn(new DateOnly(2026, 11, 8)), "a Sunday");
        Assert.IsTrue(template.AppliesOn(new DateOnly(2026, 11, 14)), "last day");
        Assert.IsFalse(template.AppliesOn(new DateOnly(2026, 11, 21)), "after it ends");

        template.IsActive = false;
        Assert.IsFalse(template.AppliesOn(new DateOnly(2026, 11, 7)), "archived");
    }
}
