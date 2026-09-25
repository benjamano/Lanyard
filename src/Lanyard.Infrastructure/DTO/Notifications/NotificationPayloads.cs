using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.DTO.Notifications;

// One shift as it appears in an email: "Mon 5 Oct · 09:00–17:00 · Supervisor". Formatted when
// the notification is queued, so delivery needs no rota lookups.
public record ShiftEmailLine(DateOnly Date, string TimeRange, string? PositionName);

// What a notification says, as plain data. Nothing here holds a service or a DbContext: jobs are
// delivered later on a background worker, in their own scope. Each payload names the location it
// belongs to, which is what company branding (logo, colour) is resolved from.
public abstract record NotificationPayload(int LocationId);

public record RotaChangedPayload(
    int LocationId,
    string LocationName,
    List<ShiftEmailLine> Added,
    List<ShiftEmailLine> Changed,
    List<ShiftEmailLine> Removed) : NotificationPayload(LocationId);

public record ShiftReminderPayload(int LocationId, string LocationName, ShiftEmailLine Shift) : NotificationPayload(LocationId);

public record TimeOffRequestedPayload(
    int LocationId,
    string RequesterName,
    string TypeName,
    DateOnly Start,
    DateOnly End,
    string Amount,
    string? Notes) : NotificationPayload(LocationId);

public record TimeOffDecidedPayload(
    int LocationId,
    string TypeName,
    DateOnly Start,
    DateOnly End,
    TimeOffEmailOutcome Outcome,
    string? Reason,
    string? DecidedByName) : NotificationPayload(LocationId);

public record NotificationJob(string UserId, NotificationTopic Topic, NotificationPayload Payload);
