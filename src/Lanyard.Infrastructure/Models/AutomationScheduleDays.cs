namespace Lanyard.Infrastructure.Models;

/// <summary>
/// Shared parse/serialize/match logic for <see cref="AutomationRule.ScheduledDaysOfWeek"/>, so the
/// engine, the validator, and the edit dialog all agree on the same CSV format.
/// </summary>
public static class AutomationScheduleDays
{
    /// <summary>
    /// Canonicalizes a set of days into a sorted, deduped CSV of <see cref="DayOfWeek"/> integer
    /// values, e.g. "1,2,3,4,5".
    /// </summary>
    public static string Serialize(IEnumerable<DayOfWeek> days)
    {
        return string.Join(',', days.Select(d => (int)d).Distinct().OrderBy(d => d));
    }

    /// <summary>
    /// Parses a CSV of <see cref="DayOfWeek"/> integer values. Null or empty is treated as
    /// "every day" rather than "no days", since that's the more useful default for a schedule rule
    /// nobody has configured days for yet.
    /// </summary>
    public static bool TryParse(string? csv, out List<DayOfWeek> days)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            days = [.. System.Enum.GetValues<DayOfWeek>()];
            return true;
        }

        List<DayOfWeek> parsed = [];
        foreach (string part in csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out int value) || value < 0 || value > 6)
            {
                days = [];
                return false;
            }

            parsed.Add((DayOfWeek)value);
        }

        // A non-blank CSV that contains no usable day tokens (e.g. ",", " , ") must not parse to
        // "every day"'s empty-list representation - that would report success while producing a
        // rule that Matches() then rejects for every day, silently unfireable.
        if (parsed.Count == 0)
        {
            days = [];
            return false;
        }

        days = parsed;
        return true;
    }

    /// <summary>
    /// Fails closed: a malformed value must not make a schedule rule fire every day, so an
    /// unparsable CSV matches nothing rather than falling back to "every day".
    /// </summary>
    public static bool Matches(string? csv, DayOfWeek day)
    {
        if (!TryParse(csv, out List<DayOfWeek> days))
        {
            return false;
        }

        return days.Contains(day);
    }
}
