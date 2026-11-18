using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.BookingImport;

// What the import page shows for a location's setup. The password itself is never returned; only
// whether one is stored.
public sealed record BookingImportSettingsView(
    int LocationId,
    bool Enabled,
    string Username,
    bool HasPassword,
    DateTime? UpdateDate);

// A saved change from the import page. NewPassword is null/blank to keep the stored one.
public sealed record BookingImportSettingsInput(
    int LocationId,
    bool Enabled,
    string Username,
    string? NewPassword);

// One run for the history list.
public sealed record BookingImportRunView(
    Guid Id,
    int LocationId,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    BookingImportRunStatus Status,
    BookingImportTrigger Trigger,
    int BookingsSeen,
    int Imported,
    int Updated,
    int Skipped,
    int Failed,
    string? Error)
{
    public TimeSpan? Duration => CompletedAtUtc is DateTime done ? done - StartedAtUtc : null;
}

// Imports party bookings from Just Step Online into the Parties system. Same access rule as the
// rest of Parties: Admin, or a Manager at that location.
public interface IBookingImportService
{
    // The saved setup, or defaults (disabled, no password) when the location has none yet.
    Task<Result<BookingImportSettingsView>> GetSettingsAsync(LocationScope scope, int locationId);

    // Creates or updates the location's setup, encrypting a new password when one is given.
    Task<Result<BookingImportSettingsView>> SaveSettingsAsync(LocationScope scope, BookingImportSettingsInput input, string actingUserId);

    // Signs in with the given username and password (or, when password is null, the stored one) to
    // check the credentials work. Doesn't import anything.
    Task<Result<bool>> TestConnectionAsync(LocationScope scope, int locationId, string username, string? password);

    // Runs the import for a location now (Trigger = Manual), returning the run that was recorded.
    Task<Result<BookingImportRunView>> RunNowAsync(LocationScope scope, int locationId);

    // The most recent runs for a location, newest first.
    Task<Result<List<BookingImportRunView>>> GetHistoryAsync(LocationScope scope, int locationId, int take = 20);

    // The core import for one location, used by the background sweep and by RunNowAsync. Runs
    // unfiltered (system) and stamps the given company, so callers must pass the real company id.
    Task<BookingImportRunView> RunImportAsync(int companyId, int locationId, BookingImportTrigger trigger, CancellationToken cancellationToken = default);
}
