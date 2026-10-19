using Lanyard.Application.Services.Tenancy;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Lanyard.Application.SignalR;

// Runs around every SignalRControlHub call. Hands the kiosk's principal to TenantContext (through
// HubCallerUser) so everything a hub method reads or writes is filtered to that kiosk's company.
public class KioskHubFilter : IHubFilter
{
    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        SetCaller(invocationContext.ServiceProvider, invocationContext.Context);

        return next(invocationContext);
    }

    public Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        SetCaller(context.ServiceProvider, context.Context);

        return next(context);
    }

    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        SetCaller(context.ServiceProvider, context.Context);

        return next(context, exception);
    }

    private static void SetCaller(IServiceProvider services, HubCallerContext caller)
    {
        HubCallerUser? holder = services.GetService<HubCallerUser>();

        if (holder is not null)
        {
            holder.User = caller.User;
        }
    }
}
