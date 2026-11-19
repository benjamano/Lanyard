using Lanyard.Application.Services.Insights;
using Lanyard.Infrastructure.DTO.Insights;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Insights;

// The bucketing and flag rules on hand-built facts - no database.
[TestClass]
public class InsightsRulesTests
{
    // Monday 5 October 2026 is British Summer Time; the clocks go back on Sunday 25 October 2026
    // and forward on Sunday 29 March 2026.
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private static readonly DateOnly ClocksBack = new(2026, 10, 25);
    private static readonly DateOnly ClocksForward = new(2026, 3, 29);

    private static DateTime Local(DateOnly day, int hour, int minute = 0) => RotaTime.ToUtc(day, new TimeOnly(hour, minute));

    private static InsightsFigures Calculate(DateOnly from, DateOnly to,
        IReadOnlyCollection<GameFact>? games = null,
        IReadOnlyCollection<WorkSpan>? clocked = null,
        IReadOnlyCollection<WorkSpan>? rota = null,
        IReadOnlyCollection<LocationOpeningHours>? hours = null,
        IReadOnlyCollection<MusicDaypart>? dayparts = null,
        IReadOnlyCollection<PartyFact>? parties = null,
        int? laserTagOffsetMinutes = null) =>
        InsightsRules.Calculate(from, to, games ?? [], clocked ?? [], rota ?? [], hours ?? [], dayparts ?? [], parties, laserTagOffsetMinutes);

    // A ten-minute game that started at the given time (games are recorded when they end).
    private static GameFact GameStarting(DateTime startUtc, int players = 6) => new(startUtc.AddMinutes(10), 600, players);

    private static PartyFact Party(DateTime startUtc, DateTime endUtc, DateTime? laserUtc = null, int children = 10, PrivateHireKind? hire = null) =>
        new(startUtc, endUtc, laserUtc, hire, children);

    private static LocationOpeningHours Hours(DayOfWeek day, int opens, int closes) => new()
    {
        LocationId = 1,
        DayOfWeek = day,
        OpensAt = new TimeOnly(opens, 0),
        ClosesAt = new TimeOnly(closes, 0),
        IsActive = true
    };

    private static List<LocationOpeningHours> EveryDay(int opens, int closes) =>
        [.. Enum.GetValues<DayOfWeek>().Select(d => Hours(d, opens, closes))];

    // A cell with no party hours: its rate basis is the whole cell.
    private static InsightsCell Cell(int samples, int games, decimal clocked, int partySamples = 0) =>
        new(DayOfWeek.Saturday, 14, samples, games, games * 6, clocked, clocked, StaffingFlag.None,
            0, partySamples, 0, samples - partySamples, games, clocked);

    // ---- Splitting time into hours ----

    [TestMethod]
    public void SplitIntoHours_CutsAtEachHourBoundary()
    {
        DateTime start = new(2026, 10, 5, 13, 30, 0, DateTimeKind.Utc);
        DateTime end = new(2026, 10, 5, 15, 15, 0, DateTimeKind.Utc);

        List<(DateTime HourStartUtc, decimal Hours)> pieces = [.. InsightsRules.SplitIntoHours(start, end)];

        CollectionAssert.AreEqual(new[] { 0.5m, 1m, 0.25m }, pieces.Select(x => x.Hours).ToArray());
        Assert.AreEqual(new DateTime(2026, 10, 5, 13, 0, 0, DateTimeKind.Utc), pieces[0].HourStartUtc);
    }

    [TestMethod]
    public void SplitIntoHours_ReturnsNothingForAnEmptySpan()
    {
        DateTime at = new(2026, 10, 5, 13, 0, 0, DateTimeKind.Utc);

        Assert.AreEqual(0, InsightsRules.SplitIntoHours(at, at).Count());
        Assert.AreEqual(0, InsightsRules.SplitIntoHours(at, at.AddHours(-1)).Count());
    }

    [TestMethod]
    public void Calculate_BucketsByUkTimeNotUtc()
    {
        // 09:00-10:00 BST is 08:00-09:00 UTC.
        InsightsFigures figures = Calculate(Monday, Monday, clocked: [new WorkSpan(Local(Monday, 9), Local(Monday, 10))]);

        InsightsSlot slot = figures.Slots.Single();
        Assert.AreEqual(9, slot.Hour);
        Assert.AreEqual(1m, slot.ClockedHours);
    }

    [TestMethod]
    public void Calculate_SplitsAShiftAcrossMidnightOntoBothDays()
    {
        DateOnly friday = Monday.AddDays(4);
        InsightsFigures figures = Calculate(friday, friday.AddDays(1),
            clocked: [new WorkSpan(Local(friday, 22), Local(friday.AddDays(1), 2))]);

        Assert.AreEqual(2m, figures.Days.Single(x => x.Date == friday).ClockedHours);
        Assert.AreEqual(2m, figures.Days.Single(x => x.Date == friday.AddDays(1)).ClockedHours);
    }

    [TestMethod]
    public void Calculate_ClipsTimeOutsideTheRange()
    {
        // Clocked in the evening before the range starts.
        InsightsFigures figures = Calculate(Monday, Monday,
            clocked: [new WorkSpan(Local(Monday.AddDays(-1), 22), Local(Monday, 3))]);

        Assert.AreEqual(3m, figures.Totals.ClockedHours);
    }

    [TestMethod]
    public void Calculate_AddsBothRepeatedHoursTogetherWhenTheClocksGoBack()
    {
        // 00:00-03:00 on the night the clocks go back is four real hours.
        InsightsFigures figures = Calculate(ClocksBack, ClocksBack,
            clocked: [new WorkSpan(Local(ClocksBack, 0), Local(ClocksBack, 3))]);

        Assert.AreEqual(4m, figures.Totals.ClockedHours);
        Assert.AreEqual(2m, figures.Slots.Single(x => x.Hour == 1).ClockedHours);
        Assert.AreEqual(1, figures.Slots.Count(x => x.Hour == 1));
    }

    [TestMethod]
    public void Calculate_HasNoOneAmWhenTheClocksGoForward()
    {
        // 00:00-03:00 on the night the clocks go forward is two real hours, and 01:00 never happens.
        InsightsFigures figures = Calculate(ClocksForward, ClocksForward,
            clocked: [new WorkSpan(Local(ClocksForward, 0), Local(ClocksForward, 3))]);

        Assert.AreEqual(2m, figures.Totals.ClockedHours);
        Assert.IsFalse(figures.Slots.Any(x => x.Hour == 1));
        CollectionAssert.AreEqual(new[] { 0, 2 }, figures.Slots.Select(x => x.Hour).ToArray());
    }

    // ---- Open entries, rota breaks and game starts ----

    [TestMethod]
    public void EffectiveClockOut_UsesTheRealClockOutWhenThereIsOne()
    {
        DateTime clockIn = Local(Monday, 9);

        Assert.AreEqual(clockIn.AddHours(20), InsightsRules.EffectiveClockOutUtc(clockIn, clockIn.AddHours(20), clockIn.AddHours(2), TimeSpan.FromHours(16)));
    }

    [TestMethod]
    public void EffectiveClockOut_RunsAnOpenEntryUntilNow()
    {
        DateTime clockIn = Local(Monday, 9);

        Assert.AreEqual(clockIn.AddHours(3), InsightsRules.EffectiveClockOutUtc(clockIn, null, clockIn.AddHours(3), TimeSpan.FromHours(16)));
    }

    [TestMethod]
    public void EffectiveClockOut_CapsAForgottenClockOut()
    {
        DateTime clockIn = Local(Monday, 9);

        Assert.AreEqual(clockIn.AddHours(16), InsightsRules.EffectiveClockOutUtc(clockIn, null, clockIn.AddDays(3), TimeSpan.FromHours(16)));
    }

    [TestMethod]
    public void RotaSpan_SpreadsTheBreakSoHoursMatchPaidHours()
    {
        Shift shift = new()
        {
            LocationId = 1,
            StartUtc = Local(Monday, 9),
            EndUtc = Local(Monday, 17),
            BreakMinutes = 30,
            CreateByUserId = "manager"
        };

        InsightsFigures figures = Calculate(Monday, Monday, rota: [InsightsRules.RotaSpan(shift.StartUtc, shift.EndUtc, shift.BreakMinutes)]);

        Assert.AreEqual(shift.PaidHours, figures.Totals.RotaHours);
        Assert.AreEqual(8, figures.Slots.Count(x => x.RotaHours > 0));
    }

    [TestMethod]
    public void Calculate_PutsAGameInTheHourItStarted()
    {
        // Recorded at 15:05 after a 15 minute game, so it started at 14:50.
        InsightsFigures figures = Calculate(Monday, Monday, games: [new GameFact(Local(Monday, 15, 5), 900, 8)]);

        InsightsSlot slot = figures.Slots.Single();
        Assert.AreEqual(14, slot.Hour);
        Assert.AreEqual(1, slot.Games);
        Assert.AreEqual(8, slot.Players);
    }

    [TestMethod]
    public void Calculate_IgnoresAGameThatStartedBeforeTheRange()
    {
        // Started at 23:55 the night before, recorded just after midnight.
        InsightsFigures figures = Calculate(Monday, Monday, games: [new GameFact(Local(Monday, 0, 5), 600, 4)]);

        Assert.AreEqual(0, figures.Totals.Games);
    }

    [TestMethod]
    public void EstimatedStart_IgnoresAnImplausiblyLongDuration()
    {
        DateTime playedAt = Local(Monday, 15);

        Assert.AreEqual(playedAt - InsightsRules.MaxGameLength, InsightsRules.EstimatedStartUtc(new GameFact(playedAt, 999_999, 4)));
        Assert.AreEqual(playedAt, InsightsRules.EstimatedStartUtc(new GameFact(playedAt, -5, 4)));
    }

    // ---- Opening hours and dayparts ----

    [TestMethod]
    public void Calculate_MarksHoursOpenFromOpeningHours()
    {
        InsightsFigures figures = Calculate(Monday, Monday, hours: [Hours(DayOfWeek.Monday, 10, 18)]);

        CollectionAssert.AreEqual(Enumerable.Range(10, 8).ToArray(), figures.Slots.Where(x => x.IsOpen).Select(x => x.Hour).ToArray());
        Assert.AreEqual(8, figures.Days.Single().OpenHours);
    }

    [TestMethod]
    public void Calculate_KeepsStaffTimeOutsideOpeningHoursButApartFromOpenTime()
    {
        // Setting up from 09:00 for a 10:00 opening.
        InsightsFigures figures = Calculate(Monday, Monday,
            clocked: [new WorkSpan(Local(Monday, 9), Local(Monday, 12))],
            hours: [Hours(DayOfWeek.Monday, 10, 18)]);

        Assert.AreEqual(3m, figures.Totals.ClockedHours);
        Assert.AreEqual(2m, figures.Totals.ClockedHoursWhileOpen);
        Assert.IsTrue(figures.Slots.Any(x => x.Hour == 9 && !x.IsOpen));
    }

    [TestMethod]
    public void Calculate_WithNoOpeningHours_TreatsOnlyBusyHoursAsOpen()
    {
        InsightsFigures figures = Calculate(Monday, Monday, games: [new GameFact(Local(Monday, 14, 20), 900, 6)]);

        Assert.AreEqual(1, figures.Slots.Count(x => x.IsOpen));
        Assert.AreEqual(1, figures.Cells.Single().Samples);
    }

    [TestMethod]
    public void Calculate_LabelsHoursWithTheirDaypart()
    {
        MusicDaypart evening = new()
        {
            Name = "Evening",
            DaysOfWeek = "1",
            StartTime = new TimeOnly(17, 0),
            EndTime = new TimeOnly(22, 0),
            IsActive = true
        };

        InsightsFigures figures = Calculate(Monday, Monday,
            games: [new GameFact(Local(Monday, 18, 20), 900, 6), new GameFact(Local(Monday, 12, 20), 900, 6)],
            hours: [Hours(DayOfWeek.Monday, 10, 22)],
            dayparts: [evening]);

        Assert.AreEqual("Evening", figures.Slots.Single(x => x.Hour == 18).Daypart);
        Assert.IsNull(figures.Slots.Single(x => x.Hour == 12).Daypart);
        Assert.AreEqual(1, figures.Dayparts.Single().Games);
    }

    // ---- Median and flags ----

    [TestMethod]
    public void Median_HandlesOddEvenAndEmpty()
    {
        Assert.AreEqual(2m, InsightsRules.Median([3m, 1m, 2m]));
        Assert.AreEqual(2.5m, InsightsRules.Median([4m, 1m, 2m, 3m]));
        Assert.IsNull(InsightsRules.Median([]));
    }

    [TestMethod]
    public void Flag_NeedsEnoughOccurrences()
    {
        Assert.AreEqual(StaffingFlag.InsufficientData, InsightsRules.Flag(Cell(2, 40, 2m), 2m));
    }

    [TestMethod]
    public void Flag_CallsBusyHoursUnderstaffed()
    {
        // 30 games over 10 staff-hours is 3 an hour against a typical 2.
        Assert.AreEqual(StaffingFlag.Understaffed, InsightsRules.Flag(Cell(4, 30, 10m), 2m));
        Assert.AreEqual(StaffingFlag.None, InsightsRules.Flag(Cell(4, 28, 10m), 2m));
    }

    [TestMethod]
    public void Flag_CallsGamesWithNobodyClockedInUnderstaffed()
    {
        Assert.AreEqual(StaffingFlag.Understaffed, InsightsRules.Flag(Cell(4, InsightsRules.MinCellGames, 0m), 2m));
        Assert.AreEqual(StaffingFlag.None, InsightsRules.Flag(Cell(4, InsightsRules.MinCellGames - 1, 0m), 2m));
    }

    [TestMethod]
    public void Flag_TreatsAFewClockedMinutesAsNobodyOn()
    {
        // 10 games over 4 weeks with someone clocked in for 6 minutes a time: no rate to speak of.
        InsightsCell cell = Cell(4, 10, 0.4m);

        Assert.IsNull(cell.GamesPerStaffHour);
        Assert.AreEqual(StaffingFlag.Understaffed, InsightsRules.Flag(cell, 2m));
        Assert.IsFalse(InsightsRules.IsTypicalRateSample(cell));
    }

    [TestMethod]
    public void Flag_DoesNotCallAFewGamesBusy()
    {
        // 4 games over 1 staff-hour is a high rate, but too few games to mean anything.
        Assert.AreEqual(StaffingFlag.None, InsightsRules.Flag(Cell(4, 4, 1m), 1m));
    }

    [TestMethod]
    public void Flag_CallsQuietHoursWithSpareStaffOverstaffed()
    {
        // 4 games over 8 staff-hours (2 people on) is 0.5 an hour against a typical 2.
        Assert.AreEqual(StaffingFlag.Overstaffed, InsightsRules.Flag(Cell(4, 4, 8m), 2m));
    }

    [TestMethod]
    public void Flag_DoesNotCallOnePersonOverstaffed()
    {
        // Quiet, but with one person on there's nobody to send home.
        Assert.AreEqual(StaffingFlag.None, InsightsRules.Flag(Cell(4, 0, 4m), 2m));
    }

    [TestMethod]
    public void Calculate_NeedsTwoWeeksForFlags()
    {
        List<GameFact> games = [.. Enumerable.Range(0, 40).Select(i => new GameFact(Local(Monday, 12 + i % 6, 30), 600, 6))];

        InsightsFigures figures = Calculate(Monday, Monday.AddDays(6), games: games,
            clocked: [new WorkSpan(Local(Monday, 10), Local(Monday, 20))], hours: EveryDay(10, 20));

        Assert.IsFalse(figures.FlagsAvailable);
        Assert.IsNotNull(figures.FlagsUnavailableReason);
        Assert.IsTrue(figures.Cells.All(x => x.Flag == StaffingFlag.InsufficientData));
    }

    [TestMethod]
    public void Calculate_NeedsEnoughGamesForFlags()
    {
        InsightsFigures figures = Calculate(Monday, Monday.AddDays(20),
            games: [new GameFact(Local(Monday, 14, 30), 600, 6)],
            clocked: [new WorkSpan(Local(Monday, 10), Local(Monday, 20))], hours: EveryDay(10, 20));

        Assert.IsFalse(figures.FlagsAvailable);
        StringAssert.Contains(figures.FlagsUnavailableReason, InsightsRules.MinFlagGames.ToString());
    }

    [TestMethod]
    public void Calculate_FlagsTheBusySaturdayAfternoonOverFourWeeks()
    {
        // Four weeks, open 10:00-20:00, one person on all day every day. Every hour has two games,
        // except Saturday 14:00, which has eight.
        DateOnly from = Monday;
        DateOnly to = Monday.AddDays(27);
        List<GameFact> games = [];
        List<WorkSpan> clocked = [];

        for (DateOnly day = from; day <= to; day = day.AddDays(1))
        {
            clocked.Add(new WorkSpan(Local(day, 10), Local(day, 20)));

            for (int hour = 10; hour < 20; hour++)
            {
                int count = day.DayOfWeek == DayOfWeek.Saturday && hour == 14 ? 8 : 2;

                for (int i = 0; i < count; i++)
                {
                    games.Add(new GameFact(Local(day, hour, 15 + i * 5), 300, 6));
                }
            }
        }

        InsightsFigures figures = Calculate(from, to, games, clocked, hours: EveryDay(10, 20));

        Assert.IsTrue(figures.FlagsAvailable);
        Assert.AreEqual(2m, figures.MedianGamesPerStaffHour);

        InsightsCell busy = figures.FlaggedCells().First();
        Assert.AreEqual(DayOfWeek.Saturday, busy.Day);
        Assert.AreEqual(14, busy.Hour);
        Assert.AreEqual(StaffingFlag.Understaffed, busy.Flag);
        Assert.AreEqual(4, busy.Samples);
        Assert.AreEqual(8m, busy.AverageGames);
        Assert.AreEqual(1, figures.FlaggedCells().Count());

        Assert.AreEqual(games.Count, figures.Totals.Games);
        Assert.AreEqual(280m, figures.Totals.ClockedHours);
        Assert.AreEqual(280m / games.Count, figures.Totals.LabourHoursPerGame);
    }

    // ---- Parties ----

    // The start times claimed by parties, from the given starts (sorted here, as Calculate does).
    private static List<DateTime> Claimed(IEnumerable<PartyFact> parties, int? offset, params DateTime[] starts)
    {
        List<DateTime> sorted = [.. starts.Order()];
        bool[] claimed = InsightsRules.ClaimPartyGames(sorted, parties, offset);
        return [.. sorted.Where((_, i) => claimed[i])];
    }

    [TestMethod]
    public void LaserTime_IsThePartysOwnTime_OrArrivalPlusTheLocationDefault()
    {
        PartyFact own = Party(Local(Monday, 13), Local(Monday, 15), Local(Monday, 14));
        PartyFact imported = Party(Local(Monday, 13), Local(Monday, 15));

        Assert.AreEqual(Local(Monday, 14), InsightsRules.LaserTime(own, 30));
        Assert.AreEqual(Local(Monday, 13, 30), InsightsRules.LaserTime(imported, 30));
        Assert.IsFalse(InsightsRules.IsMissingLaserTime(imported, 30));
    }

    [TestMethod]
    public void LaserTime_IsMissingForAKidsPartyWithNoTimeAndNoDefault()
    {
        PartyFact imported = Party(Local(Monday, 13), Local(Monday, 15));

        Assert.IsNull(InsightsRules.LaserTime(imported, null));
        Assert.IsTrue(InsightsRules.IsMissingLaserTime(imported, null));
    }

    [TestMethod]
    public void LaserTime_IsNeverSetForAHire_AndAPlayOnlyHireIsNotMissingOne()
    {
        PartyFact hire = Party(Local(Monday, 13), Local(Monday, 15), hire: PrivateHireKind.PlayOnly);

        Assert.IsNull(InsightsRules.LaserTime(hire, 30));
        Assert.IsFalse(InsightsRules.IsMissingLaserTime(hire, 30));
        Assert.IsFalse(InsightsRules.IsLaserHire(hire));
        Assert.AreEqual(0, Claimed([hire], 30, Local(Monday, 13, 30), Local(Monday, 13, 45)).Count);
    }

    [TestMethod]
    public void ClaimPartyGames_TakesTheFirstTwoGames_AndLeavesTheRestAsWalkIns()
    {
        // Two ten-minute games with a short gap, then the walk-ins carry on.
        PartyFact party = Party(Local(Monday, 13), Local(Monday, 15), Local(Monday, 14));

        List<DateTime> claimed = Claimed([party], null,
            Local(Monday, 13, 40), Local(Monday, 14, 2), Local(Monday, 14, 15), Local(Monday, 14, 30), Local(Monday, 14, 45));

        CollectionAssert.AreEqual(new[] { Local(Monday, 14, 2), Local(Monday, 14, 15) }, claimed);
    }

    [TestMethod]
    public void ClaimPartyGames_StartsLookingTenMinutesBeforeTheLaserTime()
    {
        PartyFact party = Party(Local(Monday, 13), Local(Monday, 15), Local(Monday, 14));

        CollectionAssert.AreEqual(new[] { Local(Monday, 13, 50), Local(Monday, 14, 5) },
            Claimed([party], null, Local(Monday, 13, 49), Local(Monday, 13, 50), Local(Monday, 14, 5)));
    }

    [TestMethod]
    public void ClaimPartyGames_AllowsAFirstGameUpToHalfAnHourLate()
    {
        PartyFact party = Party(Local(Monday, 13), Local(Monday, 15), Local(Monday, 14));

        CollectionAssert.AreEqual(new[] { Local(Monday, 14, 29) }, Claimed([party], null, Local(Monday, 14, 29)));
        Assert.AreEqual(0, Claimed([party], null, Local(Monday, 14, 30)).Count);
    }

    [TestMethod]
    public void ClaimPartyGames_NeedsTheSecondGameWithinTwentyMinutesOfTheFirst()
    {
        PartyFact party = Party(Local(Monday, 13), Local(Monday, 15), Local(Monday, 14));

        CollectionAssert.AreEqual(new[] { Local(Monday, 14), Local(Monday, 14, 19) },
            Claimed([party], null, Local(Monday, 14), Local(Monday, 14, 19)));
        CollectionAssert.AreEqual(new[] { Local(Monday, 14) },
            Claimed([party], null, Local(Monday, 14), Local(Monday, 14, 20)));
    }

    [TestMethod]
    public void ClaimPartyGames_GivesTwoPartiesAtTheSameTimeTheirOwnGames()
    {
        List<PartyFact> parties =
        [
            Party(Local(Monday, 13, 30), Local(Monday, 15, 30), Local(Monday, 14)),
            Party(Local(Monday, 13), Local(Monday, 15), Local(Monday, 14))
        ];

        List<DateTime> claimed = Claimed(parties, null,
            Local(Monday, 14), Local(Monday, 14, 12), Local(Monday, 14, 24), Local(Monday, 14, 36), Local(Monday, 14, 48));

        // Four games between them, each claimed once; the fifth is a walk-in.
        CollectionAssert.AreEqual(new[] { Local(Monday, 14), Local(Monday, 14, 12), Local(Monday, 14, 24), Local(Monday, 14, 36) }, claimed);
    }

    [TestMethod]
    public void ClaimPartyGames_NeverClaimsAGameTwice()
    {
        List<PartyFact> parties =
        [
            Party(Local(Monday, 13), Local(Monday, 15), Local(Monday, 14)),
            Party(Local(Monday, 13, 30), Local(Monday, 15, 30), Local(Monday, 14))
        ];

        InsightsFigures figures = Calculate(Monday, Monday, games: [GameStarting(Local(Monday, 14, 5))], parties: parties);

        Assert.AreEqual(1, figures.Totals.PartyGames);
        Assert.AreEqual(2, figures.Totals.Parties);
    }

    [TestMethod]
    public void ClaimPartyGames_GivesALaserTagHireEveryGameInTheBooking()
    {
        PartyFact hire = Party(Local(Monday, 18), Local(Monday, 20), hire: PrivateHireKind.LaserTagOnly);
        DateTime[] starts = [Local(Monday, 17, 55), Local(Monday, 18), Local(Monday, 18, 30), Local(Monday, 19), Local(Monday, 19, 45), Local(Monday, 20)];

        Assert.IsTrue(InsightsRules.IsLaserHire(hire));
        Assert.AreEqual(4, Claimed([hire], null, starts).Count);
        Assert.AreEqual(4, Claimed([hire with { HireKind = PrivateHireKind.LaserTagAndPlay }], null, starts).Count);
    }

    [TestMethod]
    public void ClaimPartyGames_UsesTheLocationDefaultForAPartyWithoutATime()
    {
        PartyFact imported = Party(Local(Monday, 13), Local(Monday, 15));
        DateTime[] starts = [Local(Monday, 13, 10), Local(Monday, 13, 31), Local(Monday, 13, 44), Local(Monday, 14)];

        CollectionAssert.AreEqual(new[] { Local(Monday, 13, 31), Local(Monday, 13, 44) }, Claimed([imported], 30, starts));
        Assert.AreEqual(0, Claimed([imported], null, starts).Count);
    }

    [TestMethod]
    public void Calculate_SplitsPartyGamesFromWalkInsByHourAndDay()
    {
        PartyFact party = Party(Local(Monday, 13), Local(Monday, 15), Local(Monday, 14));

        InsightsFigures figures = Calculate(Monday, Monday,
            games:
            [
                GameStarting(Local(Monday, 13, 40)),  // walk-in before the party
                GameStarting(Local(Monday, 13, 55)),  // party, first game
                GameStarting(Local(Monday, 14, 10)),  // party, second game
                GameStarting(Local(Monday, 14, 25))   // walk-in after the party
            ],
            parties: [party]);

        Assert.AreEqual(4, figures.Totals.Games);
        Assert.AreEqual(2, figures.Totals.PartyGames);
        Assert.AreEqual(2, figures.Totals.WalkInGames);

        InsightsSlot thirteen = figures.Slots.Single(x => x.Hour == 13);
        Assert.AreEqual(2, thirteen.Games);
        Assert.AreEqual(1, thirteen.PartyGames);
        Assert.AreEqual(1, thirteen.WalkInGames);
        Assert.AreEqual(2, figures.Days.Single().PartyGames);
    }

    [TestMethod]
    public void Calculate_PutsAPartyInEveryHourItOverlaps_WithItsChildren()
    {
        InsightsFigures figures = Calculate(Monday, Monday,
            parties:
            [
                Party(Local(Monday, 14, 30), Local(Monday, 16, 30), children: 12),
                Party(Local(Monday, 15), Local(Monday, 17), children: 8)
            ]);

        Assert.IsFalse(figures.Slots.Any(x => x.Hour == 13));
        Assert.AreEqual(1, figures.Slots.Single(x => x.Hour == 14).Parties);
        Assert.AreEqual(12, figures.Slots.Single(x => x.Hour == 14).ExpectedChildren);
        Assert.AreEqual(2, figures.Slots.Single(x => x.Hour == 15).Parties);
        Assert.AreEqual(20, figures.Slots.Single(x => x.Hour == 15).ExpectedChildren);
        Assert.AreEqual(20, figures.Slots.Single(x => x.Hour == 16).ExpectedChildren);
        Assert.IsFalse(figures.Slots.Any(x => x.Hour == 17));

        Assert.AreEqual(2, figures.Totals.Parties);
        Assert.AreEqual(20, figures.Totals.ExpectedChildren);
        Assert.AreEqual(2, figures.Days.Single().Parties);
        Assert.AreEqual(20, figures.Days.Single().ExpectedChildren);
    }

    [TestMethod]
    public void Calculate_CountsAPartyOnTheDayItStarts_EvenPastMidnight()
    {
        DateOnly tuesday = Monday.AddDays(1);

        InsightsFigures figures = Calculate(Monday, tuesday,
            parties: [Party(Local(Monday, 23), Local(tuesday, 1), children: 15)]);

        Assert.AreEqual(1, figures.Days.Single(x => x.Date == Monday).Parties);
        Assert.AreEqual(0, figures.Days.Single(x => x.Date == tuesday).Parties);
        Assert.AreEqual(1, figures.Slots.Single(x => x.Date == tuesday && x.Hour == 0).Parties);
        Assert.AreEqual(1, figures.Totals.Parties);
    }

    [TestMethod]
    public void Calculate_LeavesOutOfTheTotalsAPartyThatStartedBeforeTheRange()
    {
        DateOnly tuesday = Monday.AddDays(1);

        InsightsFigures figures = Calculate(tuesday, tuesday,
            parties: [Party(Local(Monday, 23), Local(tuesday, 1))]);

        Assert.AreEqual(0, figures.Totals.Parties);
        Assert.AreEqual(1, figures.Slots.Single(x => x.Hour == 0).Parties);
    }

    [TestMethod]
    public void Calculate_CountsKidsPartiesWithNoLaserTimeToGoOn()
    {
        List<PartyFact> parties =
        [
            Party(Local(Monday, 11), Local(Monday, 13)),
            Party(Local(Monday, 13), Local(Monday, 15), Local(Monday, 14)),
            Party(Local(Monday, 16), Local(Monday, 18), hire: PrivateHireKind.PlayOnly)
        ];

        Assert.AreEqual(1, Calculate(Monday, Monday, parties: parties).Totals.PartiesWithoutLaserTime);
        Assert.AreEqual(0, Calculate(Monday, Monday, parties: parties, laserTagOffsetMinutes: 45).Totals.PartiesWithoutLaserTime);
    }

    [TestMethod]
    public void Calculate_CountsAPartyOnceInTheRepeatedHourWhenTheClocksGoBack()
    {
        // 00:30 to 02:30 BST/GMT: 01:00 happens twice that night.
        DateTime start = new(2026, 10, 24, 23, 30, 0, DateTimeKind.Utc);
        DateTime end = new(2026, 10, 25, 2, 30, 0, DateTimeKind.Utc);

        InsightsFigures figures = Calculate(ClocksBack, ClocksBack, parties: [Party(start, end, children: 9)]);

        InsightsSlot one = figures.Slots.Single(x => x.Hour == 1);
        Assert.AreEqual(1, one.Parties);
        Assert.AreEqual(9, one.ExpectedChildren);
        Assert.AreEqual(1, figures.Totals.Parties);
    }

    // Four Mondays, 14:00, two walk-in games and one person on each week; one Monday also has a
    // party that played its two games at 14:20 and 14:33 with three party staff.
    private static InsightsFigures FourMondays(bool withParty)
    {
        DateOnly from = Monday;
        DateOnly to = Monday.AddDays(27);
        List<GameFact> games = [];
        List<WorkSpan> clocked = [];

        for (int week = 0; week < 4; week++)
        {
            DateOnly day = Monday.AddDays(week * 7);
            games.Add(GameStarting(Local(day, 14, 1)));
            games.Add(GameStarting(Local(day, 14, 45)));
            clocked.Add(new WorkSpan(Local(day, 14), Local(day, 15)));
        }

        DateOnly partyDay = Monday.AddDays(14);
        List<PartyFact> parties = [];

        if (withParty)
        {
            games.Add(GameStarting(Local(partyDay, 14, 20)));
            games.Add(GameStarting(Local(partyDay, 14, 33)));
            clocked.AddRange(Enumerable.Range(0, 3).Select(_ => new WorkSpan(Local(partyDay, 14), Local(partyDay, 15))));
            parties.Add(Party(Local(partyDay, 13, 30), Local(partyDay, 15, 30), Local(partyDay, 14, 20), children: 18));
        }

        return Calculate(from, to, games, clocked, hours: [Hours(DayOfWeek.Monday, 10, 18)], parties: parties);
    }

    [TestMethod]
    public void Calculate_LeavesPartyHoursOutOfTheRateBasis()
    {
        InsightsCell cell = FourMondays(withParty: true).Cells.Single(x => x.Hour == 14);

        Assert.AreEqual(4, cell.Samples);
        Assert.AreEqual(10, cell.Games);
        // Its own two: the 14:01 walk-in is before it, and the 14:45 one is a third game.
        Assert.AreEqual(2, cell.PartyGames);
        Assert.AreEqual(1, cell.PartySamples);
        Assert.AreEqual(3, cell.RateSamples);
        Assert.AreEqual(6, cell.RateGames);
        Assert.AreEqual(3m, cell.RateClockedHours);

        // Two games per person, as on every party-free Monday - not 10 / 7.
        Assert.AreEqual(2m, cell.GamesPerStaffHour);
        Assert.AreEqual(18m / 4, cell.AverageExpectedChildren);
    }

    [TestMethod]
    public void Calculate_WithNoPartiesUsesEveryHourForTheRate()
    {
        InsightsCell cell = FourMondays(withParty: false).Cells.Single(x => x.Hour == 14);

        Assert.AreEqual(cell.Samples, cell.RateSamples);
        Assert.AreEqual(cell.Games, cell.RateGames);
        Assert.AreEqual(cell.ClockedHours, cell.RateClockedHours);
        Assert.AreEqual(cell.Games / cell.ClockedHours, cell.GamesPerStaffHour);
        Assert.AreEqual(0, cell.PartySamples);
    }

    [TestMethod]
    public void Calculate_LeavesPartyHoursOutOfTheMedian()
    {
        List<GameFact> games = [];
        List<WorkSpan> clocked = [];
        List<PartyFact> parties = [];

        // Three weeks of Mondays, 10:00-14:00: three steady hours at 6 games per person, and a party
        // every week at 13:00, when its two games and a rush of walk-ins make 30 games with one
        // person on - an hour that would otherwise be flagged busy.
        for (int week = 0; week < 3; week++)
        {
            DateOnly day = Monday.AddDays(week * 7);

            foreach (int hour in new[] { 10, 11, 12 })
            {
                games.AddRange(Enumerable.Range(0, 6).Select(i => GameStarting(Local(day, hour, i * 5))));
                clocked.Add(new WorkSpan(Local(day, hour), Local(day, hour + 1)));
            }

            games.AddRange(Enumerable.Range(0, 30).Select(i => GameStarting(Local(day, 13, i))));
            clocked.Add(new WorkSpan(Local(day, 13), Local(day, 14)));
            parties.Add(Party(Local(day, 12, 45), Local(day, 14), Local(day, 13, 10)));
        }

        InsightsFigures figures = Calculate(Monday, Monday.AddDays(20), games, clocked, hours: [Hours(DayOfWeek.Monday, 10, 14)], parties: parties);

        Assert.AreEqual(6m, figures.MedianGamesPerStaffHour);
        Assert.IsTrue(figures.FlagsAvailable, figures.FlagsUnavailableReason);
        Assert.AreEqual(StaffingFlag.PartyHours, figures.Cells.Single(x => x.Hour == 13).Flag);
        Assert.AreEqual(StaffingFlag.PartyHours, figures.Cells.Single(x => x.Hour == 12).Flag);
        Assert.AreEqual(StaffingFlag.None, figures.Cells.Single(x => x.Hour == 10).Flag);
        Assert.IsFalse(figures.FlaggedCells().Any());
    }

    [TestMethod]
    public void Flag_CallsAnHourMostlyParties_WhenHalfItsOccurrencesHadOne()
    {
        Assert.AreEqual(StaffingFlag.PartyHours, InsightsRules.Flag(Cell(4, 40, 4m, partySamples: 2), 2m));
        Assert.AreEqual(StaffingFlag.Understaffed, InsightsRules.Flag(Cell(4, 40, 4m, partySamples: 1), 2m));
        Assert.AreEqual(StaffingFlag.InsufficientData, InsightsRules.Flag(Cell(2, 40, 4m, partySamples: 2), 2m));
    }

    [TestMethod]
    public void PartyFact_CarriesNoText()
    {
        Assert.IsFalse(typeof(PartyFact).GetProperties().Any(x => x.PropertyType == typeof(string)),
            "Insights must never be able to carry a child's name, contact or allergy note.");
    }
}
