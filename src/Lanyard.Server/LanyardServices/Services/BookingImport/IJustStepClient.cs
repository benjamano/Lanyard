using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.BookingImport;

// Talks to Just Step Online: signs in with a location's credentials and reads its booking report.
// Nothing here touches the database - it just fetches and parses, returning a Result the caller
// turns into bookings. A sign-in failure is a plain Result.Fail, not an exception.
public interface IJustStepClient
{
    // Signs in and returns the bookings between the two dates (inclusive). On a bad login or an
    // unreadable page, Result.Fail with a short reason; LoginFailed is distinguished so the caller
    // can record it separately.
    Task<Result<IReadOnlyList<ExternalBookingDto>>> FetchBookingsAsync(
        string username, string password, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    // Signs in only, for the "Test connection" button. Ok(true) when the credentials work.
    Task<Result<bool>> TestLoginAsync(string username, string password, CancellationToken cancellationToken = default);
}
