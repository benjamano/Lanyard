#nullable enable

using Lanyard.Application.SignalR;
using Lanyard.Application.Services.Clients;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Lanyard.Application.Services;

/// <summary>
/// Stops whatever projection program is running on a client's display and closes the kiosk window
/// it opened.
/// </summary>
/// <remarks>
/// Both halves are needed. IProjectionProgramRunnerService.Stop raises OnProgramStopped, not
/// OnProgramCompletedNaturally, and it is only the latter that ProjectionProgramCompletionListener
/// watches to close the window - so stopping alone would leave the last frame frozen on the screen.
/// </remarks>
public class StopProjectionProgramActionExecutor(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<ApplicationDbContext> contextFactory,
    IProjectionProgramRunnerService runnerService,
    ILogger<StopProjectionProgramActionExecutor> logger) : IActionExecutor
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory = contextFactory;
    private readonly IProjectionProgramRunnerService _runnerService = runnerService;
    private readonly ILogger<StopProjectionProgramActionExecutor> _logger = logger;

    private sealed record StopProjectionProgramParameters
    {
        public Guid TargetClientId { get; init; }
        public int? DisplayIndex { get; init; }
    }

    public bool CanHandle(string actionType) => actionType == AutomationActionTypes.StopProjectionProgram;

    public async Task<(bool Success, string? ErrorMessage)> ExecuteAsync(
        AutomationRuleAction action, Guid triggerClientId)
    {
        try
        {
            StopProjectionProgramParameters? parameters = JsonSerializer.Deserialize<StopProjectionProgramParameters>(
                action.ParametersJson);

            if (parameters == null)
            {
                return (false, "Projection stop failed: could not deserialize parameters");
            }

            if (parameters.TargetClientId == Guid.Empty)
            {
                return (false, "Action not configured with a client");
            }

            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync();

            Client? client = await ctx.Clients
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == parameters.TargetClientId);

            if (client == null)
            {
                return (false, "Client not connected");
            }

            if (string.IsNullOrEmpty(client.MostRecentConnectionId) ||
                !IsClientConnected(client.MostRecentConnectionId))
            {
                return (false, "Client not connected");
            }

            // Matches ResolveScreen on the kiosk and Kiosk.razor's ResolvedDisplayIndex, both of
            // which treat a missing display as 0.
            int displayIndex = parameters.DisplayIndex ?? 0;

            // Deliberately not an error when nothing is running: the usual pairing is a
            // "game started" rule that dismisses an idle screen which may never have appeared.
            _runnerService.Stop(parameters.TargetClientId, displayIndex);

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            IClientService clientService = scope.ServiceProvider.GetRequiredService<IClientService>();

            Result<bool> closeResult = await clientService.CloseTemporaryProjectionWindowOnClientAsync(
                parameters.TargetClientId, displayIndex);

            return closeResult.IsSuccess
                ? (true, null)
                : (false, closeResult.Error ?? "Failed to close the projection window");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stop projection program action failed for action {ActionId}", action.Id);

            return (false, $"Projection stop failed: {ex.Message}");
        }
    }

    protected internal virtual bool IsClientConnected(string connectionId) =>
        SignalRControlHub.ConnectedIds.Contains(connectionId);
}
