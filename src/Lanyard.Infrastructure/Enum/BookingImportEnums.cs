namespace Lanyard.Infrastructure.Enum
{
    // How one import run ended. Stored as its int value, so never renumber a member.
    public enum BookingImportRunStatus
    {
        // The run completed and at least one booking was created or updated.
        Success = 1,

        // The run completed but some bookings couldn't be mapped or saved (see Error / counts).
        PartialSuccess = 2,

        // The run completed cleanly but nothing changed (no new or updated bookings).
        NoChanges = 3,

        // Couldn't sign in to Just Step Online with the saved credentials.
        LoginFailed = 4,

        // The run failed before finishing (network error, unexpected page, save error).
        Failed = 5,

        // The run was asked for but skipped: import disabled, no credentials, or feature off.
        Skipped = 6
    }

    // What started an import run. Stored as its int value, so never renumber a member.
    public enum BookingImportTrigger
    {
        // The five-minute background sweep.
        Scheduled = 1,

        // An admin pressed "Run now" on the booking import page.
        Manual = 2
    }
}
