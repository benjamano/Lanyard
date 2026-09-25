namespace Lanyard.Infrastructure.DTO.Scheduling;

// What the UI is allowed to know about a user's clock-in PIN: whether one exists and when it was
// set. The hash itself never leaves the service.
public record ClockInPinStatus(bool HasPin, DateTime? SetDate);
