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

public class MusicControlActionExecutor(
    IMusicPlayerService musicPlayerService,
    ISystemDbContextFactory contextFactory,
    ILogger<MusicControlActionExecutor> logger) : IActionExecutor
{
    private readonly IMusicPlayerService _musicPlayerService = musicPlayerService;
    private readonly ISystemDbContextFactory _contextFactory = contextFactory;
    private readonly ILogger<MusicControlActionExecutor> _logger = logger;

    private sealed record MusicControlParameters
    {
        public Guid TargetClientId { get; init; }
        public string Operation { get; init; } = string.Empty;
        public Guid? PlaylistId { get; set; }
    }

    public bool CanHandle(string actionType) => actionType == AutomationActionTypes.MusicControl;

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
            // Only the connection is needed, not the whole client row.
            string? connectionId = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(c => c.Id == parameters.TargetClientId)
                .Select(c => c.MostRecentConnectionId)
                .FirstOrDefaultAsync();

            if (string.IsNullOrEmpty(connectionId) || !SignalRControlHub.ConnectedIds.Contains(connectionId))
            {
                return (false, "Client not connected");
            }

            Result<bool> result = parameters.Operation switch
            {
                "Play" when parameters.PlaylistId is Guid playlistId =>
                    await _musicPlayerService.Play(parameters.TargetClientId, playlistId: playlistId),
                "Play" => await _musicPlayerService.Play(parameters.TargetClientId),
                "Pause" => await _musicPlayerService.Pause(parameters.TargetClientId),
                _ => Result<bool>.Fail($"Action type not supported: {parameters.Operation}")
            };

            if (!result.IsSuccess)
            {
                return (false, result.Error?.StartsWith("Action type not supported") == true
                    ? result.Error
                    : $"Music operation failed: {result.Error}");
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
