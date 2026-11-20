using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.DTO.OperationsAlerts;

// One alert as the page and widget show it. SubjectName is the staff member the alert is about,
// read through the link (never stored on the alert); the acknowledged/resolved names likewise.
public record OperationsAlertView(
    Guid Id,
    OperationsAlertType Type,
    OperationsAlertSeverity Severity,
    OperationsAlertStatus Status,
    string Title,
    string? Detail,
    string? SubjectName,
    int? LocationId,
    string? LocationName,
    string? LinkUrl,
    DateTime RaisedAtUtc,
    DateTime LastSeenAtUtc,
    int OccurrenceCount,
    string? AcknowledgedByName,
    DateTime? AcknowledgedAtUtc,
    string? ResolvedByName,
    DateTime? ResolvedAtUtc,
    OperationsAlertResolution? Resolution,
    string? ResolutionNote);

// Filters for the alerts page. LocationId only narrows things for an Admin; a Manager always gets
// the location they signed in to. Null fields mean "any".
public record OperationsAlertQuery(
    OperationsAlertType? Type = null,
    OperationsAlertSeverity? MinimumSeverity = null,
    int? LocationId = null,
    bool OnlyUnacknowledged = false);

public record OperationsAlertHistoryPage(List<OperationsAlertView> Alerts, int TotalCount);

// Open counts for the nav badge and the widget header: Open is not yet acknowledged.
public record OperationsAlertCounts(int Open, int Acknowledged, int Critical);
