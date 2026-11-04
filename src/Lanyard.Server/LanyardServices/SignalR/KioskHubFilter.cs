using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Lanyard.Application.SignalR;

// Runs around every SignalRControlHub call. Hands the kiosk's principal to TenantContext (through
// HubCallerUser) so everything a hub method reads or writes is filtered to that kiosk's company,
// and turns away a connection whose credential has been revoked since it connected - a backstop
// for KioskConnectionRegistry, which normally cuts such a connection off straight away.
public class KioskHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        SetCaller(invocationContext.ServiceProvider, invocationContext.Context);

        if (KioskRequestAuthorization.GetCredentialId(invocationContext.Context.User) is Guid credentialId)
        {
            // Cached for a minute, and evicted the moment a credential is revoked.
            Result<KioskIdentity> stillValid = await invocationContext.ServiceProvider
                .GetRequiredService<IKioskCredentialService>()
                .ValidateByIdAsync(credentialId);

            if (!stillValid.IsSuccess && stillValid.Error != KioskCredentialService.CheckUnavailableError)
            {
                invocationContext.Context.Abort();

                throw new HubException("This kiosk's credential is no longer valid.");
            }
        }

        return await next(invocationContext);
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
