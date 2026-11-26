using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Scheduling;

// What one check did, by venue-local day. Raised = a new alert (and part of the summary
// notification); StillOpen = out of date and already alerted; Resolved = back in line, or no longer
// watched (past, no rota, Parties off), and its alert cleared.
public sealed record PartyStaffingCheckOutcome(List<DateOnly> Raised, List<DateOnly> StillOpen, List<DateOnly> Resolved)
{
    public static PartyStaffingCheckOutcome Empty => new([], [], []);

    public bool AnyOutOfDate => Raised.Count > 0 || StillOpen.Count > 0;
}

// Notices when a party change leaves a day whose rota is already built with the wrong party
// staffing, and raises one Operations alert per location and day for it ("Party staffing out of
// date"), linking to the rota with Auto-build ready for that day. Cleared once the day is back in
// line. Runs as the system tenant: every query is scoped by the location passed in, so it can be
// called from a manager's save and from the booking import alike. It only reads the rota - nothing
// here changes a shift. Failures are logged and returned, never thrown, so a save or an import never
// fails because of it.
public interface IPartyStaffingWatch
{
    // How far ahead it looks, today included.
    const int HorizonDays = 14;

    // Checks these days at the location, raising or resolving each one's alert. Days outside today..
    // today+HorizonDays are left alone, except past days, whose alerts are cleared. actingUserId (who
    // made the change, if a person) is left out of the notification: they see it where they made it.
    Task<Result<PartyStaffingCheckOutcome>> CheckDaysAsync(int locationId, IReadOnlyCollection<DateOnly> days,
        string? actingUserId = null, CancellationToken cancellationToken = default);

    // Every watched day at the location, for a change that can affect all of them (a staffing rule).
    Task<Result<PartyStaffingCheckOutcome>> CheckWatchedDaysAsync(int locationId, string? actingUserId = null,
        CancellationToken cancellationToken = default);

    // Looks again at days that have an open alert (all locations when locationId is null) and clears
    // the ones now in line or in the past. Never raises: a manager who resolved an alert by hand
    // doesn't see it come back until the parties change again. Returns how many it cleared.
    Task<Result<int>> RecheckOpenAsync(int? locationId = null, CancellationToken cancellationToken = default);
}

public static class PartyStaffingAlerts
{
    public const string KeyPrefix = "party-staffing:";

    public static string DedupKey(int locationId, DateOnly day) => $"{KeyPrefix}{locationId}:{day:yyyy-MM-dd}";

    public static bool TryParseDay(string dedupKey, out DateOnly day)
    {
        day = default;
        int split = dedupKey.LastIndexOf(':');

        return dedupKey.StartsWith(KeyPrefix, StringComparison.Ordinal) && split > 0
            && DateOnly.TryParseExact(dedupKey[(split + 1)..], "yyyy-MM-dd", out day);
    }

    // The rota on that day's week, with Auto-build opened for just that day.
    public static string RotaLink(int locationId, DateOnly day) => $"/manage/rota?location={locationId}&date={day:yyyy-MM-dd}&autobuild=day";
}
