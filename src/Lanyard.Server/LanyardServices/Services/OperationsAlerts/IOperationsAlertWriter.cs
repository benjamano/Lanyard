using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.OperationsAlerts;

// One problem a producer has noticed. CompanyId must be the real company: producers run as the
// unfiltered system tenant, where a 0 would otherwise be stamped as Play2Day. DedupKey names the
// problem ("kiosk-offline:{clientId}"), so raising it again refreshes the same alert. NotifyTopic
// is null when the producer sends its own notifications (the kiosk monitor does).
public record OperationsAlertRaise(
    int CompanyId,
    int? LocationId,
    OperationsAlertType Type,
    OperationsAlertSeverity Severity,
    string DedupKey,
    string Title,
    string? Detail = null,
    string? LinkUrl = null,
    Guid? SubjectClientId = null,
    Guid? SubjectRuleId = null,
    string? SubjectUserId = null,
    NotificationTopic? NotifyTopic = null);

public enum OperationsAlertRaiseOutcome
{
    // A new alert, announced on the page and notified (when the raise names a topic).
    Created,

    // An unresolved alert with the same key already existed and was refreshed.
    Refreshed,

    // Operations alerts are off for the company, or it is a demo or inactive company.
    Skipped
}

// The producer side of operations alerts: hosted services and singletons (kiosk monitor,
// automation engine, staff sweeps) raise and clear alerts here. Unfiltered by design, so every
// call names its company. Nothing here throws for expected problems; a failed write is a
// Result.Fail the producer logs and moves on from.
public interface IOperationsAlertWriter
{
    Task<Result<OperationsAlertRaiseOutcome>> RaiseAsync(OperationsAlertRaise raise, CancellationToken cancellationToken = default);

    // Resolves the unresolved alert with this key as "cleared automatically". Ok(false) when there
    // was none, which is the usual case: most recoveries follow no alert at all.
    Task<Result<bool>> ResolveAsync(int companyId, string dedupKey, string? note = null, CancellationToken cancellationToken = default);
}
