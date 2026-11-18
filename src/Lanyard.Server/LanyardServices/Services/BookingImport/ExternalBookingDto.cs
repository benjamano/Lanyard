namespace Lanyard.Application.Services.BookingImport;

// One party booking as read from a Just Step Online report row. All venue-local (the site shows a
// venue's own times); the import turns the date + times into UTC with RotaTime. Money is nullable
// because a cell can be blank. Times are nullable because some rows carry only a date, or a
// free-text slot we couldn't parse - the import falls back to the location's opening time then.
public sealed record ExternalBookingDto(
    string Ref,
    DateOnly PartyDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string PartyType,
    string ContactName,
    string ContactPhone,
    string ChildName,
    DateOnly? ChildDob,
    int Quantity,
    decimal? TotalDue,
    decimal? Paid,
    decimal? Balance,
    bool IsProvisional);
