using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class ShiftSuggestionRulesTests
{
    // Monday 5 October 2026 - British Summer Time.
    private static readonly DateOnly Week = new(2026, 10, 5);
    private static readonly Guid Host = Guid.NewGuid();
    private static readonly Guid Marshal = Guid.NewGuid();

    private static Shift ShiftOn(DateOnly day, int startHour, int endHour, int breakMinutes = 0, string? userId = null, Guid? positionId = null) => new()
    {
        Id = Guid.NewGuid(),
        LocationId = 1,
        UserId = userId,
        StaffPositionId = positionId,
        CreateByUserId = "m",
        StartUtc = RotaTime.ToUtc(day, new TimeOnly(startHour, 0)),
        EndUtc = endHour > startHour
            ? RotaTime.ToUtc(day, new TimeOnly(endHour, 0))
            : RotaTime.ToUtc(day.AddDays(1), new TimeOnly(endHour, 0)),
        BreakMinutes = breakMinutes
    };

    // The open shift being filled: Wednesday 10:00-16:00 as a Host unless said otherwise.
    private static Shift Open(int dayOffset = 2, int startHour = 10, int endHour = 16, Guid? positionId = null) =>
        ShiftOn(Week.AddDays(dayOffset), startHour, endHour, positionId: positionId ?? Host);

    private static ResolvedContract Contract(int? minShifts = null, decimal? minLength = null, decimal? minHours = null, decimal? maxHours = null) => new(
        new ContractValue<int>(minShifts, minShifts is null ? ContractTier.None : ContractTier.Company),
        new ContractValue<decimal>(minLength, minLength is null ? ContractTier.None : ContractTier.Company),
        new ContractValue<decimal>(minHours, minHours is null ? ContractTier.None : ContractTier.Company),
        new ContractValue<decimal>(maxHours, maxHours is null ? ContractTier.None : ContractTier.Company));

    private static SuggestionCandidate Person(
        string name,
        ResolvedContract? contract = null,
        IEnumerable<Shift>? shifts = null,
        Guid? primary = null,
        string? pendingTimeOff = null,
        bool claimed = false,
        string? cantWork = null,
        IEnumerable<StaffAvailability>? availability = null) =>
        new(name.ToLowerInvariant(), name, primary, primary == Host ? "Host" : primary == Marshal ? "Marshal" : null,
            contract ?? ResolvedContract.Empty, (shifts ?? []).ToList(), pendingTimeOff, claimed, cantWork, availability?.ToList());

    private static StaffAvailability Availability(DayOfWeek day, int startHour, int endHour, AvailabilityKind kind) => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = 1,
        UserId = "u",
        DayOfWeek = day,
        StartTime = new TimeOnly(startHour, 0),
        EndTime = new TimeOnly(endHour, 0),
        Kind = kind
    };

    private static List<string> Order(ShiftSuggestions result) => result.Ranked.Select(x => x.DisplayName).ToList();

    [TestMethod]
    public void Rank_NoPeople_ReturnsEmpty()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), []);

        Assert.AreEqual(0, result.Ranked.Count);
        Assert.AreEqual(0, result.NotSuggested.Count);
    }

    [TestMethod]
    public void Rank_CantWork_ListedUnderNotSuggestedWithReason()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy"),
            Person("Tom", cantWork: "They're already working then.")
        ]);

        CollectionAssert.AreEqual(new List<string> { "Amy" }, Order(result));
        Assert.AreEqual(1, result.NotSuggested.Count);
        Assert.AreEqual("Tom", result.NotSuggested[0].DisplayName);
        Assert.AreEqual("They're already working then.", result.NotSuggested[0].ExcludedReason);
        Assert.AreEqual(0, result.NotSuggested[0].Rank);
        Assert.IsTrue(result.NotSuggested[0].IsExcluded);
    }

    [TestMethod]
    public void Rank_NotSuggested_SortedByName()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Zoe", cantWork: "They're booked off that day."),
            Person("Amy", cantWork: "They're not at this location.")
        ]);

        CollectionAssert.AreEqual(new List<string> { "Amy", "Zoe" }, result.NotSuggested.Select(x => x.DisplayName).ToList());
    }

    [TestMethod]
    public void Rank_RanksAreOneBasedInOrder()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [Person("Bea"), Person("Amy"), Person("Cal")]);

        CollectionAssert.AreEqual(new List<int> { 1, 2, 3 }, result.Ranked.Select(x => x.Rank).ToList());
    }

    [TestMethod]
    public void Rank_EqualPeople_OrderedByNameThenId()
    {
        SuggestionCandidate first = new("b-id", "Sam", null, null, ResolvedContract.Empty, []);
        SuggestionCandidate second = new("a-id", "Sam", null, null, ResolvedContract.Empty, []);

        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [first, Person("amy"), second]);

        CollectionAssert.AreEqual(new List<string> { "amy", "a-id", "b-id" }, result.Ranked.Select(x => x.UserId).ToList());
    }

    [TestMethod]
    public void Rank_BelowMinimumShifts_RanksFirstWithReason()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", primary: Host),
            Person("Tom", contract: Contract(minShifts: 2))
        ]);

        CollectionAssert.AreEqual(new List<string> { "Tom", "Amy" }, Order(result));
        SuggestionReason reason = result.Ranked[0].Reasons.Single(x => x.Kind == SuggestionReasonKind.NeedsShifts);
        Assert.AreEqual("Needs 2 more shifts this week", reason.Text);
    }

    [TestMethod]
    public void Rank_NeedsOneShift_SingularWording()
    {
        Shift monday = ShiftOn(Week, 9, 17, userId: "tom");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [Person("Tom", contract: Contract(minShifts: 2), shifts: [monday])]);

        Assert.AreEqual("Needs 1 more shift this week", result.Ranked[0].Reasons.Single(x => x.Kind == SuggestionReasonKind.NeedsShifts).Text);
    }

    [TestMethod]
    public void Rank_ShiftTooShortForMinimumLength_DoesNotCountAsHelping()
    {
        // Tom needs a shift of 8 h or more; this one is 6 h, so it doesn't help him.
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", primary: Host),
            Person("Tom", contract: Contract(minShifts: 1, minLength: 8))
        ]);

        CollectionAssert.AreEqual(new List<string> { "Amy", "Tom" }, Order(result));
        Assert.IsFalse(result.Ranked[1].Reasons.Any(x => x.Kind == SuggestionReasonKind.NeedsShifts));
    }

    [TestMethod]
    public void Rank_BelowMinimumHours_RanksFirstWithReason()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", primary: Host),
            Person("Tom", contract: Contract(minHours: 16))
        ]);

        CollectionAssert.AreEqual(new List<string> { "Tom", "Amy" }, Order(result));
        Assert.AreEqual("Below their 16 h weekly minimum", result.Ranked[0].Reasons.Single(x => x.Kind == SuggestionReasonKind.BelowMinHours).Text);
    }

    [TestMethod]
    public void Rank_MinimumAlreadyMet_NoContractReason()
    {
        Shift monday = ShiftOn(Week, 9, 17, userId: "tom");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [Person("Tom", contract: Contract(minShifts: 1, minHours: 8), shifts: [monday])]);

        Assert.AreEqual(0, result.Ranked[0].Reasons.Count);
    }

    [TestMethod]
    public void Rank_WouldGoOverMaximum_SinksBelowEveryoneButStaysListed()
    {
        Shift monday = ShiftOn(Week, 9, 17, userId: "tom");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Tom", contract: Contract(minShifts: 3, maxHours: 10), shifts: [monday], claimed: true, primary: Host),
            Person("Zoe")
        ]);

        CollectionAssert.AreEqual(new List<string> { "Zoe", "Tom" }, Order(result));
        SuggestionReason caution = result.Ranked[1].Reasons.Single(x => x.Kind == SuggestionReasonKind.OverMaxHours);
        Assert.AreEqual("Would go over their 10 h weekly maximum", caution.Text);
        Assert.IsTrue(caution.IsCaution);
        Assert.AreEqual(14m, result.Ranked[1].WeekHoursAfter);
        Assert.AreEqual(10m, result.Ranked[1].MaxHours);
    }

    [TestMethod]
    public void Rank_ExactlyReachingMaximum_IsNotOver()
    {
        Shift monday = ShiftOn(Week, 9, 13, userId: "tom");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [Person("Tom", contract: Contract(maxHours: 10), shifts: [monday])]);

        Assert.IsFalse(result.Ranked[0].Reasons.Any(x => x.Kind == SuggestionReasonKind.OverMaxHours));
    }

    [TestMethod]
    public void Rank_PendingTimeOff_DemotedAndFlagged()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", pendingTimeOff: "Paid holiday", contract: Contract(minShifts: 1)),
            Person("Zoe")
        ]);

        CollectionAssert.AreEqual(new List<string> { "Zoe", "Amy" }, Order(result));
        Assert.AreEqual("Has asked for paid holiday that day (not decided yet)",
            result.Ranked[1].Reasons.Single(x => x.Kind == SuggestionReasonKind.PendingTimeOff).Text);
    }

    [TestMethod]
    public void Rank_OverMaximumRanksBelowPendingTimeOff()
    {
        Shift monday = ShiftOn(Week, 9, 17, userId: "tom");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Tom", contract: Contract(maxHours: 8), shifts: [monday]),
            Person("Amy", pendingTimeOff: "Paid holiday")
        ]);

        CollectionAssert.AreEqual(new List<string> { "Amy", "Tom" }, Order(result));
    }

    [TestMethod]
    public void Rank_ShortRestAfterPreviousShift_DemotedAndFlagged()
    {
        // Tuesday 18:00-03:00 then Wednesday 10:00: 7 h of rest.
        Shift lateNight = ShiftOn(Week.AddDays(1), 18, 3, userId: "amy");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", shifts: [lateNight], contract: Contract(minShifts: 2)),
            Person("Zoe")
        ]);

        CollectionAssert.AreEqual(new List<string> { "Zoe", "Amy" }, Order(result));
        Assert.AreEqual("Only 7 h after their previous shift", result.Ranked[1].Reasons.Single(x => x.Kind == SuggestionReasonKind.ShortRest).Text);
    }

    [TestMethod]
    public void Rank_ShortRestBeforeNextShift_Flagged()
    {
        // Wednesday 10:00-16:00, then Thursday 01:00: 9 h of rest.
        Shift early = ShiftOn(Week.AddDays(3), 1, 5, userId: "amy");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [Person("Amy", shifts: [early])]);

        Assert.AreEqual("Only 9 h before their next shift", result.Ranked[0].Reasons.Single(x => x.Kind == SuggestionReasonKind.ShortRest).Text);
    }

    [TestMethod]
    public void Rank_ElevenHoursRest_IsEnough()
    {
        // Tuesday ends 23:00, Wednesday starts 10:00: exactly 11 h.
        Shift tuesday = ShiftOn(Week.AddDays(1), 15, 23, userId: "amy");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [Person("Amy", shifts: [tuesday])]);

        Assert.IsFalse(result.Ranked[0].Reasons.Any(x => x.Kind == SuggestionReasonKind.ShortRest));
    }

    [TestMethod]
    public void Rank_ShiftEndingWhenThisStarts_IsShortRestNotIgnored()
    {
        // Back-to-back shifts aren't a clash (WhyCantWork allows them) but leave no rest.
        Shift morning = ShiftOn(Week.AddDays(2), 6, 10, userId: "amy");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [Person("Amy", shifts: [morning])]);

        Assert.AreEqual("Only 0 h after their previous shift", result.Ranked[0].Reasons.Single(x => x.Kind == SuggestionReasonKind.ShortRest).Text);
    }

    [TestMethod]
    public void Rank_ContractNeedBeatsAskingForTheShift()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", claimed: true),
            Person("Tom", contract: Contract(minShifts: 1))
        ]);

        CollectionAssert.AreEqual(new List<string> { "Tom", "Amy" }, Order(result));
    }

    [TestMethod]
    public void Rank_AskedForShift_BeatsUsualPosition()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", primary: Host),
            Person("Tom", claimed: true)
        ]);

        CollectionAssert.AreEqual(new List<string> { "Tom", "Amy" }, Order(result));
        Assert.IsTrue(result.Ranked[0].AskedForShift);
        Assert.AreEqual("Asked for this shift", result.Ranked[0].Reasons.Single().Text);
    }

    [TestMethod]
    public void Rank_UsualPosition_BeatsFewerHours()
    {
        Shift monday = ShiftOn(Week, 9, 17, userId: "amy");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Tom", primary: Marshal),
            Person("Amy", primary: Host, shifts: [monday])
        ]);

        CollectionAssert.AreEqual(new List<string> { "Amy", "Tom" }, Order(result));
        Assert.AreEqual("Usually works as Host", result.Ranked[0].Reasons.Single().Text);
    }

    [TestMethod]
    public void Rank_OpenShiftWithoutPosition_NobodyGetsUsualPosition()
    {
        Shift open = Open();
        open.StaffPositionId = null;

        ShiftSuggestions result = ShiftSuggestionRules.Rank(open, [Person("Amy", primary: Host)]);

        Assert.AreEqual(0, result.Ranked[0].Reasons.Count);
    }

    [TestMethod]
    public void Rank_FewerHoursForTheirContract_RanksHigher()
    {
        // Amy: 8 h of a 40 h max (20%). Tom: 8 h of a 16 h max (50%).
        Shift amyMonday = ShiftOn(Week, 9, 17, userId: "amy");
        Shift tomMonday = ShiftOn(Week, 9, 17, userId: "tom");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Tom", contract: Contract(maxHours: 16), shifts: [tomMonday]),
            Person("Amy", contract: Contract(maxHours: 40), shifts: [amyMonday])
        ]);

        CollectionAssert.AreEqual(new List<string> { "Amy", "Tom" }, Order(result));
    }

    [TestMethod]
    public void Rank_MinimumOnlyContract_MeasuredAgainstMinimum()
    {
        // Amy has met her 8 h minimum (100%); Tom has 4 h with no contract, measured against the
        // team's usual 8 h (50%).
        Shift amyMonday = ShiftOn(Week, 9, 17, userId: "amy");
        Shift tomMonday = ShiftOn(Week, 9, 13, userId: "tom");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", contract: Contract(minHours: 8), shifts: [amyMonday]),
            Person("Tom", shifts: [tomMonday])
        ]);

        CollectionAssert.AreEqual(new List<string> { "Tom", "Amy" }, Order(result));
    }

    [TestMethod]
    public void Rank_NoContracts_FewestHoursFirst()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", shifts: [ShiftOn(Week, 9, 17, userId: "amy")]),
            Person("Tom", shifts: [ShiftOn(Week, 9, 12, userId: "tom")]),
            Person("Zoe")
        ]);

        CollectionAssert.AreEqual(new List<string> { "Zoe", "Tom", "Amy" }, Order(result));
    }

    [TestMethod]
    public void Rank_HoursOutsideTheWeek_DontCount()
    {
        Shift lastWeek = ShiftOn(Week.AddDays(-1), 9, 17, userId: "amy");
        Shift nextWeek = ShiftOn(Week.AddDays(7), 9, 17, userId: "amy");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [Person("Amy", shifts: [lastWeek, nextWeek])]);

        Assert.AreEqual(0m, result.Ranked[0].WeekHoursBefore);
        Assert.AreEqual(6m, result.Ranked[0].WeekHoursAfter);
    }

    [TestMethod]
    public void Rank_BreakCountsAgainstHours()
    {
        Shift open = ShiftOn(Week.AddDays(2), 10, 16, breakMinutes: 30, positionId: Host);

        ShiftSuggestions result = ShiftSuggestionRules.Rank(open, [Person("Amy")]);

        Assert.AreEqual(5.5m, result.Ranked[0].WeekHoursAfter);
    }

    [TestMethod]
    public void Rank_InactiveAndSameShift_Ignored()
    {
        Shift open = Open();
        Shift removed = ShiftOn(Week, 9, 17, userId: "amy");
        removed.IsActive = false;
        Shift sameShift = new() { Id = open.Id, LocationId = 1, UserId = "amy", CreateByUserId = "m", StartUtc = open.StartUtc, EndUtc = open.EndUtc };

        ShiftSuggestions result = ShiftSuggestionRules.Rank(open, [Person("Amy", shifts: [removed, sameShift])]);

        Assert.AreEqual(0m, result.Ranked[0].WeekHoursBefore);
        Assert.AreEqual(0, result.Ranked[0].Reasons.Count);
    }

    [TestMethod]
    public void Rank_ClocksGoBack_ShiftAcrossTheChangeCountsRealHours()
    {
        // Sunday 25 October 2026, 00:00-04:00 local: the clocks go back at 02:00, so it's 5 h.
        DateOnly sunday = new(2026, 10, 25);
        Shift open = ShiftOn(sunday, 0, 4, positionId: Host);

        ShiftSuggestions result = ShiftSuggestionRules.Rank(open, [Person("Amy")]);

        Assert.AreEqual(5m, result.Ranked[0].WeekHoursAfter);
    }

    [TestMethod]
    public void Rank_ClocksGoForward_WeekIsTheLocalMondayWeek()
    {
        // Sunday 29 March 2026 is the last day of the week starting Monday 23 March, even though
        // the clocks went forward that morning. A Monday 23 March shift is in the same week; one
        // on Monday 30 March is not.
        DateOnly sunday = new(2026, 3, 29);
        Shift open = ShiftOn(sunday, 10, 14, positionId: Host);
        Shift sameWeek = ShiftOn(new DateOnly(2026, 3, 23), 9, 17, userId: "amy");
        Shift nextWeek = ShiftOn(new DateOnly(2026, 3, 30), 9, 17, userId: "amy");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(open, [Person("Amy", shifts: [sameWeek, nextWeek])]);

        Assert.AreEqual(8m, result.Ranked[0].WeekHoursBefore);
    }

    [TestMethod]
    public void Rank_OvernightShiftOnSunday_BelongsToItsStartWeek()
    {
        // Sunday 22:00 to Monday 04:00 is judged on the week it starts in, and Monday 15:00 after it
        // leaves exactly 11 h.
        Shift open = ShiftOn(Week.AddDays(6), 22, 4, positionId: Host);
        Shift monday = ShiftOn(Week, 9, 17, userId: "amy");
        Shift nextMonday = ShiftOn(Week.AddDays(7), 15, 20, userId: "amy");

        ShiftSuggestions result = ShiftSuggestionRules.Rank(open, [Person("Amy", shifts: [monday, nextMonday])]);

        Assert.AreEqual(8m, result.Ranked[0].WeekHoursBefore);
        Assert.IsFalse(result.Ranked[0].Reasons.Any(x => x.Kind == SuggestionReasonKind.ShortRest));
    }

    [TestMethod]
    public void TeamWeeklyHours_IsMedianOfOwnHours()
    {
        Assert.AreEqual(ShiftSuggestionRules.DefaultWeeklyHours, ShiftSuggestionRules.TeamWeeklyHours([ResolvedContract.Empty]));
        Assert.AreEqual(20m, ShiftSuggestionRules.TeamWeeklyHours([Contract(maxHours: 10), Contract(maxHours: 20), Contract(minHours: 30)]));
        Assert.AreEqual(15m, ShiftSuggestionRules.TeamWeeklyHours([Contract(maxHours: 10), Contract(maxHours: 20), ResolvedContract.Empty]));
    }

    [TestMethod]
    public void WeeklyHoursFor_PrefersMaximumThenMinimumThenTeam()
    {
        Assert.AreEqual(30m, ShiftSuggestionRules.WeeklyHoursFor(Contract(minHours: 10, maxHours: 30), 20m));
        Assert.AreEqual(10m, ShiftSuggestionRules.WeeklyHoursFor(Contract(minHours: 10), 20m));
        Assert.AreEqual(20m, ShiftSuggestionRules.WeeklyHoursFor(ResolvedContract.Empty, 20m));
    }

    // --- Availability ---

    [TestMethod]
    public void Rank_SaidTheyCantWorkThen_NotSuggestedWithTheirWords()
    {
        // The open shift is Wednesday 10:00-16:00.
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", contract: Contract(minShifts: 3), availability: [Availability(DayOfWeek.Wednesday, 8, 13, AvailabilityKind.Unavailable)]),
            Person("Tom")
        ]);

        CollectionAssert.AreEqual(new List<string> { "Tom" }, Order(result));
        Assert.AreEqual("They've said they can't work Wednesdays, 08:00–13:00.", result.NotSuggested.Single().ExcludedReason);
    }

    [TestMethod]
    public void Rank_CantWorkAnotherTime_StillSuggested()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", availability: [Availability(DayOfWeek.Wednesday, 17, 23, AvailabilityKind.Unavailable), Availability(DayOfWeek.Sunday, 0, 0, AvailabilityKind.Unavailable)])
        ]);

        Assert.AreEqual(1, result.Ranked.Count);
        Assert.AreEqual(0, result.NotSuggested.Count);
    }

    [TestMethod]
    public void Rank_WhyCantWorkReasonWinsOverAvailability()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", cantWork: "They're booked off that day.", availability: [Availability(DayOfWeek.Wednesday, 0, 0, AvailabilityKind.Unavailable)])
        ]);

        Assert.AreEqual("They're booked off that day.", result.NotSuggested.Single().ExcludedReason);
    }

    [TestMethod]
    public void Rank_PrefersThatTime_RanksAboveAskingAndUsualPosition()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", claimed: true, primary: Host),
            Person("Tom", availability: [Availability(DayOfWeek.Wednesday, 9, 17, AvailabilityKind.Preferred)])
        ]);

        CollectionAssert.AreEqual(new List<string> { "Tom", "Amy" }, Order(result));
        SuggestionReason reason = result.Ranked[0].Reasons.Single();
        Assert.AreEqual(SuggestionReasonKind.PreferredTime, reason.Kind);
        Assert.AreEqual("Would rather work Wednesdays, 09:00–17:00", reason.Text);
        Assert.IsFalse(reason.IsCaution);
    }

    [TestMethod]
    public void Rank_ContractNeedStillBeatsPreference()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", availability: [Availability(DayOfWeek.Wednesday, 9, 17, AvailabilityKind.Preferred)]),
            Person("Tom", contract: Contract(minShifts: 1))
        ]);

        CollectionAssert.AreEqual(new List<string> { "Tom", "Amy" }, Order(result));
    }

    [TestMethod]
    public void Rank_PreferredWindowOnlyCoveringPartOfTheShift_DoesNotCount()
    {
        ShiftSuggestions result = ShiftSuggestionRules.Rank(Open(), [
            Person("Amy", availability: [Availability(DayOfWeek.Wednesday, 8, 12, AvailabilityKind.Preferred)])
        ]);

        Assert.AreEqual(0, result.Ranked[0].Reasons.Count);
    }
}
