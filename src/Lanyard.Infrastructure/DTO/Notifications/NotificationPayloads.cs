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

// A shift nobody is working yet, sent to everyone who could pick it up.
public record OpenShiftPayload(int LocationId, string LocationName, ShiftEmailLine Shift, bool NeedsApproval) : NotificationPayload(LocationId);

// Someone is looking for a swap, sent to colleagues who could take their shift.
public record SwapRequestedPayload(int LocationId, string LocationName, string RequesterName, ShiftEmailLine Shift, string? Note) : NotificationPayload(LocationId);

// A colleague answered your swap request with one of their shifts.
public record SwapOfferedPayload(int LocationId, string LocationName, string OffererName, ShiftEmailLine YourShift, ShiftEmailLine TheirShift) : NotificationPayload(LocationId);

// For managers: a pick-up, call-off or swap waiting for their decision.
public record ShiftClaimPendingPayload(
    int LocationId,
    string LocationName,
    ShiftClaimKind Kind,
    string PersonName,
    ShiftEmailLine Shift,
    string? OtherPersonName,
    ShiftEmailLine? OtherShift,
    string? Note) : NotificationPayload(LocationId);

// For the person who asked: how their pick-up, call-off, swap or swap offer turned out. For a
// swap, Shift is the one they now work (or would have) and OtherShift the one they gave away.
public record ShiftClaimDecidedPayload(
    int LocationId,
    string LocationName,
    ShiftClaimKind Kind,
    bool Approved,
    ShiftEmailLine Shift,
    ShiftEmailLine? OtherShift,
    string? Reason) : NotificationPayload(LocationId);

public record NotificationJob(string UserId, NotificationTopic Topic, NotificationPayload Payload);
