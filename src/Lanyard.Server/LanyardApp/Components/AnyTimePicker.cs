using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Lanyard.App.Components;

/// <summary>
/// A native-rendered <see cref="FluentTimePicker{TValue}"/> that accepts any time of day.
/// Fluent's docs say Native mode ignores StartHour/EndHour/Increment, but 5.0.0-rc.5 still
/// copies them onto the browser input as min="08:00" max="18:00" step="15", so nothing
/// before 8am or after 6pm can be entered. This overwrites them with a full-day range once
/// Fluent has applied its own. Delete it and go back to plain FluentTimePicker once
/// https://github.com/microsoft/fluentui-blazor ships a fix (tracked in benjamano/Lanyard#189).
/// </summary>
public class AnyTimePicker<TValue> : FluentTimePicker<TValue>
{
    private const string CopyToShadow = "Microsoft.FluentUI.Blazor.Utilities.Attributes.copyToShadow";

    public AnyTimePicker(LibraryConfiguration configuration) : base(configuration)
    {
        RenderStyle = DatePickerRenderStyle.Native;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        if (firstRender)
        {
            await JSRuntime.InvokeVoidAsync(CopyToShadow, Id, "[part='control']", "min", "00:00");
            await JSRuntime.InvokeVoidAsync(CopyToShadow, Id, "[part='control']", "max", "23:59");
            // step is in seconds for <input type="time">; 60 = any minute, no seconds field.
            await JSRuntime.InvokeVoidAsync(CopyToShadow, Id, "[part='control']", "step", 60);
        }
    }
}
