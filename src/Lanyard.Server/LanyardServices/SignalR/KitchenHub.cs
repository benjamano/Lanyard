using Lanyard.Application.SignalR.Events;
using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.SignalR;

/// <summary>
/// Pushes live order activity to staff kitchen displays.
///
/// Deliberately a second hub on its own path rather than an addition to SignalRControlHub:
/// that hub is mounted at /websocket behind the kiosk shared-secret gate in Program.cs, which
/// is the wrong gate here. Kitchen displays are signed-in staff, not kiosks, so this hub is
/// authorised by role instead - and keeping them separate means a change to either gate cannot
/// silently widen the other.
///
/// Customers never connect here. Their phones poll their own order's status over HTTP, so a
/// dropped connection on venue wifi retries on the next tick instead of needing a
/// reconnect-modal recovery flow mid-order.
/// </summary>
[Authorize(Roles = "Admin, CanManageKitchen")]
public class KitchenHub(ILogger<KitchenHub> logger) : Hub
{
    private readonly ILogger<KitchenHub> _logger = logger;

    public const string OrderReceivedEvent = "OrderReceived";
    public const string OrderStatusChangedEvent = "OrderStatusChanged";

    /// <summary>
    /// Group name for one venue's kitchen. Sends are always targeted at one of these, never at
    /// Clients.All - a ticket for Ipswich has no business appearing on Wisbech's kitchen screen.
    /// </summary>
    public static string GroupNameFor(int locationId) => $"kitchen-location-{locationId}";

    public async Task JoinLocationGroup(int locationId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupNameFor(locationId));

        _logger.LogInformation("Kitchen display {ConnectionId} joined location {LocationId}", Context.ConnectionId, locationId);
    }

    public async Task LeaveLocationGroup(int locationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupNameFor(locationId));

        _logger.LogInformation("Kitchen display {ConnectionId} left location {LocationId}", Context.ConnectionId, locationId);
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("Kitchen display {ConnectionId} connected", Context.ConnectionId);

        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is null)
        {
            _logger.LogInformation("Kitchen display {ConnectionId} disconnected", Context.ConnectionId);
        }
        else
        {
            _logger.LogWarning(exception, "Kitchen display {ConnectionId} disconnected with an error", Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Server-side entry point for broadcasting order activity, so callers do not each have to know
/// the group-naming or event-name conventions (a mismatched event name fails silently, with the
/// client simply never firing).
/// </summary>
public interface IKitchenHubNotifier
{
    Task NotifyOrderReceivedAsync(int locationId, KitchenOrderTicketDto ticket);

    Task NotifyOrderStatusChangedAsync(int locationId, KitchenOrderTicketDto ticket);
}

public class KitchenHubNotifier(
    IHubContext<KitchenHub> hubContext,
    KitchenOrderEvents orderEvents,
    ILogger<KitchenHubNotifier> logger) : IKitchenHubNotifier
{
    private readonly IHubContext<KitchenHub> _hubContext = hubContext;
    private readonly KitchenOrderEvents _orderEvents = orderEvents;
    private readonly ILogger<KitchenHubNotifier> _logger = logger;

    public async Task NotifyOrderReceivedAsync(int locationId, KitchenOrderTicketDto ticket)
    {
        _logger.LogInformation("Dispatching {Event} for order {OrderId} to location {LocationId}",
            KitchenHub.OrderReceivedEvent, ticket.OrderId, locationId);

        // Raised for the in-process Blazor kitchen display first, so that display still updates
        // even if the hub send below fails - it is the one that staff are actually watching.
        _orderEvents.RaiseOrderReceived(ticket);

        // Awaited, not fire-and-forget: a ticket that never reaches a kitchen display is food
        // that never gets cooked, so a failure here needs to surface rather than be dropped.
        await _hubContext.Clients
            .Group(KitchenHub.GroupNameFor(locationId))
            .SendAsync(KitchenHub.OrderReceivedEvent, ticket);
    }

    public async Task NotifyOrderStatusChangedAsync(int locationId, KitchenOrderTicketDto ticket)
    {
        _logger.LogInformation("Dispatching {Event} for order {OrderId} to location {LocationId} (now {Status})",
            KitchenHub.OrderStatusChangedEvent, ticket.OrderId, locationId, ticket.Status);

        _orderEvents.RaiseOrderStatusChanged(ticket);

        await _hubContext.Clients
            .Group(KitchenHub.GroupNameFor(locationId))
            .SendAsync(KitchenHub.OrderStatusChangedEvent, ticket);
    }
}
