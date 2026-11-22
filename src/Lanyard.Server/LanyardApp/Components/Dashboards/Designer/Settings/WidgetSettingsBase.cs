using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components;

namespace Lanyard.App.Components.Dashboards.Designer.Settings;

// The shape every inline settings panel has (see WidgetDescriptor.SettingsComponent): the widget,
// edited in place, and OnChanged, raised with a short description after every edit so the canvas
// redraws straight away and the change can be undone.
public abstract class WidgetSettingsBase<TWidget> : ComponentBase where TWidget : DashboardWidget
{
    [Parameter, EditorRequired]
    public required DashboardWidget Widget { get; set; }

    [Parameter]
    public EventCallback<string> OnChanged { get; set; }

    protected TWidget W => (TWidget)Widget;

    protected Task SetAsync(Action change, string description)
    {
        change();
        return OnChanged.InvokeAsync(description);
    }
}
