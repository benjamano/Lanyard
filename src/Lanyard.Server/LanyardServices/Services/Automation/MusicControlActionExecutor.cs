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

    /// <summary>Shuffle setting values. Null (or missing) leaves the kiosk's shuffle alone.</summary>
    public const string On = "On";
    public const string Off = "Off";

    private sealed record MusicControlParameters
    {
        public Guid TargetClientId { get; init; }
        public string Operation { get; init; } = string.Empty;
        public Guid? PlaylistId { get; set; }

        // Play only: when the chosen playlist is already loaded on the kiosk, carry on from
        // where it paused instead of starting it again from the top.
        public bool ResumeIfLoaded { get; init; }

        // Play only: "On"/"Off", or null to leave it as it is.
        public string? Shuffle { get; init; }

        // Play only: a RepeatMode name, or null to leave it as it is.
        public string? Repeat { get; init; }
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
                "Play" => await PlayAsync(parameters),
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

    private async Task<Result<bool>> PlayAsync(MusicControlParameters parameters)
    {
        Guid clientId = parameters.TargetClientId;
        bool previousShuffle = _musicPlayerService.GetShuffleEnabled(clientId);
        RepeatMode previousRepeat = _musicPlayerService.GetCurrentRepeatMode(clientId);

        // Settings first, so a freshly loaded shuffled playlist still starts on a random song.
        if (parameters.Shuffle is On or Off)
        {
            await _musicPlayerService.SetShuffle(clientId, parameters.Shuffle == On);
        }

        if (Enum.TryParse(parameters.Repeat, ignoreCase: false, out RepeatMode repeat) &&
            Enum.IsDefined(repeat))
        {
            await _musicPlayerService.SetRepeatMode(clientId, repeat);
        }

        Result<bool> result;

        if (parameters.PlaylistId is not Guid playlistId)
        {
            result = await _musicPlayerService.Play(clientId);
        }
        else if (parameters.ResumeIfLoaded && _musicPlayerService.IsPlaylistLoaded(clientId, playlistId))
        {
            result = await _musicPlayerService.Play(clientId);
        }
        else
        {
            result = await _musicPlayerService.Play(clientId, playlistId: playlistId);
        }

        // A failed step (say the playlist was deleted) shouldn't leave whatever was already
        // playing reshuffled or stuck repeating, so put the settings back.
        if (!result.IsSuccess)
        {
            await _musicPlayerService.SetShuffle(clientId, previousShuffle);
            await _musicPlayerService.SetRepeatMode(clientId, previousRepeat);
        }

        return result;
    }
}
