#nullable enable

using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Lanyard.Application.Services;

public class DmxSceneControlActionExecutor(
    IDmxSceneRunnerService sceneRunner,
    IDbContextFactory<ApplicationDbContext> contextFactory,
    ILogger<DmxSceneControlActionExecutor> logger) : IActionExecutor
{
    private readonly IDmxSceneRunnerService _sceneRunner = sceneRunner;
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory = contextFactory;
    private readonly ILogger<DmxSceneControlActionExecutor> _logger = logger;

    public const string StartScene = "StartScene";
    public const string StopScene = "StopScene";
    public const string StopAllScenes = "StopAllScenes";

    private sealed record DmxSceneControlParameters
    {
        public Guid TargetClientId { get; init; }
        public string Operation { get; init; } = StartScene;
        public Guid? SceneId { get; init; }
    }

    public bool CanHandle(string actionType) => actionType == AutomationActionTypes.DmxSceneControl;

    public async Task<(bool Success, string? ErrorMessage)> ExecuteAsync(
        AutomationRuleAction action, Guid triggerClientId)
    {
        try
        {
            DmxSceneControlParameters? parameters = JsonSerializer.Deserialize<DmxSceneControlParameters>(
                action.ParametersJson);
            if (parameters == null)
            {
                return (false, "DMX scene control failed: could not deserialize parameters");
            }

            if (parameters.TargetClientId == Guid.Empty)
            {
                return (false, "Action not configured with a client");
            }

            // StopAllScenes targets the whole client, so it is the only operation without a scene.
            bool requiresScene = parameters.Operation != StopAllScenes;
            if (requiresScene && (parameters.SceneId == null || parameters.SceneId == Guid.Empty))
            {
                return (false, "Action not configured with a DMX scene");
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

            Result<bool> result;

            switch (parameters.Operation)
            {
                case StartScene:
                    result = await _sceneRunner.StartSceneAsync(parameters.TargetClientId, parameters.SceneId!.Value);
                    break;
                case StopScene:
                    result = _sceneRunner.StopScene(parameters.SceneId!.Value);
                    break;
                case StopAllScenes:
                    result = _sceneRunner.StopAllScenesForClient(parameters.TargetClientId);
                    break;
                default:
                    return (false, $"Unknown DMX operation: {parameters.Operation}");
            }

            return result.IsSuccess
                ? (true, null)
                : (false, result.Error ?? "Failed to control DMX scene");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DMX scene control action failed for action {ActionId}", action.Id);
            return (false, $"DMX scene control failed: {ex.Message}");
        }
    }

    protected internal virtual bool IsClientConnected(string connectionId) =>
        SignalRControlHub.ConnectedIds.Contains(connectionId);
}
