using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// Works out a company's pay periods from its rota settings. Dates only, never times: callers pass
// a venue-local day (RotaTime.Today), so a period always starts and ends at venue-local midnight.
public static class PayPeriodCalculator
{
    public static PayPeriod Containing(CompanySchedulingSettings settings, DateOnly date)
    {
        if (settings.PayPeriod == PayPeriodFrequency.Monthly)
        {
            int startDay = Math.Clamp(settings.PayPeriodStartDay, 1, 28);
            DateOnly thisMonth = new(date.Year, date.Month, startDay);
            DateOnly from = date.Day >= startDay ? thisMonth : thisMonth.AddMonths(-1);

            return new PayPeriod(from, from.AddMonths(1).AddDays(-1));
        }

        int length = settings.PayPeriod switch
        {
            PayPeriodFrequency.Fortnightly => 14,
            PayPeriodFrequency.FourWeekly => 28,
            _ => 7
        };

        // Without an anchor there is no way to count fortnights; Monday weeks are the safe guess
        // and match the Timesheets page.
        DateOnly anchor = settings.PayPeriodAnchorDate ?? RotaTime.GetWeekStart(date);

        // Floor division, so a date before the anchor still lands in the right period.
        int periodsFromAnchor = (int)Math.Floor((date.DayNumber - anchor.DayNumber) / (double)length);
        DateOnly start = anchor.AddDays(periodsFromAnchor * length);

        return new PayPeriod(start, start.AddDays(length - 1));
    }

    public static PayPeriod Previous(CompanySchedulingSettings settings, DateOnly date) =>
        Containing(settings, Containing(settings, date).From.AddDays(-1));

    // A weekly-based pay period counted from nothing would just be guessing.
    public static bool IsConfigured(CompanySchedulingSettings settings) =>
        settings.PayPeriod == PayPeriodFrequency.Monthly || settings.PayPeriodAnchorDate is not null;

    // "Monthly, 26th to 25th", "Monthly (calendar month)", "Fortnightly".
    public static string Describe(CompanySchedulingSettings settings) => settings.PayPeriod switch
    {
        PayPeriodFrequency.Monthly when settings.PayPeriodStartDay <= 1 => "Monthly (calendar month)",
        PayPeriodFrequency.Monthly => $"Monthly, {Ordinal(settings.PayPeriodStartDay)} to {Ordinal(settings.PayPeriodStartDay - 1)}",
        PayPeriodFrequency.Fortnightly => "Fortnightly",
        PayPeriodFrequency.FourWeekly => "Every four weeks",
        _ => "Weekly"
    };

    public static string Ordinal(int day)
    {
        string suffix = (day % 100) is 11 or 12 or 13 ? "th" : (day % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th"
        };

        return $"{day}{suffix}";
    }
}
