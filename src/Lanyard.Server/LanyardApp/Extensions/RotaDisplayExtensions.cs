using System.Globalization;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Lanyard.App.Extensions;

// Venue-local formatting for shifts. Everything goes through RotaTime rather than ToLocalTime():
// the server runs in UTC, so ToLocalTime() would show summer shifts an hour early.
public static class RotaDisplayExtensions
{
    public static CultureInfo Uk => RotaFormat.Uk;

    public static DateOnly LocalDate(this Shift shift) => RotaTime.LocalDate(shift.StartUtc);

    public static bool EndsNextDay(this Shift shift) => RotaTime.LocalDate(shift.EndUtc) > RotaTime.LocalDate(shift.StartUtc);

    public static string TimeRange(this Shift shift) => RotaFormat.TimeRange(shift.StartUtc, shift.EndUtc);

    // "09–17", "09:30–17" - for the month grid's narrow cells.
    public static string CompactTimeRange(this Shift shift) =>
        $"{Compact(RotaTime.LocalTime(shift.StartUtc))}–{Compact(RotaTime.LocalTime(shift.EndUtc))}";

    public static string FormatHours(decimal hours) => RotaFormat.Hours(hours);

    public static string StateLabel(this ShiftState state) => state switch
    {
        ShiftState.Draft => "Draft - not published yet",
        ShiftState.Published => "Published",
        ShiftState.Changed => "Changed since it was published",
        ShiftState.RemovedPendingNotice => "Removed - staff are told on the next publish",
        _ => "Removed"
    };

    public static BadgeColor StateColor(this ShiftState state) => state switch
    {
        ShiftState.Draft => BadgeColor.Subtle,
        ShiftState.Published => BadgeColor.Success,
        ShiftState.Changed => BadgeColor.Warning,
        _ => BadgeColor.Danger
    };

    public static string Label(this TimeOffStatus status) => status switch
    {
        TimeOffStatus.Pending => "Waiting for approval",
        TimeOffStatus.Approved => "Approved",
        TimeOffStatus.Rejected => "Rejected",
        _ => "Cancelled"
    };

    public static BadgeColor Color(this TimeOffStatus status) => status switch
    {
        TimeOffStatus.Pending => BadgeColor.Warning,
        TimeOffStatus.Approved => BadgeColor.Success,
        TimeOffStatus.Rejected => BadgeColor.Danger,
        _ => BadgeColor.Subtle
    };

    // "18 days left of 28 (2026/27)", "Unlimited", "No allowance set".
    public static string Summary(this TimeOffBalance balance, decimal hoursPerDay)
    {
        if (balance.Allowance.IsUnlimited)
        {
            return "Unlimited";
        }

        if (!balance.Allowance.IsConfigured)
        {
            return "No allowance set";
        }

        return $"{TimeOffFormat.Days(balance.RemainingHours ?? 0, hoursPerDay)} left of {TimeOffFormat.Days(balance.Allowance.Hours, hoursPerDay)}";
    }

    public static string DayLabel(this DateOnly date) => date.ToString("ddd d MMM", Uk);

    public static string RangeLabel(DateOnly from, DateOnly to)
    {
        if (from.Year != to.Year)
        {
            return $"{from.ToString("d MMM yyyy", Uk)} – {to.ToString("d MMM yyyy", Uk)}";
        }

        return from.Month == to.Month
            ? $"{from.Day} – {to.ToString("d MMM yyyy", Uk)}"
            : $"{from.ToString("d MMM", Uk)} – {to.ToString("d MMM yyyy", Uk)}";
    }

    private static string Compact(TimeOnly time) =>
        time.Minute == 0 ? time.ToString("HH", Uk) : time.ToString("HH:mm", Uk);
}
