using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Infrastructure.DTO.Scheduling;

// One holiday year: allowances reset on its first day. Inclusive at both ends.
public record HolidayYear(DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly date) => date >= Start && date <= End;

    // "2026/27" for a year that doesn't start on 1 January, "2026" for one that does.
    public string Label => Start is { Month: 1, Day: 1 } ? Start.Year.ToString() : $"{Start.Year}/{(Start.Year + 1) % 100:00}";

    public static HolidayYear For(int startMonth, int startDay, DateOnly date)
    {
        DateOnly thisYearsStart = new(date.Year, startMonth, startDay);
        DateOnly start = date < thisYearsStart ? thisYearsStart.AddYears(-1) : thisYearsStart;

        return new HolidayYear(start, start.AddYears(1).AddDays(-1));
    }

    public static HolidayYear For(CompanySchedulingSettings settings, DateOnly date) =>
        For(settings.FinancialYearStartMonth, settings.FinancialYearStartDay, date);
}

// A person's allowance for one type after the company -> position -> user coalesce. Source None
// means nothing is configured at any tier, which counts as no allowance (0 h).
public record ResolvedAllowance(Guid TimeOffTypeId, bool IsUnlimited, decimal Hours, ContractTier Source)
{
    public bool IsConfigured => Source != ContractTier.None;

    public static ResolvedAllowance None(Guid typeId) => new(typeId, false, 0m, ContractTier.None);
}

// Where a person stands on one allowance-deducting type in one holiday year.
public record TimeOffBalance(TimeOffType Type, ResolvedAllowance Allowance, decimal ApprovedHours, decimal PendingHours)
{
    // Null when unlimited. Can go negative when a manager approved more than the allowance.
    public decimal? RemainingHours => Allowance.IsUnlimited ? null : Allowance.Hours - ApprovedHours;

    // What would be left if everything pending were approved too.
    public decimal? RemainingAfterPendingHours => RemainingHours is decimal remaining ? remaining - PendingHours : null;
}

public record TimeOffBalances(HolidayYear Year, decimal HoursPerDay, List<TimeOffBalance> Balances);

// What a request would cost and anything the requester or manager should know before it goes in.
// Warnings never block: exceeding an allowance is the manager's call.
public record TimeOffPreview(HolidayYear Year, decimal HoursPerDay, decimal DefaultHours, TimeOffBalance? Balance, List<string> Warnings);

public record TimeOffRequestDraft(Guid TimeOffTypeId, DateOnly StartDate, DateOnly EndDate, decimal Hours, string? Notes);

public record TimeOffSubmitResult(TimeOffRequest Request, List<string> Warnings);

// One line of the manager's list: the request plus what a manager needs beside it to decide.
public record TimeOffRequestView(
    TimeOffRequest Request,
    string DisplayName,
    decimal HoursPerDay,
    TimeOffBalance? Balance,
    List<string> Warnings,
    List<string> OthersOff);

// Days-and-hours wording shared by every time-off screen, so "30 days (240 h)" reads the same
// everywhere.
public static class TimeOffFormat
{
    public static string Days(decimal hours, decimal hoursPerDay)
    {
        if (hoursPerDay <= 0)
        {
            return RotaFormat.Hours(hours);
        }

        decimal days = Math.Round(hours / hoursPerDay, 2);
        string dayWord = days == 1 ? "day" : "days";

        return $"{days.ToString("0.##", RotaFormat.Uk)} {dayWord}";
    }

    public static string DaysAndHours(decimal hours, decimal hoursPerDay) =>
        $"{Days(hours, hoursPerDay)} ({RotaFormat.Hours(hours)})";

    // "Mon 5 Oct" or "Mon 5 – Fri 9 Oct" (month shown on both ends when they differ).
    public static string DateRange(DateOnly start, DateOnly end)
    {
        if (start == end)
        {
            return start.ToString("ddd d MMM", RotaFormat.Uk);
        }

        string startFormat = start.Month == end.Month && start.Year == end.Year ? "ddd d" : "ddd d MMM";

        return $"{start.ToString(startFormat, RotaFormat.Uk)} – {end.ToString("ddd d MMM", RotaFormat.Uk)}";
    }
}
