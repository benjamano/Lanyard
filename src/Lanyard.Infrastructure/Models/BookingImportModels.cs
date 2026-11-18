using System.ComponentModel.DataAnnotations;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models
{
    // Per-location setup for importing party bookings from Just Step Online. One row per location,
    // created on first save; no row = disabled. Each Just Step login is scoped to one venue, so the
    // credentials here belong to that one location and the bookings they pull are tagged with it.
    //
    // The password is stored encrypted (ASP.NET Core Data Protection), never in clear, and is never
    // handed back to the browser - the UI only shows whether one is set.
    public class BookingImportSettings : ICompanyOwned
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        // When off, the background sweep skips this location (and "Run now" refuses).
        public bool Enabled { get; set; }

        // The Just Step Online sign-in email for this location.
        [StringLength(256)]
        public string Username { get; set; } = string.Empty;

        // The password, encrypted with IDataProtector. Empty = none set yet.
        public string PasswordProtected { get; set; } = string.Empty;

        // How often the background job imports, in hours (1 = hourly), and the venue-local hours it is
        // allowed to run between - so it doesn't poll Just Step overnight. The window is [start, end):
        // the default 7..23 runs 07:00-22:59 and rests 23:00-06:59. "Run now" ignores both.
        public int FrequencyHours { get; set; } = 1;
        public int ActiveStartHour { get; set; } = 7;
        public int ActiveEndHour { get; set; } = 23;

        public DateTime? UpdateDate { get; set; }
        public string? UpdatedByUserId { get; set; }

        public bool HasPassword => !string.IsNullOrEmpty(PasswordProtected);
    }

    // One run of the booking import for one location: when it ran, how it ended, and how many
    // bookings it brought in. Kept for a retention window then purged, like kiosk self-tests.
    // Runs that failed to sign in are kept too - "the login stopped working" is useful history.
    public class BookingImportRun : ICompanyOwned
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public DateTime StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }

        public BookingImportRunStatus Status { get; set; }
        public BookingImportTrigger Trigger { get; set; }

        // How many bookings the report returned, and what happened to them.
        public int BookingsSeen { get; set; }
        public int Imported { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }

        [StringLength(500)]
        public string? Error { get; set; }

        public TimeSpan? Duration => CompletedAtUtc is DateTime done ? done - StartedAtUtc : null;
    }
}
