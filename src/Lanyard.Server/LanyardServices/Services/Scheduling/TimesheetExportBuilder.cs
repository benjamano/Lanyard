using System.Text;
using System.Text.RegularExpressions;
using Lanyard.Application.Services.Export;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// The payroll export rules, kept free of the database so the dialog's preview and the file itself
// can never disagree, and so the rules can be tested on a hand-built timesheet.
//
// - Only closed, approved entries that aren't flagged for review count towards hours. Anything else
//   that was clocked is listed as an Excluded row with its reason - never silently left out.
// - An entry belongs to the venue-local day it clocked in on, the same as the Timesheets page, so
//   a night shift isn't split across two days.
// - Each entry is rounded to the nearest whole minute of real elapsed time (taken from UTC, so a
//   shift across a clock change is paid for the hours actually worked). Hours are only derived
//   from minutes when the file is written.
// - Clocked time never has a break taken off. The company's break rule (BreakDeductionPolicy) is
//   applied per person per day to give separate paid columns, and the rota break of the matched
//   shift is always shown so payroll can see the working.
public static partial class TimesheetExportBuilder
{
    public const string ContentType = "text/csv";

    public static readonly IReadOnlyList<string> Header =
        ["Row type", "Employee", "Date", "First in", "Last out", "Entries", "Minutes", "Hours", "Rota break (min)", "Break deducted (min)", "Paid minutes", "Paid hours", "Status"];

    public static TimesheetExport Build(TimesheetView view) => Build(view, BreakDeductionPolicy.None);

    public static TimesheetExport Build(TimesheetView view, BreakDeductionPolicy breaks)
    {
        List<TimesheetExportRow> rows = [];
        int people = 0;
        int dayRows = 0;
        int approvedMinutes = 0;
        int awaitingApproval = 0;
        int needsReview = 0;
        int stillClockedIn = 0;
        int deductedMinutes = 0;
        int paidMinutes = 0;

        foreach (TimesheetPerson person in view.People)
        {
            List<TimesheetLine> clocked = [.. person.Lines.Where(l => l.Entry is not null).OrderBy(l => l.Entry!.ClockInUtc)];

            // A published shift nobody clocked for has nothing to pay.
            if (clocked.Count == 0)
            {
                continue;
            }

            people++;

            List<TimesheetLine> included = [.. clocked.Where(l => ExclusionFor(l.Entry!) is null)];
            int personMinutes = 0;
            int personDeducted = 0;

            foreach (IGrouping<DateOnly, TimesheetLine> day in included.GroupBy(l => l.Date).OrderBy(g => g.Key))
            {
                List<TimeEntry> entries = [.. day.Select(l => l.Entry!)];
                int minutes = entries.Sum(RoundedMinutes);
                int? rotaBreak = RotaBreakMinutes(day);
                int deducted = BreakToDeduct(breaks, minutes, rotaBreak);

                rows.Add(new TimesheetExportRow(
                    TimesheetExportRowType.Day,
                    person.DisplayName,
                    day.Key,
                    RotaTime.LocalTime(entries.Min(e => e.ClockInUtc)),
                    RotaTime.LocalTime(entries.Max(e => e.ClockOutUtc!.Value)),
                    entries.Count,
                    minutes,
                    rotaBreak,
                    null,
                    deducted,
                    minutes - deducted));

                personMinutes += minutes;
                personDeducted += deducted;
                dayRows++;
            }

            foreach (TimesheetLine line in clocked.Where(l => ExclusionFor(l.Entry!) is not null))
            {
                TimeEntry entry = line.Entry!;
                TimesheetExclusionReason reason = ExclusionFor(entry)!.Value;

                rows.Add(new TimesheetExportRow(
                    TimesheetExportRowType.Excluded,
                    person.DisplayName,
                    line.Date,
                    RotaTime.LocalTime(entry.ClockInUtc),
                    entry.ClockOutUtc is DateTime outUtc ? RotaTime.LocalTime(outUtc) : null,
                    1,
                    null,
                    null,
                    reason));

                switch (reason)
                {
                    case TimesheetExclusionReason.StillClockedIn: stillClockedIn++; break;
                    case TimesheetExclusionReason.NeedsReview: needsReview++; break;
                    default: awaitingApproval++; break;
                }
            }

            rows.Add(new TimesheetExportRow(
                TimesheetExportRowType.Total,
                person.DisplayName,
                null,
                null,
                null,
                included.Count,
                personMinutes,
                RotaBreakMinutes(included),
                null,
                personDeducted,
                personMinutes - personDeducted));

            approvedMinutes += personMinutes;
            deductedMinutes += personDeducted;
            paidMinutes += personMinutes - personDeducted;
        }

        TimesheetExportSummary summary = new(view.LocationName, view.From, view.To, people, dayRows, approvedMinutes, awaitingApproval, needsReview, stillClockedIn, deductedMinutes, paidMinutes);

        return new TimesheetExport(summary, rows);
    }

    // The unpaid break for one person's day under the company's rule, never more than the day.
    // The fixed rule applies only when the day is strictly longer than the threshold, so a day of
    // exactly 6 hours keeps its full pay under "20 minutes over 6 hours".
    public static int BreakToDeduct(BreakDeductionPolicy breaks, int clockedMinutes, int? rotaBreakMinutes)
    {
        int deduct = breaks.Rule switch
        {
            BreakDeductionRule.RotaBreak => rotaBreakMinutes ?? 0,
            BreakDeductionRule.FixedOverThreshold when clockedMinutes > breaks.AfterMinutes => breaks.Minutes,
            _ => 0
        };

        return Math.Clamp(deduct, 0, Math.Max(0, clockedMinutes));
    }

    public static TimesheetExclusionReason? ExclusionFor(TimeEntry entry)
    {
        if (entry.IsOpen)
        {
            return TimesheetExclusionReason.StillClockedIn;
        }

        // Checked before approval: an entry flagged after it was approved still needs a look.
        if (entry.NeedsReview)
        {
            return TimesheetExclusionReason.NeedsReview;
        }

        return entry.IsApproved ? null : TimesheetExclusionReason.AwaitingApproval;
    }

    public static int RoundedMinutes(TimeEntry entry)
    {
        if (entry.ClockOutUtc is not DateTime outUtc)
        {
            return 0;
        }

        return (int)Math.Max(0, Math.Round((outUtc - entry.ClockInUtc).TotalMinutes, MidpointRounding.AwayFromZero));
    }

    public static decimal HoursFromMinutes(int minutes) => Math.Round(minutes / 60m, 2, MidpointRounding.AwayFromZero);

    // The rota break of each distinct shift these entries were clocked against. Null when none of
    // them matched a shift, so "no shift" reads differently from "a shift with no break".
    private static int? RotaBreakMinutes(IEnumerable<TimesheetLine> lines)
    {
        List<Shift> shifts = [.. lines.Select(l => l.Shift).OfType<Shift>().DistinctBy(s => s.Id)];

        return shifts.Count == 0 ? null : shifts.Sum(s => s.BreakMinutes);
    }

    public static byte[] ToCsv(TimesheetExport export)
    {
        StringBuilder builder = new();
        CsvWriter.AppendRow(builder, Header.Select(CsvWriter.Text));

        foreach (TimesheetExportRow row in export.Rows)
        {
            CsvWriter.AppendRow(builder,
            [
                CsvWriter.Text(row.Type.ToString()),
                CsvWriter.Text(row.Employee),
                row.Date?.ToString("yyyy-MM-dd", RotaFormat.Uk) ?? string.Empty,
                row.FirstIn?.ToString("HH:mm", RotaFormat.Uk) ?? string.Empty,
                row.LastOut?.ToString("HH:mm", RotaFormat.Uk) ?? string.Empty,
                CsvWriter.Number(row.Entries),
                CsvWriter.Number(row.Minutes),
                CsvWriter.Number(row.Minutes is int minutes ? HoursFromMinutes(minutes) : null, "0.00"),
                CsvWriter.Number(row.RotaBreakMinutes),
                CsvWriter.Number(row.BreakDeductedMinutes),
                CsvWriter.Number(row.PaidMinutes),
                CsvWriter.Number(row.PaidMinutes is int paid ? HoursFromMinutes(paid) : null, "0.00"),
                CsvWriter.Text(StatusText(row))
            ]);
        }

        return CsvWriter.ToBytes(builder);
    }

    public static string StatusText(TimesheetExportRow row) => row switch
    {
        { Type: TimesheetExportRowType.Day } => "Approved",
        { ExclusionReason: TimesheetExclusionReason.StillClockedIn } => "Still clocked in",
        { ExclusionReason: TimesheetExclusionReason.NeedsReview } => "Needs review",
        { ExclusionReason: TimesheetExclusionReason.AwaitingApproval } => "Awaiting approval",
        _ => string.Empty
    };

    // "timesheet-ipswich-2026-10-05-to-2026-10-11.csv"
    public static string FileName(string locationName, DateOnly from, DateOnly to)
    {
        string slug = NonSlugCharacters().Replace(locationName.ToLowerInvariant(), "-").Trim('-');

        return $"timesheet-{(slug.Length == 0 ? "location" : slug)}-{from.ToString("yyyy-MM-dd", RotaFormat.Uk)}-to-{to.ToString("yyyy-MM-dd", RotaFormat.Uk)}.csv";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();
}
