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
        IReadOnlyCollection<MusicDaypart>? dayparts = null) =>
        InsightsRules.Calculate(from, to, games ?? [], clocked ?? [], rota ?? [], hours ?? [], dayparts ?? []);

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

    private static InsightsCell Cell(int samples, int games, decimal clocked) =>
        new(DayOfWeek.Saturday, 14, samples, games, games * 6, clocked, clocked, StaffingFlag.None);

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
}
