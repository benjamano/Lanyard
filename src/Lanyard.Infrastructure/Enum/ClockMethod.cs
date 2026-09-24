namespace Lanyard.Infrastructure.Enum;

public enum ClockMethod
{
    // Tapped their name on the terminal and typed their PIN.
    Pin = 0,

    // Scanned the terminal's rotating QR code with their own signed-in phone.
    Qr = 1,

    // Entered or corrected by a manager on the Timesheets page.
    Manual = 2,

    // Closed by the system, e.g. an entry left open for 16 hours or an account deleted mid-shift.
    Automatic = 3
}

public enum ClockDirection
{
    In = 0,
    Out = 1
}
