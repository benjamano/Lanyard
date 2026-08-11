using Microsoft.FluentUI.AspNetCore.Components;

namespace Lanyard.App;

public static class ToastServiceExtensions
{
    public static void ShowSuccess(this INotificationService service, string message)
        => service.ShowToastAsync(o => { o.Intent = ToastIntent.Success; o.Title = message; });

    public static void ShowError(this INotificationService service, string message)
        => service.ShowToastAsync(o => { o.Intent = ToastIntent.Error; o.Title = message; });

    public static void ShowWarning(this INotificationService service, string message)
        => service.ShowToastAsync(o => { o.Intent = ToastIntent.Warning; o.Title = message; });

    public static void ShowInfo(this INotificationService service, string message)
        => service.ShowToastAsync(o => { o.Intent = ToastIntent.Info; o.Title = message; });
}
