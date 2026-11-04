using Lanyard.Infrastructure.DTO.Insights;
using Lanyard.Infrastructure.DTO.Scheduling;

namespace Lanyard.App.Components.Insights;

// What the typical-week heatmap shades by.
public enum InsightsMetric
{
    Games,
    Players,
    Staff,
    GamesPerStaffHour
}

// Labels and number formats shared by the Insights page and its dashboard widget.
public static class InsightsDisplay
{
    public static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public static string PageUrl(int? locationId, DateOnly? from = null, DateOnly? to = null)
    {
        List<string> query = [];

        if (locationId is int location)
        {
            query.Add($"location={location}");
        }

        if (from is DateOnly f)
        {
            query.Add($"from={f:yyyy-MM-dd}");
        }

        if (to is DateOnly t)
        {
            query.Add($"to={t:yyyy-MM-dd}");
        }

        return query.Count == 0 ? "/manage/insights" : $"/manage/insights?{string.Join('&', query)}";
    }

    public static string ShortDay(DayOfWeek day) => RotaFormat.Uk.DateTimeFormat.GetAbbreviatedDayName(day);

    public static string MetricLabel(InsightsMetric metric) => metric switch
    {
        InsightsMetric.Games => "Games",
        InsightsMetric.Players => "Players",
        InsightsMetric.Staff => "Staff on",
        InsightsMetric.GamesPerStaffHour => "Games per staff hour",
        _ => metric.ToString()
    };

    public static decimal Value(InsightsCell cell, InsightsMetric metric) => metric switch
    {
        InsightsMetric.Games => cell.AverageGames,
        InsightsMetric.Players => cell.AveragePlayers,
        InsightsMetric.Staff => cell.AverageStaff,
        InsightsMetric.GamesPerStaffHour => cell.GamesPerStaffHour ?? 0,
        _ => 0
    };

    // One decimal place, and no ".0" on whole numbers, so a grid of small values stays readable.
    public static string Format(decimal value) => Math.Round(value, 1).ToString("0.#", RotaFormat.Uk);

    public static string Format(decimal? value, string whenMissing = "–") => value is decimal v ? Format(v) : whenMissing;

    public static string Hours(decimal hours) => $"{Math.Round(hours, 1).ToString("#,0.#", RotaFormat.Uk)}h";

    public static string? FlagGlyph(StaffingFlag flag) => flag switch
    {
        StaffingFlag.Understaffed => "▲",
        StaffingFlag.Overstaffed => "▼",
        _ => null
    };

    public static string FlagLabel(StaffingFlag flag) => flag switch
    {
        StaffingFlag.Understaffed => "Busy for the staff on",
        StaffingFlag.Overstaffed => "Quiet for the staff on",
        StaffingFlag.InsufficientData => "Not enough data yet",
        _ => "About as usual"
    };

    public static string CellName(InsightsCell cell) =>
        $"{RotaFormat.Uk.DateTimeFormat.GetDayName(cell.Day)}s {cell.Hour:00}:00";

    // The hover text for a heatmap cell, and the line in the "hours to look at" list.
    public static string Describe(InsightsCell cell)
    {
        string staff = cell.ClockedHours > 0 ? $"{Format(cell.AverageStaff)} staff on" : "nobody clocked in";
        string rate = cell.GamesPerStaffHour is decimal r ? $", {Format(r)} games per staff hour" : string.Empty;
        string flag = FlagGlyph(cell.Flag) is null ? string.Empty : $" · {FlagLabel(cell.Flag)}";
        string weeks = cell.Samples == 1 ? "1 week" : $"{cell.Samples} weeks";

        return $"{CellName(cell)}: {Format(cell.AverageGames)} games, {Format(cell.AveragePlayers)} players, {staff}{rate} (average of {weeks}){flag}";
    }
}
