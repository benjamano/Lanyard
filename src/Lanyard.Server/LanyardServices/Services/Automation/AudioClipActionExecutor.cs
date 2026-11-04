using Lanyard.Application.Services.AudioClips;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Lanyard.Application.Services;

/// <summary>
/// Plays an audio clip over the music on a kiosk, for automation rules such as "when a game ends
/// with a winner, play the winners' jingle". Goes through the same playback service as the Audio
/// Clips page, so the clip's company and location rules and the play log all apply, and it never
/// counts as changing the music by hand.
/// </summary>
public class AudioClipActionExecutor(
    IAudioClipPlaybackService playback,
    ISystemDbContextFactory contextFactory,
    ILogger<AudioClipActionExecutor> logger) : IActionExecutor
{
    public sealed record AudioClipActionParameters
    {
        public Guid TargetClientId { get; init; }
        public Guid AudioClipId { get; init; }
    }

    public bool CanHandle(string actionType) => actionType == AutomationActionTypes.PlayAudioClip;

    public async Task<(bool Success, string? ErrorMessage)> ExecuteAsync(AutomationRuleAction action, Guid triggerClientId)
    {
        try
        {
            AudioClipActionParameters? parameters = JsonSerializer.Deserialize<AudioClipActionParameters>(action.ParametersJson);

            if (parameters is null || parameters.TargetClientId == Guid.Empty)
            {
                return (false, "Action not configured with a client");
            }

            if (parameters.AudioClipId == Guid.Empty)
            {
                return (false, "Action not configured with an audio clip");
            }

            await using ApplicationDbContext ctx = await contextFactory.CreateDbContextAsync();

            // The kiosk's company: the playback service refuses a clip from any other.
            int? companyId = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == parameters.TargetClientId)
                .Select(x => (int?)x.CompanyId)
                .FirstOrDefaultAsync();

            if (companyId is not int company)
            {
                return (false, "Client not found");
            }

            Result<List<AudioClipKioskOutcome>> result = await playback.PlayAsync(
                new AudioClipPlayRequest(parameters.AudioClipId, [parameters.TargetClientId], company, AudioClipPlaySource.Automation, null));

            if (!result.IsSuccess)
            {
                return (false, result.Error);
            }

            AudioClipKioskOutcome? outcome = result.Data?.SingleOrDefault();

            return outcome?.Status == AudioClipPlayStatus.Sent
                ? (true, null)
                : (false, outcome?.Error ?? "The clip wasn't sent");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audio clip action failed for rule action {ActionId}", action.Id);
            return (false, $"Audio clip action failed: {ex.Message}");
        }
    }
}
