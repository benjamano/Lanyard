#nullable enable

using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Lanyard.Application.Services;

public class MusicControlActionExecutor(
    MusicPlayerService musicPlayerService,
    IDbContextFactory<ApplicationDbContext> contextFactory,
    ILogger<MusicControlActionExecutor> logger) : IActionExecutor
{
    private readonly MusicPlayerService _musicPlayerService = musicPlayerService;
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory = contextFactory;
    private readonly ILogger<MusicControlActionExecutor> _logger = logger;

    private sealed record MusicControlParameters
    {
        public Guid TargetClientId { get; init; }
        public string Operation { get; init; } = string.Empty;
        public Guid? PlaylistId { get; set; }
    }

    public bool CanHandle(string actionType) => actionType == "MusicControl";

    public async Task<(bool Success, string? ErrorMessage)> ExecuteAsync(
        AutomationRuleAction action, Guid triggerClientId)
    {
        try
        {
            MusicControlParameters? parameters = JsonSerializer.Deserialize<MusicControlParameters>(
                action.ParametersJson);
            if (parameters == null)
            {
                return (false, "Music operation failed: could not deserialize parameters");
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
                !SignalRControlHub.ConnectedIds.Contains(client.MostRecentConnectionId))
            {
                return (false, "Client not connected");
            }

            switch (parameters.Operation)
            {
                case "Play":
                    if (parameters.PlaylistId == null)
                    {
                        await _musicPlayerService.Play(parameters.TargetClientId);
                    }
                    else
                    {
                        await _musicPlayerService.Play(parameters.TargetClientId, playlistId: parameters.PlaylistId ?? Guid.Empty);
                    }
                    break;
                case "Pause":
                    await _musicPlayerService.Pause(parameters.TargetClientId);
                    break;
                default:
                    return (false, $"Action type not supported: {parameters.Operation}");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Music control action failed for action {ActionId}", action.Id);
            return (false, $"Music operation failed: {ex.Message}");
        }
    }
}
