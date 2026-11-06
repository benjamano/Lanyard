using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class RotaAutoBuildRulesTests
{
    // Monday 5 October 2026 - British Summer Time. Saturday is the 10th.
    private static readonly DateOnly Week = new(2026, 10, 5);
    private static readonly DateOnly Saturday = Week.AddDays(5);
    private static readonly Guid Host = Guid.NewGuid();
    private static readonly Guid Duty = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private static ShiftTemplate Template(DayOfWeek day = DayOfWeek.Saturday, int start = 9, int end = 14, params (Guid Position, string Name, int Count)[] slots) => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = 1,
        LocationId = 1,
        Name = "Saturday AM",
        DayOfWeek = day,
        StartTime = new TimeOnly(start, 0),
        EndTime = new TimeOnly(end, 0),
        Slots = slots.Select(s => new ShiftTemplateSlot
        {
            ShiftTemplateId = Guid.Empty,
            StaffPositionId = s.Position,
            StaffPosition = new StaffPosition { Id = s.Position, CompanyId = 1, Name = s.Name },
            Headcount = s.Count
        }).ToList()
    };

    private static AutoBuildWorker Worker(string name, params Guid[] positions) =>
        new(name.ToLowerInvariant(), name, positions.FirstOrDefault(), null, ResolvedContract.Empty, [], [], new Dictionary<DateOnly, string>());

    // Who can work what: the worker must hold the shift's position (the WhyCantWork position rule).
    private static Func<AutoBuildWorker, Shift, string?> Holds(Dictionary<string, Guid[]> held) =>
        (worker, shift) => held[worker.UserId].Contains(shift.StaffPositionId!.Value) ? null : "They don't work as that.";

    private static ResolvedContract MaxHours(decimal hours) => new(
        new ContractValue<int>(null, ContractTier.None),
        new ContractValue<decimal>(null, ContractTier.None),
        new ContractValue<decimal>(null, ContractTier.None),
        new ContractValue<decimal>(hours, ContractTier.Company));

    [TestMethod]
    public void ExpandTemplates_OneSlotPerPositionPerMatchingDay()
    {
        ShiftTemplate template = Template(slots: [(Duty, "Duty Manager", 2), (Host, "Host", 4)]);

        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([template], Week, Week.AddDays(13));

        Assert.AreEqual(4, slots.Count, "two Saturdays x two positions");
        Assert.AreEqual(12, slots.Sum(x => x.Needed));
        Assert.IsTrue(slots.All(x => x.Day.DayOfWeek == DayOfWeek.Saturday));
        Assert.AreEqual(RotaTime.ToUtc(Saturday, new TimeOnly(9, 0)), slots[0].StartUtc);
    }

    [TestMethod]
    public void ExpandTemplates_PastMidnight_EndsNextDay()
    {
        ShiftTemplate template = Template(DayOfWeek.Saturday, 18, 1, (Host, "Host", 1));

        DemandSlot slot = RotaAutoBuildRules.ExpandTemplates([template], Week, Week.AddDays(6)).Single();

        Assert.AreEqual(RotaTime.ToUtc(Saturday.AddDays(1), new TimeOnly(1, 0)), slot.EndUtc);
    }

    [TestMethod]
    public void SubtractCovered_ExistingMatchingShiftsCountOnce()
    {
        ShiftTemplate template = Template(slots: [(Host, "Host", 3)]);
        DemandSlot slot = RotaAutoBuildRules.ExpandTemplates([template], Saturday, Saturday).Single();

        Shift same = new() { LocationId = 1, StaffPositionId = Host, StartUtc = slot.StartUtc, EndUtc = slot.EndUtc, CreateByUserId = "m", IsActive = true };
        Shift open = new() { LocationId = 1, StaffPositionId = Host, StartUtc = slot.StartUtc, EndUtc = slot.EndUtc, CreateByUserId = "m", IsActive = true };
        Shift otherTime = new() { LocationId = 1, StaffPositionId = Host, StartUtc = slot.StartUtc.AddHours(1), EndUtc = slot.EndUtc, CreateByUserId = "m", IsActive = true };
        Shift removed = new() { LocationId = 1, StaffPositionId = Host, StartUtc = slot.StartUtc, EndUtc = slot.EndUtc, CreateByUserId = "m", IsActive = false };

        (List<DemandSlot> remaining, int covered) = RotaAutoBuildRules.SubtractCovered([slot], [same, open, otherTime, removed]);

        Assert.AreEqual(2, covered);
        Assert.AreEqual(1, remaining.Single().Needed);
    }

    [TestMethod]
    public void SubtractCovered_FullyCovered_NothingRemains()
    {
        DemandSlot slot = RotaAutoBuildRules.ExpandTemplates([Template(slots: [(Host, "Host", 1)])], Saturday, Saturday).Single();
        Shift same = new() { LocationId = 1, StaffPositionId = Host, StartUtc = slot.StartUtc, EndUtc = slot.EndUtc, CreateByUserId = "m", IsActive = true };

        (List<DemandSlot> remaining, int covered) = RotaAutoBuildRules.SubtractCovered([slot], [same]);

        Assert.AreEqual(0, remaining.Count);
        Assert.AreEqual(1, covered);
    }

    [TestMethod]
    public void Assign_FillsEachSlotWithDifferentPeople()
    {
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([Template(slots: [(Host, "Host", 2)])], Saturday, Saturday);
        List<AutoBuildWorker> workers = [Worker("Amy", Host), Worker("Tom", Host), Worker("Priya", Host)];

        List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(1, slots, workers, (_, _) => null, "m", Now);

        Assert.AreEqual(2, picks.Count);
        Assert.IsTrue(picks.All(x => x.Shift.UserId is not null));
        Assert.AreEqual(2, picks.Select(x => x.Shift.UserId).Distinct().Count(), "nobody is put on the same time twice");
        Assert.IsTrue(picks.All(x => x.Shift.PublishedDateUtc is null && x.Shift.Notes == "Saturday AM"), "drafts, noted with the template");
    }

    [TestMethod]
    public void Assign_MoreShiftsThanPeople_RestLeftOpen()
    {
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([Template(slots: [(Host, "Host", 3)])], Saturday, Saturday);

        List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(1, slots, [Worker("Amy", Host)], (_, _) => null, "m", Now);

        Assert.AreEqual(1, picks.Count(x => x.Shift.UserId is not null));
        Assert.AreEqual(2, picks.Count(x => x.Line.IsOpen));
        Assert.IsTrue(picks.Where(x => x.Line.IsOpen).All(x => x.Line.WhyOpen is not null));
    }

    [TestMethod]
    public void Assign_NeverPicksSomeoneWhoCantWorkIt()
    {
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([Template(slots: [(Duty, "Duty Manager", 1)])], Saturday, Saturday);
        Dictionary<string, Guid[]> held = new() { ["amy"] = [Host], ["sam"] = [Duty] };

        List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(1, slots, [Worker("Amy", Host), Worker("Sam", Duty)], Holds(held), "m", Now);

        Assert.AreEqual("sam", picks.Single().Shift.UserId);
    }

    [TestMethod]
    public void Assign_SaidTheyCantWorkThen_NotPicked()
    {
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([Template(slots: [(Host, "Host", 1)])], Saturday, Saturday);
        AutoBuildWorker amy = Worker("Amy", Host) with
        {
            Availability = [new StaffAvailability { CompanyId = 1, UserId = "amy", DayOfWeek = DayOfWeek.Saturday, Kind = AvailabilityKind.Unavailable }]
        };

        List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(1, slots, [amy], (_, _) => null, "m", Now);

        Assert.IsTrue(picks.Single().Line.IsOpen);
    }

    [TestMethod]
    public void Assign_WouldGoOverMaximum_LeftOpenRatherThanOverworked()
    {
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([Template(slots: [(Host, "Host", 1)])], Saturday, Saturday);
        AutoBuildWorker amy = Worker("Amy", Host) with { Contract = MaxHours(4) };

        List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(1, slots, [amy], (_, _) => null, "m", Now);

        Assert.IsTrue(picks.Single().Line.IsOpen);
        StringAssert.Contains(picks.Single().Line.WhyOpen, "maximum");
    }

    [TestMethod]
    public void Assign_PendingTimeOffThatDay_NotPicked()
    {
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([Template(slots: [(Host, "Host", 1)])], Saturday, Saturday);
        AutoBuildWorker amy = Worker("Amy", Host) with { PendingTimeOff = new Dictionary<DateOnly, string> { [Saturday] = "Holiday" } };

        List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(1, slots, [amy, Worker("Tom", Host)], (_, _) => null, "m", Now);

        Assert.AreEqual("tom", picks.Single().Shift.UserId);
    }

    [TestMethod]
    public void Assign_HoursBuildUpAcrossTheRun_SpreadsTheWork()
    {
        // Every weekday 09-17 for one Host: with two people and no contracts, it alternates
        // rather than giving one person all five days.
        List<ShiftTemplate> templates = Enumerable.Range(0, 5)
            .Select(i => Template((DayOfWeek)(i + 1), 9, 17, (Host, "Host", 1)))
            .ToList();
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates(templates, Week, Week.AddDays(6));
        List<AutoBuildWorker> workers = [Worker("Amy", Host), Worker("Tom", Host)];

        List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(1, slots, workers, (_, _) => null, "m", Now);

        Assert.AreEqual(5, picks.Count(x => x.Shift.UserId is not null));
        int amy = picks.Count(x => x.Shift.UserId == "amy");
        Assert.IsTrue(amy is 2 or 3, $"Amy got {amy} of 5");
        Assert.AreEqual(amy, workers[0].Shifts.Count, "picks are added to the worker's shifts");
    }

    [TestMethod]
    public void Assign_ScarcePersonSavedForTheShiftOnlyTheyCanWork()
    {
        // Sam holds both positions; Amy only Host. Both shifts at the same time. Filling the Host
        // shift first by name would take Sam (alphabetically after Amy, so not here) - the point is
        // the Duty shift, which only Sam can do, is filled first and Amy takes the Host shift.
        ShiftTemplate template = Template(slots: [(Host, "Host", 1), (Duty, "Duty Manager", 1)]);
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([template], Saturday, Saturday);
        Dictionary<string, Guid[]> held = new() { ["aaron"] = [Host, Duty], ["zoe"] = [Host] };

        List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(1, slots, [Worker("Aaron", Host, Duty), Worker("Zoe", Host)], Holds(held), "m", Now);

        Assert.AreEqual("aaron", picks.Single(x => x.Shift.StaffPositionId == Duty).Shift.UserId);
        Assert.AreEqual("zoe", picks.Single(x => x.Shift.StaffPositionId == Host).Shift.UserId);
    }

    [TestMethod]
    public void Assign_PastSomeonesWish_OnlyPersonLeft_StillPicked()
    {
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([Template(slots: [(Host, "Host", 1)])], Saturday, Saturday);
        AutoBuildWorker amy = Worker("Amy", Host) with { Wish = new StaffWorkPreference { CompanyId = 1, UserId = "amy", MaxHoursPerWeek = 2 } };

        List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(1, slots, [amy], (_, _) => null, "m", Now);

        Assert.AreEqual("amy", picks.Single().Shift.UserId, "a wish is soft");
    }

    [TestMethod]
    public void Assign_PastSomeonesWish_SomeoneElsePickedFirst()
    {
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([Template(slots: [(Host, "Host", 1)])], Saturday, Saturday);
        AutoBuildWorker amy = Worker("Amy", Host) with { Wish = new StaffWorkPreference { CompanyId = 1, UserId = "amy", MaxShiftsPerWeek = 1 } };
        amy.Shifts.Add(new Shift { Id = Guid.NewGuid(), LocationId = 1, UserId = "amy", CreateByUserId = "m", IsActive = true,
            StartUtc = RotaTime.ToUtc(Week, new TimeOnly(9, 0)), EndUtc = RotaTime.ToUtc(Week, new TimeOnly(10, 0)) });
        AutoBuildWorker tom = Worker("Tom", Host);
        tom.Shifts.Add(new Shift { Id = Guid.NewGuid(), LocationId = 1, UserId = "tom", CreateByUserId = "m", IsActive = true,
            StartUtc = RotaTime.ToUtc(Week, new TimeOnly(9, 0)), EndUtc = RotaTime.ToUtc(Week, new TimeOnly(17, 0)) });

        List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(1, slots, [amy, tom], (_, _) => null, "m", Now);

        Assert.AreEqual("tom", picks.Single().Shift.UserId);
    }
}
