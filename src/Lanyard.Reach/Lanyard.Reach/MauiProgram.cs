using Microsoft.Extensions.Logging;
using Lanyard.Reach.Shared.Services;
using Lanyard.Reach.Services;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Lanyard.Reach;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        // Add device-specific services used by the Lanyard.Reach.Shared project
        builder.Services.AddSingleton<IFormFactor, FormFactor>();

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        builder.Services.AddFluentUIComponents(options =>
        {
            options.ValidateClassNames = true;
            options.UseTooltipServiceProvider = true;
            options.HideTooltipOnCursorLeave = true;
        });

        return builder.Build();
    }
}
