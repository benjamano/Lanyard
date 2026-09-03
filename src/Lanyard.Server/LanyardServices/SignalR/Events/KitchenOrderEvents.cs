using Lanyard.Shared.DTO;

namespace Lanyard.Application.SignalR.Events;

/// <summary>
/// In-process notification of kitchen order activity, for the staff kitchen display.
///
/// This exists alongside <see cref="IKitchenHubNotifier"/>'s SignalR sends rather than instead of
/// them, because they serve different consumers:
///
///  - KitchenHub is for displays outside this process (a kiosk client, a second screen app).
///  - This event is for the Blazor kitchen display, which already holds a live server circuit.
///    Having that page dial back into its own hub as a SignalR client would mean re-presenting
///    the user's auth cookie from inside a circuit that no longer has an HttpContext - awkward,
///    fragile, and pointless when the page is running in the same process that raised the event.
///
/// Both are raised from the same place (KitchenHubNotifier) so they cannot drift apart. The same
/// pattern is used for projection and music events - see SignalRProjectionControlHubEvents.
/// </summary>
public class KitchenOrderEvents
{
    public event Action<KitchenOrderTicketDto>? OnOrderReceived;

    public event Action<KitchenOrderTicketDto>? OnOrderStatusChanged;

    public void RaiseOrderReceived(KitchenOrderTicketDto ticket) => OnOrderReceived?.Invoke(ticket);

    public void RaiseOrderStatusChanged(KitchenOrderTicketDto ticket) => OnOrderStatusChanged?.Invoke(ticket);
}
