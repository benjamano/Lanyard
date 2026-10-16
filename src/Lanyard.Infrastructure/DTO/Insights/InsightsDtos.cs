namespace Lanyard.Infrastructure.DTO.Insights;

// How a weekday-hour compares with the location's own typical games per staff-hour.
public enum StaffingFlag
{
    None = 0,

    // Too few weeks of that hour in the range (or too little data overall) to judge it.
    InsufficientData = 1,

    // Busier per person on shift than usual, or games played with nobody clocked in.
    Understaffed = 2,

    // Quieter per person on shift than usual, with more than one person on to spare.
    Overstaffed = 3
}

// Admin may name any location of their company; a Manager is always pinned to their own.
public record InsightsQuery(int? LocationId, DateOnly From, DateOnly To)
{
    public const int MaxRangeDays = 93;
    public const int DefaultRangeDays = 28;

    public int Days => To.DayNumber - From.DayNumber + 1;
}

// One venue-local hour (Hour 14 is 14:00-15:00 UK time). Hours are not money: no pay is reported.
public record InsightsSlot(
    DateOnly Date,
    int Hour,
    bool IsOpen,
    string? Daypart,
    int Games,
    int Players,
    decimal ClockedHours,
    decimal RotaHours)
{
    public bool HasActivity => Games > 0 || ClockedHours > 0 || RotaHours > 0;
}

// One weekday-hour across the whole range, e.g. "Saturdays 14:00". Samples is how many open
// occurrences of that hour the range holds, so the averages are per occurrence.
public record InsightsCell(
    DayOfWeek Day,
    int Hour,
    int Samples,
    int Games,
    int Players,
    decimal ClockedHours,
    decimal RotaHours,
    StaffingFlag Flag)
{
    public decimal AverageGames => Samples == 0 ? 0 : (decimal)Games / Samples;
    public decimal AveragePlayers => Samples == 0 ? 0 : (decimal)Players / Samples;

    // Clocked hours in a one-hour slot are the number of people on, on average.
    public decimal AverageStaff => Samples == 0 ? 0 : ClockedHours / Samples;
    public decimal AverageRotaStaff => Samples == 0 ? 0 : RotaHours / Samples;

    public decimal? GamesPerStaffHour => ClockedHours > 0 ? Games / ClockedHours : null;
}

public record InsightsDay(DateOnly Date, int Games, int Players, decimal ClockedHours, decimal RotaHours, int OpenHours);

public record InsightsDaypartRow(string Name, int Games, int Players, decimal ClockedHours, decimal RotaHours)
{
    public decimal? GamesPerStaffHour => ClockedHours > 0 ? Games / ClockedHours : null;
}

public record InsightsTotals(int Games, int Players, decimal ClockedHours, decimal RotaHours, decimal ClockedHoursWhileOpen)
{
    // All clocked time, including setting up and closing down, per game played.
    public decimal? LabourHoursPerGame => Games > 0 ? ClockedHours / Games : null;
    public decimal? GamesPerStaffHour => ClockedHours > 0 ? Games / ClockedHours : null;
    public decimal RotaMinusClockedHours => RotaHours - ClockedHours;
}

// Everything worked out from the raw facts, with no I/O (see InsightsRules).
public record InsightsFigures(
    InsightsTotals Totals,
    List<InsightsSlot> Slots,
    List<InsightsCell> Cells,
    List<InsightsDay> Days,
    List<InsightsDaypartRow> Dayparts,
    decimal? MedianGamesPerStaffHour,
    bool FlagsAvailable,
    string? FlagsUnavailableReason)
{
    // Worst first: understaffed by how far over the median, then overstaffed by how far under.
    public IEnumerable<InsightsCell> FlaggedCells() => Cells
        .Where(x => x.Flag is StaffingFlag.Understaffed or StaffingFlag.Overstaffed)
        .OrderBy(x => x.Flag == StaffingFlag.Understaffed ? 0 : 1)
        .ThenByDescending(x => x.Flag == StaffingFlag.Understaffed
            ? x.GamesPerStaffHour ?? decimal.MaxValue
            : -(x.GamesPerStaffHour ?? 0))
        .ThenBy(x => ((int)x.Day + 6) % 7)
        .ThenBy(x => x.Hour);
}

public record InsightsSummary(
    int LocationId,
    string LocationName,
    DateOnly From,
    DateOnly To,
    int KioskCount,
    int UnassignedKioskCount,
    bool HasOpeningHours,
    bool HasDayparts,
    InsightsFigures Figures);
