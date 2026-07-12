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

public class StartProjectionProgramActionExecutor(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<ApplicationDbContext> contextFactory,
    ILogger<StartProjectionProgramActionExecutor> logger) : IActionExecutor
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory = contextFactory;
    private readonly ILogger<StartProjectionProgramActionExecutor> _logger = logger;

    private sealed record StartProjectionProgramParameters
    {
        public Guid TargetClientId { get; init; }
        public Guid ProjectionProgramId { get; init; }
        public int? DisplayIndex { get; init; }
    }

    public bool CanHandle(string actionType) => actionType == AutomationActionTypes.StartProjectionProgram;

    public async Task<(bool Success, string? ErrorMessage)> ExecuteAsync(
        AutomationRuleAction action, Guid triggerClientId)
    {
        try
        {
            StartProjectionProgramParameters? parameters = JsonSerializer.Deserialize<StartProjectionProgramParameters>(
                action.ParametersJson);
            if (parameters == null)
            {
                return (false, "Projection trigger failed: could not deserialize parameters");
            }

            if (parameters.TargetClientId == Guid.Empty || parameters.ProjectionProgramId == Guid.Empty)
            {
                return (false, "Action not configured with a client and projection program");
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

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            IProjectionProgramService projectionProgramService = scope.ServiceProvider.GetRequiredService<IProjectionProgramService>();

            Result<bool> result = await projectionProgramService.TriggerProjectionProgramAsync(
                parameters.ProjectionProgramId, parameters.TargetClientId, parameters.DisplayIndex);

            return result.IsSuccess
                ? (true, null)
                : (false, result.Error ?? "Failed to trigger projection program");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Start projection program action failed for action {ActionId}", action.Id);
            return (false, $"Projection trigger failed: {ex.Message}");
        }
    }

    protected internal virtual bool IsClientConnected(string connectionId) =>
        SignalRControlHub.ConnectedIds.Contains(connectionId);
}
