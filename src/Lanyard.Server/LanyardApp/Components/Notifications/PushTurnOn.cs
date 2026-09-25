using Lanyard.Application.Services.Notifications;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Lanyard.App.Components.Notifications;

// The "Turn on notifications" button, shared by NotificationsCard and InstallAppPrompt: binding the
// plain-JS tap handler (see lanyardPush.bindEnableButton) and handling what it reports back, so
// both places save and word the outcome the same way.
internal static class PushTurnOn
{
    public static async Task BindAsync<T>(IJSRuntime js, ElementReference button, DotNetObjectReference<T> dotNetRef, string publicKey)
        where T : class
    {
        try
        {
            await js.InvokeVoidAsync("lanyardPush.bindEnableButton", button, dotNetRef, publicKey);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    // Saves the new subscription and tells the person how it went. True when notifications are now on.
    public static async Task<bool> HandleResultAsync(
        string permission,
        PushSubscriptionInput? subscription,
        string? userId,
        DeviceReport device,
        IPushSubscriptionService subscriptionService,
        INotificationService toast)
    {
        switch (permission)
        {
            case "granted" when subscription is not null && !string.IsNullOrEmpty(userId):
                Result<bool> saved = await subscriptionService.SaveAsync(userId, subscription, device);

                if (saved.IsSuccess)
                {
                    toast.ShowSuccess("Notifications are on for this device.");
                    return true;
                }

                toast.ShowError($"Couldn't turn on notifications: {saved.Error}");
                return false;

            case "denied":
                toast.ShowWarning("Notifications are blocked. You can allow them later in your browser's site settings.");
                return false;

            case "default":
                // The browser's prompt was closed without an answer; nothing to say.
                return false;

            default:
                toast.ShowError("Couldn't turn on notifications on this device.");
                return false;
        }
    }
}
