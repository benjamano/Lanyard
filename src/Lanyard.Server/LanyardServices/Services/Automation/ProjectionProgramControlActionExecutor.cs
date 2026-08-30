#nullable enable

using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Lanyard.Application.Services;

/// <summary>
/// Mirrors <see cref="DmxSceneControlActionExecutor"/>'s single-action/many-operations shape,
/// covering everything <see cref="StartProjectionProgramActionExecutor"/> and
/// <see cref="StopProjectionProgramActionExecutor"/> already do, plus the runner's pause/skip
/// controls that previously had no automation-rule equivalent. Kept alongside the older two
/// executors rather than replacing them, so automation rules built before this action type
/// existed keep working unchanged.
/// </summary>
public class ProjectionProgramControlActionExecutor(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<ApplicationDbContext> contextFactory,
    IProjectionProgramRunnerService runnerService,
    ILogger<ProjectionProgramControlActionExecutor> logger) : IActionExecutor
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory = contextFactory;
    private readonly IProjectionProgramRunnerService _runnerService = runnerService;
    private readonly ILogger<ProjectionProgramControlActionExecutor> _logger = logger;

    public const string Start = "Start";
    public const string Stop = "Stop";
    public const string Pause = "Pause";
    public const string Resume = "Resume";
    public const string SkipToNext = "SkipToNext";
    public const string SkipToPrevious = "SkipToPrevious";

    private sealed record ProjectionProgramControlParameters
    {
        public Guid TargetClientId { get; init; }
        public string Operation { get; init; } = Start;
        public Guid? ProjectionProgramId { get; init; }
        public int? DisplayIndex { get; init; }
    }

    public bool CanHandle(string actionType) => actionType == AutomationActionTypes.ProjectionProgramControl;

    public async Task<(bool Success, string? ErrorMessage)> ExecuteAsync(
        AutomationRuleAction action, Guid triggerClientId)
    {
        try
        {
            ProjectionProgramControlParameters? parameters = JsonSerializer.Deserialize<ProjectionProgramControlParameters>(
                action.ParametersJson);

            if (parameters == null)
            {
                return (false, "Projection program control failed: could not deserialize parameters");
            }

            if (parameters.TargetClientId == Guid.Empty)
            {
                return (false, "Action not configured with a client");
            }

            if (parameters.Operation == Start && (parameters.ProjectionProgramId == null || parameters.ProjectionProgramId == Guid.Empty))
            {
                return (false, "Action not configured with a projection program");
            }

            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync();
            Client? client = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
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

            Result<bool> result;

            switch (parameters.Operation)
            {
                case Start:
                    await using (AsyncServiceScope startScope = _scopeFactory.CreateAsyncScope())
                    {
                        IProjectionProgramService projectionProgramService = startScope.ServiceProvider.GetRequiredService<IProjectionProgramService>();

                        result = await projectionProgramService.TriggerProjectionProgramAsync(
                            parameters.ProjectionProgramId!.Value, parameters.TargetClientId, displayIndex);
                    }
                    break;
                case Stop:
                    result = _runnerService.Stop(parameters.TargetClientId, displayIndex);

                    // Both halves matter here, same as StopProjectionProgramActionExecutor: Stop
                    // alone raises OnProgramStopped, not OnProgramCompletedNaturally, which is the
                    // only event the window-close listener watches - so stopping without closing
                    // would leave the last frame frozen on the screen.
                    if (result.IsSuccess)
                    {
                        await using AsyncServiceScope closeScope = _scopeFactory.CreateAsyncScope();
                        IClientService clientService = closeScope.ServiceProvider.GetRequiredService<IClientService>();

                        Result<bool> closeResult = await clientService.CloseTemporaryProjectionWindowOnClientAsync(
                            parameters.TargetClientId, displayIndex);

                        if (!closeResult.IsSuccess)
                        {
                            return (false, closeResult.Error ?? "Failed to close the projection window");
                        }
                    }
                    break;
                case Pause:
                    result = _runnerService.Pause(parameters.TargetClientId, displayIndex);
                    break;
                case Resume:
                    result = _runnerService.Resume(parameters.TargetClientId, displayIndex);
                    break;
                case SkipToNext:
                    result = _runnerService.SkipToNextStep(parameters.TargetClientId, displayIndex);
                    break;
                case SkipToPrevious:
                    result = _runnerService.SkipToPreviousStep(parameters.TargetClientId, displayIndex);
                    break;
                default:
                    return (false, $"Unknown projection program operation: {parameters.Operation}");
            }

            return result.IsSuccess
                ? (true, null)
                : (false, result.Error ?? "Failed to control the projection program");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Projection program control action failed for action {ActionId}", action.Id);
            return (false, $"Projection program control failed: {ex.Message}");
        }
    }

    protected internal virtual bool IsClientConnected(string connectionId) =>
        SignalRControlHub.ConnectedIds.Contains(connectionId);
}
