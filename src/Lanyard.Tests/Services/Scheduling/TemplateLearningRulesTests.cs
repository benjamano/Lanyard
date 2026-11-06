using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class TemplateLearningRulesTests
{
    // Eight Saturdays from 3 October 2026 (the first four in summer time, the rest in winter).
    private static readonly DateOnly From = new(2026, 9, 28);
    private static readonly DateOnly To = From.AddDays(8 * 7 - 1);
    private static readonly List<DateOnly> Saturdays = Enumerable.Range(0, 8).Select(i => From.AddDays(5 + 7 * i)).ToList();

    private static readonly Guid Csa = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();
    private static readonly Guid Supervisor = Guid.NewGuid();

    private static readonly Dictionary<Guid, string> Positions = new() { [Csa] = "CSA", [Manager] = "Manager", [Supervisor] = "Supervisor" };

    private static readonly Dictionary<string, string> Names = new()
    {
        ["ben"] = "Ben M.", ["rupert"] = "Rupert H.", ["ryan"] = "Ryan F.", ["tia"] = "Tia M.",
        ["ella"] = "Ella S.", ["james"] = "James K.", ["jack"] = "Jack L.", ["sub"] = "Sub S."
    };

    private static Shift Worked(DateOnly day, string start, string end, Guid position, string? userId, int breakMinutes = 0)
    {
        TimeOnly s = TimeOnly.Parse(start), e = TimeOnly.Parse(end);
        return new Shift
        {
            Id = Guid.NewGuid(),
            LocationId = 1,
            UserId = userId,
            StaffPositionId = position,
            StartUtc = RotaTime.ToUtc(day, s),
            EndUtc = RotaTime.ToUtc(e <= s ? day.AddDays(1) : day, e),
            BreakMinutes = breakMinutes,
            PublishedDateUtc = DateTime.UtcNow,
            IsActive = true,
            CreateByUserId = "m"
        };
    }

    private static PartyBooking Hire(DateOnly day) => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = 1,
        LocationId = 1,
        HireKind = PrivateHireKind.LaserTagAndPlay,
        StartUtc = RotaTime.ToUtc(day, new TimeOnly(18, 30)),
        EndUtc = RotaTime.ToUtc(day, new TimeOnly(21, 30)),
        Status = PartyBookingStatus.Booked
    };

    // Ben's Saturday. The hire shifts only happen on hire days (weeks 1, 4 and 7).
    private static (List<Shift> Shifts, List<PartyBooking> Hires) BensSaturdays()
    {
        List<Shift> shifts = [];
        List<PartyBooking> hires = [];

        for (int i = 0; i < Saturdays.Count; i++)
        {
            DateOnly day = Saturdays[i];

            // Ben's 09:00 start drifts to 09:10 one week: still the same shift.
            shifts.Add(Worked(day, i == 2 ? "09:10" : "09:00", "11:00", Csa, "ben"));
            // Rupert is off one week and someone covers.
            shifts.Add(Worked(day, "08:30", "18:30", Manager, i == 5 ? "sub" : "rupert"));
            shifts.Add(Worked(day, "11:00", "14:00", Csa, "james"));
            shifts.Add(Worked(day, "14:30", "18:30", Csa, "jack"));

            if (i is 1 or 4 or 7)
            {
                hires.Add(Hire(day));
                shifts.Add(Worked(day, "08:30", "21:30", Manager, "ryan", 45));
                shifts.Add(Worked(day, "14:30", "21:30", Csa, "tia", 15));
                shifts.Add(Worked(day, "11:00", "21:30", Supervisor, "ella", 45));
            }
        }

        // A one-off.
        shifts.Add(Worked(Saturdays[3], "12:00", "16:00", Csa, "sub"));

        return (shifts, hires);
    }

    private static TemplateLearningResult Learn(List<Shift> shifts, List<PartyBooking> hires,
        List<ShiftTemplate>? existing = null, List<PartyStaffingRule>? rules = null) =>
        TemplateLearningRules.Learn(From, To, shifts, hires, Names, Positions, existing ?? [], rules ?? []);

    private static LearnedTemplate Find(TemplateLearningResult result, string start, string end) =>
        result.Templates.Single(x => x.StartTime == TimeOnly.Parse(start) && x.EndTime == TimeOnly.Parse(end));

    [TestMethod]
    public void Near_WithinFifteenMinutes()
    {
        Assert.IsTrue(TemplateLearningRules.Near(new TimeOnly(9, 0), new TimeOnly(9, 15)));
        Assert.IsFalse(TemplateLearningRules.Near(new TimeOnly(9, 0), new TimeOnly(9, 16)));
        Assert.IsTrue(TemplateLearningRules.Near(new TimeOnly(23, 55), new TimeOnly(0, 5)), "across midnight");
    }

    [TestMethod]
    public void Learn_BensSaturday_EveryWeekShiftsWithTheirRegulars()
    {
        (List<Shift> shifts, List<PartyBooking> hires) = BensSaturdays();

        TemplateLearningResult result = Learn(shifts, hires);

        LearnedLine early = Find(result, "09:00", "11:00").Lines.Single();
        Assert.AreEqual(1, early.Headcount, "09:10 one week is the same shift");
        Assert.AreEqual("ben", early.Regulars.Single().UserId);

        LearnedLine manager = Find(result, "08:30", "18:30").Lines.Single();
        Assert.AreEqual(1, manager.Headcount);
        Assert.AreEqual("rupert", manager.Regulars.Single().UserId, "one week's cover doesn't change the regular");

        Assert.AreEqual("james", Find(result, "11:00", "14:00").Lines.Single().Regulars.Single().UserId);
        Assert.AreEqual("jack", Find(result, "14:30", "18:30").Lines.Single().Regulars.Single().UserId);
        Assert.AreEqual(DayOfWeek.Saturday, Find(result, "14:30", "18:30").DayOfWeek);
    }

    [TestMethod]
    public void Learn_HireOnlyShifts_LearnedWithNobodyNormallyAndTheirRegular()
    {
        (List<Shift> shifts, List<PartyBooking> hires) = BensSaturdays();

        TemplateLearningResult result = Learn(shifts, hires);

        LearnedTemplate late = Find(result, "08:30", "21:30");
        Assert.IsTrue(late.Lines.Single().HireOnly);
        Assert.AreEqual(0, late.Lines.Single().Headcount);
        Assert.AreEqual("ryan", late.Lines.Single().Regulars.Single().UserId);
        Assert.AreEqual(45, late.BreakMinutes);

        Assert.AreEqual("tia", Find(result, "14:30", "21:30").Lines.Single().Regulars.Single().UserId);
        Assert.AreEqual("ella", Find(result, "11:00", "21:30").Lines.Single().Regulars.Single().UserId);
    }

    [TestMethod]
    public void Learn_HireStaffing_OneOfEachPerHire()
    {
        (List<Shift> shifts, List<PartyBooking> hires) = BensSaturdays();

        TemplateLearningResult result = Learn(shifts, hires);

        CollectionAssert.AreEquivalent(new[] { "CSA", "Manager", "Supervisor" }, result.HireStaffing.Select(x => x.PositionName).ToList());
        Assert.IsTrue(result.HireStaffing.All(x => x.Count == 1 && x.HireKind == PrivateHireKind.LaserTagAndPlay && x.HiresSeen == 3));
    }

    [TestMethod]
    public void Learn_OneOffs_LeftOut()
    {
        (List<Shift> shifts, List<PartyBooking> hires) = BensSaturdays();

        TemplateLearningResult result = Learn(shifts, hires);

        Assert.AreEqual(7, result.Templates.Count);
        Assert.AreEqual(1, result.LeftOut);
    }

    [TestMethod]
    public void Learn_HireMostWeeks_StillHireOnly()
    {
        // Hires on 6 of 8 Saturdays: the late shifts ran most weeks, but only ever with a hire.
        List<Shift> shifts = [];
        List<PartyBooking> hires = [];

        for (int i = 0; i < Saturdays.Count; i++)
        {
            shifts.Add(Worked(Saturdays[i], "08:30", "18:30", Manager, "rupert"));

            if (i < 6)
            {
                hires.Add(Hire(Saturdays[i]));
                shifts.Add(Worked(Saturdays[i], "08:30", "21:30", Manager, "ryan"));
            }
        }

        Assert.IsTrue(Find(Learn(shifts, hires), "08:30", "21:30").Lines.Single().HireOnly);
    }

    [TestMethod]
    public void Learn_SeveralPeople_MedianHeadcountAndRegularsWhoWorkedHalfOrMore()
    {
        List<Shift> shifts = [];

        for (int i = 0; i < Saturdays.Count; i++)
        {
            shifts.Add(Worked(Saturdays[i], "09:00", "14:00", Csa, "ben"));
            shifts.Add(Worked(Saturdays[i], "09:00", "14:00", Csa, i % 2 == 0 ? "james" : "jack"));

            if (i == 0)
            {
                shifts.Add(Worked(Saturdays[i], "09:00", "14:00", Csa, "tia"));
            }
        }

        LearnedLine line = Find(Learn(shifts, []), "09:00", "14:00").Lines.Single();

        Assert.AreEqual(2, line.Headcount);
        CollectionAssert.AreEqual(new[] { "ben", "jack" }, line.Regulars.Select(x => x.UserId).ToList(),
            "Ben every week, then James and Jack half each - name order breaks the tie, capped at 2");
    }

    [TestMethod]
    public void Learn_ExistingTemplate_SameOrChanged()
    {
        (List<Shift> shifts, List<PartyBooking> hires) = BensSaturdays();

        ShiftTemplate early = new()
        {
            Id = Guid.NewGuid(), CompanyId = 1, LocationId = 1, Name = "Early CSA", DayOfWeek = DayOfWeek.Saturday,
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(11, 0),
            Slots = [new ShiftTemplateSlot { ShiftTemplateId = Guid.Empty, StaffPositionId = Csa, Headcount = 1,
                Regulars = [new ShiftTemplateSlotRegular { ShiftTemplateSlotId = Guid.Empty, UserId = "ben" }] }]
        };
        ShiftTemplate manager = new()
        {
            Id = Guid.NewGuid(), CompanyId = 1, LocationId = 1, Name = "Manager", DayOfWeek = DayOfWeek.Saturday,
            StartTime = new TimeOnly(8, 30), EndTime = new TimeOnly(18, 30),
            Slots = [new ShiftTemplateSlot { ShiftTemplateId = Guid.Empty, StaffPositionId = Manager, Headcount = 2 }]
        };

        TemplateLearningResult result = Learn(shifts, hires, [early, manager]);

        Assert.AreEqual(LearnedStatus.Same, Find(result, "09:00", "11:00").Status);

        LearnedTemplate changed = Find(result, "08:30", "18:30");
        Assert.AreEqual(LearnedStatus.Changed, changed.Status);
        Assert.AreEqual(manager.Id, changed.ExistingTemplateId);
        Assert.AreEqual("Manager", changed.Name, "keeps the existing name");
        Assert.IsTrue(changed.Changes.Any(x => x.Contains("2 → 1")));
        Assert.IsTrue(changed.Changes.Any(x => x.Contains("Rupert")));

        Assert.AreEqual(LearnedStatus.New, Find(result, "11:00", "14:00").Status);
    }

    [TestMethod]
    public void ToTemplate_CarriesLinesAndRegularsInOrder()
    {
        (List<Shift> shifts, List<PartyBooking> hires) = BensSaturdays();
        LearnedTemplate learned = Find(Learn(shifts, hires), "08:30", "21:30");

        ShiftTemplate template = TemplateLearningRules.ToTemplate(learned, 7);

        Assert.AreEqual(7, template.LocationId);
        Assert.AreEqual(0, template.Slots.Single().Headcount);
        Assert.AreEqual("ryan", template.Slots.Single().Regulars.Single().UserId);
        Assert.IsNull(ShiftTemplateRules.Validate(template));
    }

    [TestMethod]
    public void Learn_ExistingTemplateAFewMinutesOut_IsAnUpdateNotANewOne()
    {
        (List<Shift> shifts, List<PartyBooking> hires) = BensSaturdays();
        ShiftTemplate early = new()
        {
            Id = Guid.NewGuid(), CompanyId = 1, LocationId = 1, Name = "Early CSA", DayOfWeek = DayOfWeek.Saturday,
            StartTime = new TimeOnly(9, 5), EndTime = new TimeOnly(11, 0),
            Slots = [new ShiftTemplateSlot { ShiftTemplateId = Guid.Empty, StaffPositionId = Csa, Headcount = 1,
                Regulars = [new ShiftTemplateSlotRegular { ShiftTemplateSlotId = Guid.Empty, UserId = "ben" }] }]
        };

        LearnedTemplate learned = Find(Learn(shifts, hires, [early]), "09:00", "11:00");

        Assert.AreEqual(LearnedStatus.Changed, learned.Status);
        Assert.AreEqual(early.Id, learned.ExistingTemplateId);
        Assert.AreEqual("Times: 09:05–11:00 → 09:00–11:00", learned.Changes.Single());
    }
}
