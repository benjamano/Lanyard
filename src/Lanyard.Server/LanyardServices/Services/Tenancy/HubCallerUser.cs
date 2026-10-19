using System.Security.Claims;

namespace Lanyard.Application.Services.Tenancy;

// Who is calling the current SignalR hub method. Scoped, so one per hub invocation; set by
// KioskHubFilter before the hub method runs. TenantContext reads it first, because a hub method
// isn't a circuit and HttpContext isn't reliable inside a hub (it is the connection's original
// request, and isn't there at all on some transports).
public sealed class HubCallerUser
{
    public ClaimsPrincipal? User { get; set; }
}
