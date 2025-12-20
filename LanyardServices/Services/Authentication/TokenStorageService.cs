using Microsoft.JSInterop;

namespace Lanyard.Application.Services.Authentication;

public class TokenStorageService
{
    private readonly IJSRuntime _jsRuntime;

    public TokenStorageService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task SetTokenAsync(string token)
    {
        // Store in localStorage for client-side access
        await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "authToken", token);
        
        // Store in cookie for server-side middleware access
        await _jsRuntime.InvokeVoidAsync("eval", 
            $"document.cookie = 'authToken={token}; path=/; max-age=86400; SameSite=Strict'");
    }

    public async Task<string?> GetTokenAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "authToken");
        }
        catch
        {
            return null;
        }
    }

    public async Task RemoveTokenAsync()
    {
        try
        {
            // Remove from localStorage
            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "authToken");
            
            // Remove cookie
            await _jsRuntime.InvokeVoidAsync("eval", 
                "document.cookie = 'authToken=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT'");
        }
        catch (TaskCanceledException)
        {
            return;
        }
    }
}
