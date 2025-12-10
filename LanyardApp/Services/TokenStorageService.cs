using Microsoft.JSInterop;

namespace LanyardApp.Services
{
    public class TokenStorageService
    {
        private readonly IJSRuntime _jsRuntime;

        public TokenStorageService(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        public async Task SetTokenAsync(string token)
        {
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "authToken", token);
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
                await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "authToken");
            }
            catch (TaskCanceledException ex)
            {
                return;
            }
        }
    }
}
