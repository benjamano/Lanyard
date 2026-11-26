using Lanyard.Application.Services.VideoStreaming;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

// The one way a Text widget's words can change from a live screen. Everything else about a
// dashboard is changed in the designer by Admins and Managers (DashboardService); this exists
// so a kiosk set up as a notice board can be updated at the screen itself.
public class KioskTextWidgetService(
    IDbContextFactory<ApplicationDbContext> factory,
    IVideoStreamTokenService tokenService,
    ILogger<KioskTextWidgetService> logger) : IKioskTextWidgetService
{
    // Comfortably more than a screenful of notices, but a stop on someone pasting a novel.
    public const int MaxContentLength = 20_000;

    public const string NotAKioskMessage = "This screen couldn't be confirmed as a kiosk. Restart the kiosk and try again.";

    // Kiosk text is shown as markup, so it's cleaned of scripts and the like before it's stored,
    // as well as when it's shown.
    private static readonly Ganss.Xss.HtmlSanitizer Sanitizer = new();

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IVideoStreamTokenService _tokenService = tokenService;
    private readonly ILogger<KioskTextWidgetService> _logger = logger;

    public async Task<Result<bool>> SaveTextFromKioskAsync(Guid widgetId, Guid clientId, string? viewerToken, string html)
    {
        try
        {
            if (!_tokenService.ValidateViewerToken(clientId, viewerToken))
            {
                _logger.LogWarning("Refused kiosk text edit of widget {WidgetId}: no valid kiosk token for client {ClientId}", widgetId, clientId);
                return Result<bool>.Fail(NotAKioskMessage);
            }

            if (html.Length > MaxContentLength)
            {
                return Result<bool>.Fail($"That's too long to save. Keep it under {MaxContentLength:N0} characters.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Client? client = await ctx.Clients.FirstOrDefaultAsync(x => x.Id == clientId);

            TextAreaWidget? widget = await ctx.DashboardWidgets
                .OfType<TextAreaWidget>()
                .Include(x => x.Dashboard)
                .FirstOrDefaultAsync(x => x.Id == widgetId && x.IsActive);

            if (client is null || widget?.Dashboard is null || !widget.Dashboard.IsActive)
            {
                return Result<bool>.Fail("This text can't be found. It may have been removed from the dashboard.");
            }

            if (!widget.EditableOnKiosk)
            {
                return Result<bool>.Fail("This text can only be changed in the dashboard designer.");
            }

            if (widget.Dashboard.CompanyId != client.CompanyId)
            {
                _logger.LogWarning("Refused kiosk text edit of widget {WidgetId}: client {ClientId} belongs to another company", widgetId, clientId);
                return Result<bool>.Fail("This text can only be changed in the dashboard designer.");
            }

            widget.Content = Sanitizer.Sanitize(html);
            await ctx.SaveChangesAsync();

            _logger.LogInformation("Kiosk {ClientId} updated the text of widget {WidgetId}", clientId, widgetId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Couldn't save the text: {ex.Message}");
        }
    }
}
