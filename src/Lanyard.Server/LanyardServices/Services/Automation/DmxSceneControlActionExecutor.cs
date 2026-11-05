#nullable enable

using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Lanyard.Application.Services;

public class DmxSceneControlActionExecutor(
    IDmxSceneRunnerService sceneRunner,
    ISystemDbContextFactory contextFactory,
    ILogger<DmxSceneControlActionExecutor> logger) : IActionExecutor
{
    private readonly IDmxSceneRunnerService _sceneRunner = sceneRunner;
    private readonly ISystemDbContextFactory _contextFactory = contextFactory;
    private readonly ILogger<DmxSceneControlActionExecutor> _logger = logger;

    public const string StartScene = "StartScene";
    public const string StopScene = "StopScene";
    public const string StopAllScenes = "StopAllScenes";

    private sealed record DmxSceneControlParameters
    {
        public Guid TargetClientId { get; init; }
        public string Operation { get; init; } = StartScene;
        public Guid? SceneId { get; init; }
        public int? HoldMilliseconds { get; init; }
    }

    public bool CanHandle(string actionType) => actionType == AutomationActionTypes.DmxSceneControl;

    /// <summary>
    /// The kiosk and scene a DMX action points at, for AutomationRuleService to check they belong
    /// to the rule's company when the rule is saved. False when the JSON can't be read.
    /// </summary>
    public static bool TryReadTargets(string parametersJson, out Guid targetClientId, out Guid? sceneId)
    {
        targetClientId = Guid.Empty;
        sceneId = null;

        try
        {
            DmxSceneControlParameters? parameters = JsonSerializer.Deserialize<DmxSceneControlParameters>(parametersJson);

            if (parameters == null)
            {
                return false;
            }

            targetClientId = parameters.TargetClientId;
            sceneId = parameters.Operation == StopAllScenes ? null : parameters.SceneId;

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

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

            // The runner trusts its caller, and ParametersJson is just text: make sure the kiosk
            // and scene are the rule's own company's before driving anything.
            int? ruleCompanyId = await ctx.AutomationRules
                .AsNoTracking()
                .TagWithCallSite()
                .Where(r => r.Id == action.AutomationRuleId)
                .Select(r => (int?)r.CompanyId)
                .FirstOrDefaultAsync();

            if (ruleCompanyId != client.CompanyId)
            {
                _logger.LogWarning("Refused DMX action {ActionId}: kiosk {ClientId} isn't in the rule's company", action.Id, parameters.TargetClientId);
                return (false, "The action's kiosk belongs to a different company");
            }

            if (requiresScene)
            {
                bool sceneOnClient = await ctx.DmxScenes
                    .AsNoTracking()
                    .TagWithCallSite()
                    .AnyAsync(s => s.Id == parameters.SceneId && s.ClientId == parameters.TargetClientId && s.IsActive);

                if (!sceneOnClient)
                {
                    return (false, "The action's DMX scene no longer exists on that kiosk");
                }
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
                    // The runner auto-stops on its own task, so the engine is never
                    // blocked for the duration of the hold.
                    TimeSpan? holdFor = parameters.HoldMilliseconds is > 0
                        ? TimeSpan.FromMilliseconds(parameters.HoldMilliseconds.Value)
                        : null;

                    result = await _sceneRunner.StartSceneAsync(parameters.TargetClientId, parameters.SceneId!.Value, holdFor);
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
