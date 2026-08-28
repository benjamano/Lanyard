namespace Lanyard.Infrastructure.Enum;

public static class HallOfFamePeriodExtensions
{
    /// <summary>
    /// Resolves a period to the inclusive UTC lower bound of its window, or null for
    /// <see cref="HallOfFamePeriod.AllTime"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately a pure function of <paramref name="localNow"/> rather than reading the clock
    /// itself, so boundary behaviour is unit-testable without sleeping or a DI-wide clock seam.
    ///
    /// Boundaries are server-local, then converted back to UTC because rows store PlayedAtUtc.
    /// Local is the right basis for a lobby screen: on UTC, a leaderboard would visibly reset at
    /// 1am during British Summer Time, mid-shift. This matches the only other implicitly
    /// server-local value in the app, Client.AutoRestartTimeOfDay.
    /// </remarks>
    public static DateTime? ToUtcLowerBound(this HallOfFamePeriod period, DateTime localNow)
    {
        DateTime localStart;

        switch (period)
        {
            case HallOfFamePeriod.Today:
                localStart = localNow.Date;
                break;

            case HallOfFamePeriod.ThisWeek:
                // Monday-start, hardcoded rather than culture-derived: the venue is UK, and a
                // leaderboard that silently changed its week boundary with the server's culture
                // would be worse than one that is simply always Monday.
                int daysSinceMonday = ((int)localNow.DayOfWeek + 6) % 7;
                localStart = localNow.Date.AddDays(-daysSinceMonday);
                break;

            case HallOfFamePeriod.ThisMonth:
                localStart = new DateTime(localNow.Year, localNow.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
                break;

            case HallOfFamePeriod.AllTime:
            default:
                return null;
        }

        return DateTime.SpecifyKind(localStart, DateTimeKind.Local).ToUniversalTime();
    }
}
