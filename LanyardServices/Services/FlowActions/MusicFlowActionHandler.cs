using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Lanyard.Infrastructure.DataAccess;

namespace Lanyard.Application.Services.FlowActions;

public sealed class MusicFlowActionHandler(
    MusicPlayerService musicPlayerService,
    IDbContextFactory<ApplicationDbContext> dbContextFactory) : IFlowActionHandler
{
    private const string SetPlaylistTemplateKey = "music.set-playlist";
    private const string PlayTemplateKey = "music.play";
    private const string LoadSongTemplateKey = "music.load-song";
    private const string PauseTemplateKey = "music.pause";
    private const string StopTemplateKey = "music.stop";

    private readonly MusicPlayerService _musicPlayerService = musicPlayerService;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory = dbContextFactory;

    public bool CanHandle(string templateKey)
    {
        return string.Equals(templateKey, SetPlaylistTemplateKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(templateKey, PlayTemplateKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(templateKey, LoadSongTemplateKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(templateKey, PauseTemplateKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(templateKey, StopTemplateKey, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Result<bool>> ExecuteAsync(ProjectionProgramStep step, FlowActionExecutionContext context, CancellationToken ct)
    {
        Guid? targetClientId = ResolveTargetClientId(step, context);
        if (targetClientId is null)
        {
            return Result<bool>.Fail("No target client was provided in context or step parameters.");
        }

        if (MatchesTemplate(step, SetPlaylistTemplateKey))
        {
            return await ExecuteSetPlaylistAsync(step, targetClientId.Value, ct);
        }

        if (MatchesTemplate(step, PlayTemplateKey))
        {
            await _musicPlayerService.Play(targetClientId.Value);
            return Result<bool>.Ok(true);
        }

        if (MatchesTemplate(step, LoadSongTemplateKey))
        {
            return await ExecuteLoadSongAsync(step, targetClientId.Value, ct);
        }

        if (MatchesTemplate(step, PauseTemplateKey))
        {
            await _musicPlayerService.Pause(targetClientId.Value);
            return Result<bool>.Ok(true);
        }

        if (MatchesTemplate(step, StopTemplateKey))
        {
            await _musicPlayerService.Stop(targetClientId.Value);
            return Result<bool>.Ok(true);
        }

        return Result<bool>.Fail($"Unsupported music action template '{step.Template?.Name}'.");
    }

    private async Task<Result<bool>> ExecuteSetPlaylistAsync(ProjectionProgramStep step, Guid clientId, CancellationToken ct)
    {
        Guid? playlistId = ReadGuidParameter(step, "playlistId");
        if (playlistId is null)
        {
            return Result<bool>.Fail("'playlistId' is required for music.set-playlist.");
        }

        await using ApplicationDbContext db = await _dbContextFactory.CreateDbContextAsync(ct);

        Playlist? playlist = await db.Playlists
            .AsNoTracking()
            .Where(x => x.Id == playlistId.Value)
            .FirstOrDefaultAsync(ct);

        if (playlist is null)
        {
            return Result<bool>.Fail($"Playlist '{playlistId}' was not found.");
        }

        await _musicPlayerService.LoadPlaylist(clientId, playlist);
        return Result<bool>.Ok(true);
    }

    private async Task<Result<bool>> ExecuteLoadSongAsync(ProjectionProgramStep step, Guid clientId, CancellationToken ct)
    {
        Guid? songId = ReadGuidParameter(step, "songId");
        if (songId is null)
        {
            return Result<bool>.Fail("'songId' is required for music.load-song.");
        }

        Guid? playlistId = ReadGuidParameter(step, "playlistId");

        await using ApplicationDbContext db = await _dbContextFactory.CreateDbContextAsync(ct);

        Song? song = await db.Songs
            .AsNoTracking()
            .Where(x => x.Id == songId.Value)
            .FirstOrDefaultAsync(ct);

        if (song is null)
        {
            return Result<bool>.Fail($"Song '{songId}' was not found.");
        }

        Playlist? playlist = null;
        if (playlistId.HasValue)
        {
            playlist = await db.Playlists
                .AsNoTracking()
                .Where(x => x.Id == playlistId.Value)
                .FirstOrDefaultAsync(ct);

            if (playlist is null)
            {
                return Result<bool>.Fail($"Playlist '{playlistId}' was not found.");
            }
        }

        await _musicPlayerService.Play(clientId, song, playlist);
        return Result<bool>.Ok(true);
    }

    private static bool MatchesTemplate(ProjectionProgramStep step, string expected)
    {
        return string.Equals(step.Template?.Name, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static Guid? ResolveTargetClientId(ProjectionProgramStep step, FlowActionExecutionContext context)
    {
        Guid? overrideClientId = ReadGuidParameter(step, "targetClientId") ?? ReadGuidParameter(step, "clientId");
        if (overrideClientId.HasValue)
        {
            return overrideClientId.Value;
        }

        if (context.TriggerClientId.HasValue)
        {
            return context.TriggerClientId.Value;
        }

        if (context.TryGetValue("triggerClientId", out string? contextClientIdValue)
            && Guid.TryParse(contextClientIdValue, out Guid contextClientId))
        {
            return contextClientId;
        }

        return null;
    }

    private static Guid? ReadGuidParameter(ProjectionProgramStep step, string parameterName)
    {
        ProjectionProgramParameterValue? parameterValue = step.ParameterValues
            .FirstOrDefault(x => string.Equals(x.Parameter?.Name, parameterName, StringComparison.OrdinalIgnoreCase));

        if (Guid.TryParse(parameterValue?.Value, out Guid value))
        {
            return value;
        }

        return null;
    }
}
