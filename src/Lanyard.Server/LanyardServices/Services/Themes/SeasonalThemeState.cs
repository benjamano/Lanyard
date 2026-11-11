namespace Lanyard.Application.Services.Themes;

// Per-circuit signal: the account card and the company picker raise it after saving, and
// MainLayout listens so the decorations change straight away without a page reload.
public class SeasonalThemeState
{
    public event Func<Task>? Changed;

    public async Task NotifyChangedAsync()
    {
        if (Changed is { } handler)
        {
            await handler.Invoke();
        }
    }
}
