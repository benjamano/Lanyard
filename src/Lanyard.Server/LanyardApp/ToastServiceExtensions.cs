using Microsoft.FluentUI.AspNetCore.Components;

namespace Lanyard.App;

public static class ToastServiceExtensions
{
    public static void ShowSuccess(this IToastService service, string message)
        => service.ShowToastAsync(o => { o.Intent = ToastIntent.Success; o.Title = message; });

    public static void ShowError(this IToastService service, string message)
        => service.ShowToastAsync(o => { o.Intent = ToastIntent.Error; o.Title = message; });

    public static void ShowWarning(this IToastService service, string message)
        => service.ShowToastAsync(o => { o.Intent = ToastIntent.Warning; o.Title = message; });

    public static void ShowInfo(this IToastService service, string message)
        => service.ShowToastAsync(o => { o.Intent = ToastIntent.Info; o.Title = message; });
}
