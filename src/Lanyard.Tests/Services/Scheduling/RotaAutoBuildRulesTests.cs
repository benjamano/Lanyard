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

    // --- Parties ---

    private static PartyBooking Party(string type, int startHour = 10, int endHour = 12, PartyBookingStatus status = PartyBookingStatus.Booked) => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = 1,
        LocationId = 1,
        PartyType = type,
        ChildName = "Ava",
        StartUtc = RotaTime.ToUtc(Saturday, new TimeOnly(startHour, 0)),
        EndUtc = RotaTime.ToUtc(Saturday, new TimeOnly(endHour, 0)),
        Status = status
    };

    private static readonly Guid PartyHost = Guid.NewGuid();

    private static PartyStaffingRule Rule(string? type, int count, Guid? position = null) => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = 1,
        LocationId = 1,
        PartyType = type,
        StaffPositionId = position ?? PartyHost,
        StaffPosition = new StaffPosition { Id = position ?? PartyHost, CompanyId = 1, Name = "Party Host" },
        StaffCount = count
    };

    // Saturday AM 09:00-14:00 and PM 14:00-19:00, two Hosts each.
    private static List<DemandSlot> SaturdayShifts()
    {
        ShiftTemplate am = Template(DayOfWeek.Saturday, 9, 14, (Host, "Host", 2));
        ShiftTemplate pm = Template(DayOfWeek.Saturday, 14, 19, (Host, "Host", 2));
        pm.Name = "Saturday PM";

        return RotaAutoBuildRules.ExpandTemplates([am, pm], Saturday, Saturday);
    }

    [TestMethod]
    public void ExpandParties_AddsStaffForTheWholeShiftThePartyIsIn()
    {
        List<DemandSlot> shifts = SaturdayShifts();
        PartyBooking party = Party(" lazer + PLAY ");

        (List<DemandSlot> slots, List<PartyBooking> uncovered) = RotaAutoBuildRules.ExpandParties([party], [Rule("Lazer + Play", 2), Rule(null, 1)], shifts);

        DemandSlot slot = slots.Single();
        Assert.AreEqual(0, uncovered.Count);
        Assert.AreEqual(2, slot.Needed);
        Assert.AreEqual(PartyHost, slot.StaffPositionId);
        Assert.AreEqual(party.Id, slot.PartyBookingId);
        Assert.AreEqual(RotaTime.ToUtc(Saturday, new TimeOnly(9, 0)), slot.StartUtc, "the shift's start, not the party's");
        Assert.AreEqual(RotaTime.ToUtc(Saturday, new TimeOnly(14, 0)), slot.EndUtc);
        Assert.AreEqual("Saturday AM · Party: lazer + PLAY", slot.Name, "the type, never the child's name");
    }

    [TestMethod]
    public void ExpandParties_TypeWithoutRules_UsesTheDefault()
    {
        (List<DemandSlot> slots, _) = RotaAutoBuildRules.ExpandParties([Party("Play")], [Rule("Lazer + Play", 2), Rule(null, 1)], SaturdayShifts());

        Assert.AreEqual(1, slots.Single().Needed);
    }

    [TestMethod]
    public void ExpandParties_TwoPartiesInOneShift_AddUp()
    {
        (List<DemandSlot> slots, _) = RotaAutoBuildRules.ExpandParties([Party("Play", 9, 10), Party("Play", 11, 12)], [Rule(null, 1)], SaturdayShifts());

        Assert.AreEqual(2, slots.Sum(x => x.Needed));
        Assert.AreEqual(2, slots.Select(x => x.PartyBookingId).Distinct().Count());
    }

    [TestMethod]
    public void ExpandParties_PartyAcrossTwoShifts_GoesToTheShiftCoveringMostOfIt()
    {
        (List<DemandSlot> slots, _) = RotaAutoBuildRules.ExpandParties([Party("Play", 13, 16)], [Rule(null, 1)], SaturdayShifts());

        Assert.AreEqual("Saturday PM · Party: Play", slots.Single().Name, "1 h in the AM shift, 2 h in the PM one");
    }

    private static PartyStaffingRule HireRule(PrivateHireKind kind, int count, Guid position, string positionName)
    {
        PartyStaffingRule rule = Rule(null, count, position);
        rule.HireKind = kind;
        rule.StaffPosition = new StaffPosition { Id = position, CompanyId = 1, Name = positionName };
        return rule;
    }

    [TestMethod]
    public void ExpandParties_ShiftThatListsThePosition_WinsOverOtherOverlappingShifts()
    {
        // Saturday has a Host evening shift 14:00-21:00 and a late Duty Manager shift 08:00-21:00. A
        // hire's extra Duty Manager joins the late manager shift; its extra Host joins the evening
        // shift, even though the late shift covers the hire just as well.
        ShiftTemplate evening = Template(DayOfWeek.Saturday, 14, 21, (Host, "Host", 1));
        evening.Name = "Saturday evening";
        ShiftTemplate late = Template(DayOfWeek.Saturday, 8, 21, (Duty, "Duty Manager", 1));
        late.Name = "Saturday late manager";
        List<DemandSlot> shifts = RotaAutoBuildRules.ExpandTemplates([evening, late], Saturday, Saturday);

        PartyBooking hire = Party("", 18, 21);
        hire.HireKind = PrivateHireKind.LaserTagAndPlay;

        (List<DemandSlot> slots, _) = RotaAutoBuildRules.ExpandParties([hire],
            [HireRule(PrivateHireKind.LaserTagAndPlay, 1, Duty, "Duty Manager"), HireRule(PrivateHireKind.LaserTagAndPlay, 1, Host, "Host")], shifts);

        Assert.AreEqual(2, slots.Count, "one shift each, not one per overlapping template");
        Assert.AreEqual("Saturday late manager · Private hire: Laser tag + Play", slots.Single(x => x.StaffPositionId == Duty).Name);
        Assert.AreEqual("Saturday evening · Private hire: Laser tag + Play", slots.Single(x => x.StaffPositionId == Host).Name);
    }

    [TestMethod]
    public void ExpandParties_HireUsesOnlyItsOwnLines()
    {
        PartyBooking hire = Party("", 10, 12);
        hire.HireKind = PrivateHireKind.PlayOnly;

        (List<DemandSlot> none, List<PartyBooking> uncovered) = RotaAutoBuildRules.ExpandParties([hire], [Rule(null, 1)], SaturdayShifts());
        Assert.AreEqual(0, none.Count, "the kids' party default doesn't apply to a hire");
        Assert.AreEqual(0, uncovered.Count);

        (List<DemandSlot> slots, _) = RotaAutoBuildRules.ExpandParties([hire],
            [Rule(null, 1), HireRule(PrivateHireKind.PlayOnly, 3, Host, "Host"), HireRule(PrivateHireKind.LaserTagOnly, 5, Host, "Host")], SaturdayShifts());
        Assert.AreEqual(3, slots.Single().Needed);
    }

    [TestMethod]
    public void ExpandParties_NoShiftThen_ReportedNotStaffed()
    {
        PartyBooking evening = Party("Play", 19, 21);

        (List<DemandSlot> slots, List<PartyBooking> uncovered) = RotaAutoBuildRules.ExpandParties([evening], [Rule(null, 1)], SaturdayShifts());

        Assert.AreEqual(0, slots.Count);
        Assert.AreSame(evening, uncovered.Single());
    }

    [TestMethod]
    public void ExpandParties_CancelledOrNoMatchingRules_Nothing()
    {
        Assert.AreEqual(0, RotaAutoBuildRules.ExpandParties([Party("Play", status: PartyBookingStatus.Cancelled)], [Rule(null, 1)], SaturdayShifts()).Slots.Count);

        (List<DemandSlot> slots, List<PartyBooking> uncovered) = RotaAutoBuildRules.ExpandParties([Party("Play")], [Rule("Lazer", 1)], SaturdayShifts());
        Assert.AreEqual(0, slots.Count);
        Assert.AreEqual(0, uncovered.Count, "a party type with no staffing needs no shift, so no warning");
    }

    [TestMethod]
    public void Match_PartyShiftOnlyCountsForItsOwnParty()
    {
        List<DemandSlot> shifts = SaturdayShifts();
        PartyBooking first = Party("Play", 9, 10);
        PartyBooking second = Party("Play", 11, 12);
        (List<DemandSlot> slots, _) = RotaAutoBuildRules.ExpandParties([first, second], [Rule(null, 1)], shifts);
        DateTime start = shifts[0].StartUtc, end = shifts[0].EndUtc;
        Shift forFirst = new() { LocationId = 1, StaffPositionId = PartyHost, StartUtc = start, EndUtc = end, PartyBookingId = first.Id, CreateByUserId = "m", IsActive = true };
        Shift unlinked = new() { LocationId = 1, StaffPositionId = PartyHost, StartUtc = start, EndUtc = end, CreateByUserId = "m", IsActive = true };

        (List<DemandSlot> remaining, int covered, List<Shift> unmatched) = RotaAutoBuildRules.Match(slots, [forFirst, unlinked]);

        Assert.AreEqual(1, covered);
        Assert.AreEqual(second.Id, remaining.Single().PartyBookingId);
        Assert.AreSame(unlinked, unmatched.Single(), "a shift made by hand isn't the party's");
    }

    [TestMethod]
    public void Assign_PartyShiftsCarryThePartyId()
    {
        PartyBooking party = Party("Play");
        (List<DemandSlot> slots, _) = RotaAutoBuildRules.ExpandParties([party], [Rule(null, 1)], SaturdayShifts());

        AutoBuildPick pick = RotaAutoBuildRules.Assign(1, slots, [Worker("Amy", PartyHost)], (_, _) => null, "m", Now).Single();

        Assert.AreEqual(party.Id, pick.Shift.PartyBookingId);
        Assert.AreEqual("amy", pick.Shift.UserId);
    }

    [TestMethod]
    public void Assign_LateFinishTheNightBefore_NotPicked()
    {
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([Template(slots: [(Host, "Host", 1)])], Saturday, Saturday);
        AutoBuildWorker amy = Worker("Amy", Host);
        amy.Shifts.Add(new Shift { Id = Guid.NewGuid(), LocationId = 1, UserId = "amy", CreateByUserId = "m", IsActive = true,
            StartUtc = RotaTime.ToUtc(Saturday.AddDays(-1), new TimeOnly(17, 0)), EndUtc = RotaTime.ToUtc(Saturday.AddDays(-1), new TimeOnly(23, 0)) });

        List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(1, slots, [amy], (_, _) => null, "m", Now);

        Assert.IsTrue(picks.Single().Line.IsOpen);
    }

    [TestMethod]
    public void Assign_SplitShiftSameDay_Allowed()
    {
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([Template(DayOfWeek.Saturday, 15, 18, (Host, "Host", 1))], Saturday, Saturday);
        AutoBuildWorker amy = Worker("Amy", Host);
        amy.Shifts.Add(new Shift { Id = Guid.NewGuid(), LocationId = 1, UserId = "amy", CreateByUserId = "m", IsActive = true,
            StartUtc = RotaTime.ToUtc(Saturday, new TimeOnly(9, 0)), EndUtc = RotaTime.ToUtc(Saturday, new TimeOnly(14, 0)) });

        List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(1, slots, [amy], (_, _) => null, "m", Now);

        Assert.AreEqual("amy", picks.Single().Shift.UserId);
    }

    // --- Regulars and zero headcount ---

    private static ShiftTemplate WithRegulars(ShiftTemplate template, params string[] userIds)
    {
        template.Slots[0].Regulars = userIds
            .Select((id, i) => new ShiftTemplateSlotRegular { ShiftTemplateSlotId = Guid.Empty, UserId = id, SortOrder = i })
            .ToList();
        return template;
    }

    [TestMethod]
    public void Assign_RegularGetsTheirShiftOverABetterRankedPerson()
    {
        // Without a regular, Amy (fewer hours, alphabetically first) would win.
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([WithRegulars(Template(slots: [(Host, "Host", 1)]), "tom")], Saturday, Saturday);

        AutoBuildPick pick = RotaAutoBuildRules.Assign(1, slots, [Worker("Amy", Host), Worker("Tom", Host)], (_, _) => null, "m", Now).Single();

        Assert.AreEqual("tom", pick.Shift.UserId);
        Assert.AreEqual("Regular on this shift", pick.Line.Reason);
    }

    [TestMethod]
    public void Assign_RegularWhoCantWorkIt_SomeoneElsePicked()
    {
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([WithRegulars(Template(slots: [(Host, "Host", 1)]), "tom")], Saturday, Saturday);
        AutoBuildWorker tom = Worker("Tom", Host) with
        {
            Availability = [new StaffAvailability { CompanyId = 1, UserId = "tom", DayOfWeek = DayOfWeek.Saturday, Kind = AvailabilityKind.Unavailable }]
        };

        AutoBuildPick pick = RotaAutoBuildRules.Assign(1, slots, [Worker("Amy", Host), tom], (_, _) => null, "m", Now).Single();

        Assert.AreEqual("amy", pick.Shift.UserId);
    }

    [TestMethod]
    public void Assign_RegularsShiftFilledBeforeAnotherThatCouldUseThem()
    {
        // Rupert is the regular on the 08:30-18:30 manager shift. A harder 08:30-21:30 manager shift
        // (fewer could work it) mustn't take him first.
        ShiftTemplate regular = WithRegulars(Template(DayOfWeek.Saturday, 8, 18, (Duty, "Duty Manager", 1)), "rupert");
        ShiftTemplate late = Template(DayOfWeek.Saturday, 8, 21, (Duty, "Duty Manager", 1));
        late.Name = "Saturday late";
        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([regular, late], Saturday, Saturday);

        List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(1, slots, [Worker("Rupert", Duty), Worker("Ryan", Duty)], (_, _) => null, "m", Now);

        Assert.AreEqual("rupert", picks.Single(x => x.Line.Name == "Saturday AM").Shift.UserId);
        Assert.AreEqual("ryan", picks.Single(x => x.Line.Name == "Saturday late").Shift.UserId);
    }

    [TestMethod]
    public void ZeroHeadcount_NothingNormally_HireAddsToThatShift()
    {
        ShiftTemplate evening = Template(DayOfWeek.Saturday, 14, 21, (Host, "Host", 0));
        evening.Name = "Saturday evening";
        List<DemandSlot> shifts = RotaAutoBuildRules.ExpandTemplates([evening], Saturday, Saturday);

        (List<DemandSlot> remaining, _) = RotaAutoBuildRules.SubtractCovered(shifts, []);
        Assert.AreEqual(0, remaining.Count, "nobody needed without a hire");

        PartyBooking hire = Party("", 18, 21);
        hire.HireKind = PrivateHireKind.PlayOnly;
        (List<DemandSlot> extra, _) = RotaAutoBuildRules.ExpandParties([hire], [HireRule(PrivateHireKind.PlayOnly, 2, Host, "Host")], shifts);

        Assert.AreEqual(2, extra.Single().Needed);
        Assert.AreEqual(RotaTime.ToUtc(Saturday, new TimeOnly(14, 0)), extra.Single().StartUtc);
    }

    [TestMethod]
    public void Assign_HireJoiningALineWithARegular_RegularPickedFirst()
    {
        ShiftTemplate late = WithRegulars(Template(DayOfWeek.Saturday, 8, 21, (Duty, "Duty Manager", 0)), "ryan");
        List<DemandSlot> shifts = RotaAutoBuildRules.ExpandTemplates([late], Saturday, Saturday);
        PartyBooking hire = Party("", 18, 21);
        hire.HireKind = PrivateHireKind.LaserTagAndPlay;

        (List<DemandSlot> extra, _) = RotaAutoBuildRules.ExpandParties([hire], [HireRule(PrivateHireKind.LaserTagAndPlay, 1, Duty, "Duty Manager")], shifts);
        AutoBuildPick pick = RotaAutoBuildRules.Assign(1, extra, [Worker("Amy", Duty), Worker("Ryan", Duty)], (_, _) => null, "m", Now).Single();

        Assert.AreEqual("ryan", pick.Shift.UserId);
    }
}
