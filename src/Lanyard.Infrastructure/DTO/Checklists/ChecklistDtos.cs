using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.DTO.Checklists;

// Who ticked a task, as shown on a checklist. A kiosk tick has no UserId; Name is then the kiosk's.
public sealed record ChecklistTickView(
    Guid CompletionId,
    string? UserId,
    string Name,
    DateTime CompletedUtc,
    ChecklistCompletionMethod Method,
    string? Note,
    decimal? Reading);

// One task on a day's checklist, with its live tick if it has one. MinReading/MaxReading are the
// Number task's acceptable range as it was on the day (both null: no range).
public sealed record ChecklistItemView(
    Guid ItemId,
    string Title,
    string? Detail,
    ChecklistResponse Response,
    string? Unit,
    ChecklistTickView? Tick,
    Guid? TaskId = null,
    decimal? MinReading = null,
    decimal? MaxReading = null);

// One checklist on one venue day. CanTick and CantTickReason are for the person asking: a manager's
// board shows the same runs with CanTick worked out for the manager.
public sealed record ChecklistRunView(
    Guid RunId,
    Guid ChecklistId,
    string Name,
    string? Description,
    DateOnly VenueDate,
    DateTime? DueUtc,
    ChecklistRunStatus Status,
    int Done,
    int Total,
    bool CanTick,
    string? CantTickReason,
    IReadOnlyList<ChecklistItemView> Items);

// A location's checklists for one venue day.
public sealed record ChecklistDayView(
    int LocationId,
    string LocationName,
    DateOnly VenueDate,
    IReadOnlyList<ChecklistRunView> Runs);

// One row of a location's checklist history.
public sealed record ChecklistHistoryRow(
    Guid RunId,
    DateOnly VenueDate,
    string Name,
    ChecklistRunStatus Status,
    int Done,
    int Total,
    DateTime? DueUtc,
    DateTime? LastTickUtc);

// Someone who can be picked on the clock-in terminal to tick checklists: today's roster there and
// the location's Managers. Names are the terminal's short form ("Amy C.").
public sealed record TerminalChecklistPerson(string UserId, string DisplayName, bool HasPin);
