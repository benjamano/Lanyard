using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.OperationsAlerts;

public enum OperationsAlertChangeKind
{
    // A new alert: the page's live region announces these.
    Raised,

    // The same problem seen again (count, last seen, detail or severity moved).
    Refreshed,

    Acknowledged,
    Resolved
}

public record OperationsAlertChanged(
    int CompanyId,
    int? LocationId,
    Guid AlertId,
    OperationsAlertChangeKind Kind,
    OperationsAlertSeverity Severity,
    string Title);

// Tells open alert pages and widgets that an alert changed, so they refresh without polling. Every
// write goes through OperationsAlertWriter or OperationsAlertService, which publish here after
// saving. Listeners must check the company and location themselves (OperationsAlertAccess.CanSee):
// events from every company go to every listener. Same in-process pattern as IKioskFleetEventBus;
// there is no browser SignalR hub, because Blazor pages already run on the server.
public interface IOperationsAlertEventBus
{
    event Action<OperationsAlertChanged>? OnChanged;

    void Publish(OperationsAlertChanged change);
}

public class OperationsAlertEventBus : IOperationsAlertEventBus
{
    public event Action<OperationsAlertChanged>? OnChanged;

    public void Publish(OperationsAlertChanged change) => OnChanged?.Invoke(change);
}
