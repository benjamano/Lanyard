using System.Text;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

// The export rules on a hand-built timesheet - no database, so each rule is tested on its own.
[TestClass]
public class TimesheetExportBuilderTests
{
    // Monday 5 October 2026 is British Summer Time.
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private static DateTime Local(DateOnly day, int hour, int minute = 0) => RotaTime.ToUtc(day, new TimeOnly(hour, minute));

    private static TimeEntry Entry(DateTime inUtc, DateTime? outUtc, bool approved = true, bool needsReview = false, Shift? shift = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = "amy",
        LocationId = 1,
        ShiftId = shift?.Id,
        Shift = shift,
        ClockInUtc = inUtc,
        ClockOutUtc = outUtc,
        ApprovedDateUtc = approved ? inUtc.AddDays(1) : null,
        ApprovedByUserId = approved ? "manager" : null,
        NeedsReview = needsReview,
        IsActive = true
    };

    private static Shift ShiftOn(DateOnly day, int startHour, int endHour, int breakMinutes) => new()
    {
        Id = Guid.NewGuid(),
        LocationId = 1,
        UserId = "amy",
        StartUtc = Local(day, startHour),
        EndUtc = Local(day, endHour),
        BreakMinutes = breakMinutes,
        CreateByUserId = "manager"
    };

    private static TimesheetLine Line(TimeEntry entry) => new(RotaTime.LocalDate(entry.ClockInUtc), entry.Shift, entry);

    private static TimesheetView View(params TimesheetPerson[] people) => new(1, "Ipswich", Monday, Monday.AddDays(6), [.. people]);

    private static TimesheetPerson Person(string name, params TimesheetLine[] lines) => new(name.ToLowerInvariant(), name, [.. lines], 0m, 0m, 0, 0);

    private static string Csv(TimesheetExport export)
    {
        byte[] bytes = TimesheetExportBuilder.ToCsv(export);
        return Encoding.UTF8.GetString(bytes[3..]);
    }

    [TestMethod]
    public void Build_ApprovedEntry_GivesADayRowAndATotal()
    {
        TimesheetExport export = TimesheetExportBuilder.Build(View(Person("Amy Clark", Line(Entry(Local(Monday, 9), Local(Monday, 17, 30))))));

        Assert.HasCount(2, export.Rows);

        TimesheetExportRow day = export.Rows[0];
        Assert.AreEqual(TimesheetExportRowType.Day, day.Type);
        Assert.AreEqual(Monday, day.Date);
        Assert.AreEqual(new TimeOnly(9, 0), day.FirstIn);
        Assert.AreEqual(new TimeOnly(17, 30), day.LastOut);
        Assert.AreEqual(510, day.Minutes);

        TimesheetExportRow total = export.Rows[1];
        Assert.AreEqual(TimesheetExportRowType.Total, total.Type);
        Assert.AreEqual(510, total.Minutes);
        Assert.AreEqual(1, export.Summary.People);
        Assert.AreEqual(510, export.Summary.ApprovedMinutes);
        Assert.AreEqual(0, export.Summary.ExcludedCount);
    }

    [TestMethod]
    public void Build_UnapprovedFlaggedAndOpenEntries_AreListedAsExcludedWithNoHours()
    {
        TimesheetExport export = TimesheetExportBuilder.Build(View(Person("Amy Clark",
            Line(Entry(Local(Monday, 9), Local(Monday, 17), approved: false)),
            Line(Entry(Local(Monday.AddDays(1), 9), Local(Monday.AddDays(1), 17), needsReview: true)),
            Line(Entry(Local(Monday.AddDays(2), 9), null, approved: false)))));

        List<TimesheetExportRow> excluded = [.. export.Rows.Where(r => r.Type == TimesheetExportRowType.Excluded)];

        Assert.HasCount(3, excluded);
        Assert.AreEqual(TimesheetExclusionReason.AwaitingApproval, excluded[0].ExclusionReason);
        Assert.AreEqual(TimesheetExclusionReason.NeedsReview, excluded[1].ExclusionReason);
        Assert.AreEqual(TimesheetExclusionReason.StillClockedIn, excluded[2].ExclusionReason);
        Assert.IsTrue(excluded.All(r => r.Minutes is null));
        Assert.IsNull(excluded[2].LastOut);

        Assert.AreEqual(0, export.Rows.Count(r => r.Type == TimesheetExportRowType.Day));
        Assert.AreEqual(0, export.Rows.Single(r => r.Type == TimesheetExportRowType.Total).Minutes);
        Assert.AreEqual(1, export.Summary.AwaitingApproval);
        Assert.AreEqual(1, export.Summary.NeedsReview);
        Assert.AreEqual(1, export.Summary.StillClockedIn);
        Assert.AreEqual(0, export.Summary.ApprovedMinutes);
    }

    [TestMethod]
    public void Build_ApprovedButStillFlagged_IsExcludedAsNeedsReview()
    {
        TimeEntry entry = Entry(Local(Monday, 9), Local(Monday, 17), approved: true, needsReview: true);

        Assert.AreEqual(TimesheetExclusionReason.NeedsReview, TimesheetExportBuilder.ExclusionFor(entry));
    }

    [TestMethod]
    public void Build_TwoEntriesOnOneDay_MergeIntoOneRow()
    {
        TimesheetExport export = TimesheetExportBuilder.Build(View(Person("Amy Clark",
            Line(Entry(Local(Monday, 9), Local(Monday, 12))),
            Line(Entry(Local(Monday, 13), Local(Monday, 17, 15))))));

        TimesheetExportRow day = export.Rows.Single(r => r.Type == TimesheetExportRowType.Day);

        Assert.AreEqual(2, day.Entries);
        Assert.AreEqual(new TimeOnly(9, 0), day.FirstIn);
        Assert.AreEqual(new TimeOnly(17, 15), day.LastOut);
        Assert.AreEqual(180 + 255, day.Minutes);
    }

    [TestMethod]
    public void Build_EachEntryIsRoundedToTheNearestMinute_AndTheTotalIsTheSumOfTheDays()
    {
        DateTime tuesday = Local(Monday.AddDays(1), 9);

        TimesheetExport export = TimesheetExportBuilder.Build(View(Person("Amy Clark",
            Line(Entry(Local(Monday, 9), Local(Monday, 10).AddSeconds(29.5))),
            Line(Entry(tuesday, tuesday.AddHours(1).AddSeconds(30))))));

        List<TimesheetExportRow> days = [.. export.Rows.Where(r => r.Type == TimesheetExportRowType.Day)];

        Assert.AreEqual(60, days[0].Minutes);
        Assert.AreEqual(61, days[1].Minutes);
        Assert.AreEqual(121, export.Rows.Single(r => r.Type == TimesheetExportRowType.Total).Minutes);
    }

    [TestMethod]
    public void ToCsv_TotalHours_ComeFromTotalMinutesNotFromRoundedDayHours()
    {
        // Three days of 7 minutes: each day is 0.12 h, but 21 minutes is 0.35 h, not 0.36.
        TimesheetExport export = TimesheetExportBuilder.Build(View(Person("Amy Clark",
            Line(Entry(Local(Monday, 9), Local(Monday, 9, 7))),
            Line(Entry(Local(Monday.AddDays(1), 9), Local(Monday.AddDays(1), 9, 7))),
            Line(Entry(Local(Monday.AddDays(2), 9), Local(Monday.AddDays(2), 9, 7))))));

        string[] lines = Csv(export).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.AreEqual("Day,Amy Clark,2026-10-05,09:00,09:07,1,7,0.12,,Approved", lines[1]);
        Assert.AreEqual("Total,Amy Clark,,,,3,21,0.35,,", lines[4]);
    }

    [TestMethod]
    public void Build_RotaBreak_IsSummedPerDistinctShift_AndBlankWithoutAShift()
    {
        Shift shift = ShiftOn(Monday, 9, 17, 30);

        TimesheetExport export = TimesheetExportBuilder.Build(View(Person("Amy Clark",
            Line(Entry(Local(Monday, 9), Local(Monday, 12), shift: shift)),
            Line(Entry(Local(Monday, 13), Local(Monday, 17), shift: shift)),
            Line(Entry(Local(Monday.AddDays(1), 9), Local(Monday.AddDays(1), 17))))));

        List<TimesheetExportRow> days = [.. export.Rows.Where(r => r.Type == TimesheetExportRowType.Day)];

        Assert.AreEqual(30, days[0].RotaBreakMinutes);
        Assert.IsNull(days[1].RotaBreakMinutes);
        Assert.AreEqual(30, export.Rows.Single(r => r.Type == TimesheetExportRowType.Total).RotaBreakMinutes);
    }

    [TestMethod]
    public void Build_EntryAcrossMidnight_StaysWholeOnTheDayItStarted()
    {
        TimesheetExport export = TimesheetExportBuilder.Build(View(Person("Amy Clark",
            Line(Entry(Local(Monday, 20), Local(Monday.AddDays(1), 2))))));

        TimesheetExportRow day = export.Rows.Single(r => r.Type == TimesheetExportRowType.Day);

        Assert.AreEqual(Monday, day.Date);
        Assert.AreEqual(new TimeOnly(20, 0), day.FirstIn);
        Assert.AreEqual(new TimeOnly(2, 0), day.LastOut);
        Assert.AreEqual(360, day.Minutes);
    }

    [TestMethod]
    public void Build_NightTheClocksGoBack_PaysTheRealNineHours()
    {
        // Clocks go back at 02:00 BST on Sunday 25 October 2026, so 22:00-06:00 is nine hours.
        DateOnly saturday = new(2026, 10, 24);

        TimesheetExport export = TimesheetExportBuilder.Build(View(Person("Amy Clark",
            Line(Entry(Local(saturday, 22), Local(saturday.AddDays(1), 6))))));

        TimesheetExportRow day = export.Rows.Single(r => r.Type == TimesheetExportRowType.Day);

        Assert.AreEqual(saturday, day.Date);
        Assert.AreEqual(new TimeOnly(22, 0), day.FirstIn);
        Assert.AreEqual(new TimeOnly(6, 0), day.LastOut);
        Assert.AreEqual(540, day.Minutes);
    }

    [TestMethod]
    public void Build_NightTheClocksGoForward_PaysTheRealSevenHours()
    {
        // Clocks go forward at 01:00 GMT on Sunday 29 March 2026, so 22:00-06:00 is seven hours.
        DateOnly saturday = new(2026, 3, 28);

        TimesheetExport export = TimesheetExportBuilder.Build(View(Person("Amy Clark",
            Line(Entry(Local(saturday, 22), Local(saturday.AddDays(1), 6))))));

        TimesheetExportRow day = export.Rows.Single(r => r.Type == TimesheetExportRowType.Day);

        Assert.AreEqual(new TimeOnly(22, 0), day.FirstIn);
        Assert.AreEqual(new TimeOnly(6, 0), day.LastOut);
        Assert.AreEqual(420, day.Minutes);
    }

    [TestMethod]
    public void Build_ShiftNobodyClockedFor_GivesNoRows()
    {
        TimesheetLine missed = new(Monday, ShiftOn(Monday, 9, 17, 30), null);

        TimesheetExport export = TimesheetExportBuilder.Build(View(Person("Amy Clark", missed)));

        Assert.IsEmpty(export.Rows);
        Assert.AreEqual(0, export.Summary.People);
    }

    [TestMethod]
    public void ToCsv_StartsWithByteOrderMarkAndHeader_AndNeutralisesFormulaNames()
    {
        TimesheetExport export = TimesheetExportBuilder.Build(View(Person("=HYPERLINK(\"http://evil\",\"Amy\")",
            Line(Entry(Local(Monday, 9), Local(Monday, 17))))));

        byte[] bytes = TimesheetExportBuilder.ToCsv(export);
        string[] lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.AreEqual("Row type,Employee,Date,First in,Last out,Entries,Minutes,Hours,Rota break (min),Status", lines[0]);
        StringAssert.StartsWith(lines[1], "Day,\"'=HYPERLINK(\"\"http://evil\"\",\"\"Amy\"\")\",2026-10-05,");
    }

    [TestMethod]
    public void ToCsv_ExcludedRow_ShowsTheReasonAndNoHours()
    {
        TimesheetExport export = TimesheetExportBuilder.Build(View(Person("Amy Clark",
            Line(Entry(Local(Monday, 9), null, approved: false)))));

        string[] lines = Csv(export).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.AreEqual("Excluded,Amy Clark,2026-10-05,09:00,,1,,,,Still clocked in", lines[1]);
        Assert.AreEqual("Total,Amy Clark,,,,0,0,0.00,,", lines[2]);
    }

    [TestMethod]
    [DataRow("Ipswich", "timesheet-ipswich-2026-10-05-to-2026-10-11.csv")]
    [DataRow("Play2Day: Bury St. Edmunds!", "timesheet-play2day-bury-st-edmunds-2026-10-05-to-2026-10-11.csv")]
    [DataRow("../../etc", "timesheet-etc-2026-10-05-to-2026-10-11.csv")]
    [DataRow("***", "timesheet-location-2026-10-05-to-2026-10-11.csv")]
    public void FileName_IsSlugged(string locationName, string expected)
    {
        Assert.AreEqual(expected, TimesheetExportBuilder.FileName(locationName, Monday, Monday.AddDays(6)));
    }
}
