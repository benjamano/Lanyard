using Microsoft.JSInterop;

namespace Lanyard.App.Extensions;

public static class ClipboardExtensions
{
    public static async Task<bool> TryCopyToClipboardAsync(this IJSRuntime js, string text)
    {
        try
        {
            await js.InvokeVoidAsync("navigator.clipboard.writeText", text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
