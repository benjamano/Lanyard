using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services;

public interface IKioskTextWidgetService
{
    /// <summary>
    /// Saves the text of a Text widget typed on a kiosk screen. Kiosk screens run signed out, so
    /// the kiosk proves itself with the viewer token its client was issued for this window. The
    /// widget must allow kiosk editing, and its dashboard must belong to the kiosk's company.
    /// </summary>
    Task<Result<bool>> SaveTextFromKioskAsync(Guid widgetId, Guid clientId, string? viewerToken, string html);
}
