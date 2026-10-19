using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.AudioClips;
using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.AudioClips;

public class AudioClipService(
    IDbContextFactory<ApplicationDbContext> factory,
    IFileService fileService,
    IAudioClipPlaybackService playback,
    ISecurityService securityService,
    ILogger<AudioClipService> logger) : IAudioClipService
{
    private const int MaxFadeMs = 10_000;

    public bool CanManage(LocationScope scope, int? locationId) =>
        locationId is int id ? SchedulingAccess.CanManageLocation(scope, id) : scope.IsAdmin;

    public async Task<Result<List<AudioClip>>> ListAsync(LocationScope scope)
    {
        try
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            List<AudioClip> clips = await VisibleClips(ctx, scope)
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                .OrderBy(x => x.Name)
                .ToListAsync();

            return Result<List<AudioClip>>.Ok(clips);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list audio clips");
            return Result<List<AudioClip>>.Fail($"Failed to load the clips: {ex.Message}");
        }
    }

    public async Task<Result<AudioClip>> UploadAsync(LocationScope scope, IFormFile file, AudioClipDetails details, string userId, CancellationToken ct = default)
    {
        try
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync(ct);

            string? error = await CheckDetailsAsync(ctx, scope, details);
            if (error is not null)
            {
                return Result<AudioClip>.Fail(error);
            }

            Result<double> inspected = AudioClipFileCheck.Inspect(file.FileName, file.Length, file.OpenReadStream);
            if (!inspected.IsSuccess)
            {
                return Result<AudioClip>.Fail(inspected.Error!);
            }

            // Not added to the music library: a clip isn't a song and mustn't turn up in playlists.
            Result<FileMetadata> uploaded = await fileService.UploadFileAsync(file, null, userId, new FileUploadOptions(AddAudioToMusicLibrary: false), ct);
            if (!uploaded.IsSuccess || uploaded.Data is null)
            {
                return Result<AudioClip>.Fail(uploaded.Error ?? "Couldn't store the file.");
            }

            return await AddClipAsync(ctx, details, uploaded.Data.Id, inspected.Data, userId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload audio clip {FileName}", file.FileName);
            return Result<AudioClip>.Fail($"Failed to upload the clip: {ex.Message}");
        }
    }

    public async Task<Result<AudioClip>> CreateFromFileAsync(LocationScope scope, Guid fileId, AudioClipDetails details, string userId, CancellationToken ct = default)
    {
        try
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync(ct);

            string? error = await CheckDetailsAsync(ctx, scope, details);
            if (error is not null)
            {
                return Result<AudioClip>.Fail(error);
            }

            FileMetadata? file = await ctx.FileMetadata
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == fileId && x.IsActive, ct);

            if (file is null)
            {
                return Result<AudioClip>.Fail("That file no longer exists.");
            }

            // The file's bytes are checked below, as for an upload; size first, so a huge file is
            // never read in.
            if (file.FileSize > AudioClipFileCheck.MaxBytes)
            {
                return Result<AudioClip>.Fail($"Clips can be at most {AudioClipFileCheck.MaxBytes / (1024 * 1024)} MB.");
            }

            Result<FileContent> content = await fileService.OpenFileContentAsync(file.Id, null, ct);
            if (!content.IsSuccess || content.Data is null)
            {
                return Result<AudioClip>.Fail(content.Error ?? "Couldn't read the file.");
            }

            using MemoryStream copy = new();
            await using (content.Data)
            {
                await content.Data.Stream.CopyToAsync(copy, ct);
            }

            byte[] bytes = copy.ToArray();
            Result<double> inspected = AudioClipFileCheck.Inspect(file.FileName, bytes.LongLength, () => new MemoryStream(bytes, writable: false));
            if (!inspected.IsSuccess)
            {
                return Result<AudioClip>.Fail(inspected.Error!);
            }

            return await AddClipAsync(ctx, details, file.Id, inspected.Data, userId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to make an audio clip from file {FileId}", fileId);
            return Result<AudioClip>.Fail($"Failed to add the clip: {ex.Message}");
        }
    }

    public async Task<Result<AudioClip>> UpdateAsync(LocationScope scope, Guid clipId, AudioClipDetails details)
    {
        try
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            AudioClip? clip = await ctx.AudioClips.FirstOrDefaultAsync(x => x.Id == clipId && x.DeleteDate == null);

            if (clip is null)
            {
                return Result<AudioClip>.Fail("Audio clip not found.");
            }

            // Both where it is now and where it's moving to must be the caller's to manage.
            if (!CanManage(scope, clip.LocationId))
            {
                return Result<AudioClip>.Fail(NotYoursError(clip.LocationId));
            }

            string? error = await CheckDetailsAsync(ctx, scope, details);
            if (error is not null)
            {
                return Result<AudioClip>.Fail(error);
            }

            Apply(clip, details);
            clip.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            logger.LogInformation("Audio clip {ClipId} updated", clip.Id);

            return Result<AudioClip>.Ok(clip);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update audio clip {ClipId}", clipId);
            return Result<AudioClip>.Fail($"Failed to save the clip: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteAsync(LocationScope scope, Guid clipId)
    {
        try
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            AudioClip? clip = await ctx.AudioClips.FirstOrDefaultAsync(x => x.Id == clipId && x.DeleteDate == null);

            if (clip is null)
            {
                return Result<bool>.Fail("Audio clip not found.");
            }

            if (!CanManage(scope, clip.LocationId))
            {
                return Result<bool>.Fail(NotYoursError(clip.LocationId));
            }

            // Soft: the play history still points at it. The file stays in the file manager.
            clip.DeleteDate = DateTime.UtcNow;
            await ctx.SaveChangesAsync();

            logger.LogInformation("Audio clip {ClipId} deleted", clip.Id);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete audio clip {ClipId}", clipId);
            return Result<bool>.Fail($"Failed to delete the clip: {ex.Message}");
        }
    }

    public async Task<Result<List<AudioClipFileOption>>> ListAudioFilesAsync(LocationScope scope)
    {
        try
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            List<FileMetadata> files = await ctx.FileMetadata
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive && x.FileSize <= AudioClipFileCheck.MaxBytes
                    && (x.FileName.ToLower().EndsWith(".mp3") || x.FileName.ToLower().EndsWith(".wav") || x.FileName.ToLower().EndsWith(".m4a")))
                .OrderBy(x => x.FileName)
                .ToListAsync();

            return Result<List<AudioClipFileOption>>.Ok(files
                .Where(x => AudioClipFormats.ExtensionOf(x.FileName) is not null)
                .Select(x => new AudioClipFileOption(x.Id, x.FileName))
                .ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list audio files for clips");
            return Result<List<AudioClipFileOption>>.Fail($"Failed to load the files: {ex.Message}");
        }
    }

    public async Task<Result<List<AudioClipKiosk>>> ListKiosksAsync(LocationScope scope)
    {
        try
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            var kiosks = await KioskQuery(ctx, scope)
                .AsNoTracking()
                .TagWithCallSite()
                .OrderBy(x => x.Location!.Name)
                .ThenBy(x => x.Name)
                .Select(x => new { x.Id, x.Name, x.LocationId, LocationName = x.Location!.Name, x.MostRecentConnectionId, x.ReportedClientVersion })
                .ToListAsync();

            return Result<List<AudioClipKiosk>>.Ok(kiosks
                .Select(x => new AudioClipKiosk(
                    x.Id,
                    x.Name,
                    x.LocationId,
                    x.LocationName,
                    IsOnline(x.MostRecentConnectionId),
                    IsRecentEnough(x.ReportedClientVersion),
                    x.ReportedClientVersion))
                .ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list kiosks for audio clips");
            return Result<List<AudioClipKiosk>>.Fail($"Failed to load the kiosks: {ex.Message}");
        }
    }

    public async Task<Result<List<AudioClipKioskOutcome>>> PlayAsync(LocationScope scope, Guid clipId, IReadOnlyCollection<Guid> clientIds, string userId)
    {
        try
        {
            if (!await CanPlayAsync(scope))
            {
                return Result<List<AudioClipKioskOutcome>>.Fail("You don't have permission to play audio clips.");
            }

            if (scope.CompanyId is not int companyId)
            {
                return Result<List<AudioClipKioskOutcome>>.Fail("Couldn't work out which company you're signed in to.");
            }

            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            bool canSeeClip = await VisibleClips(ctx, scope).AnyAsync(x => x.Id == clipId);
            if (!canSeeClip)
            {
                return Result<List<AudioClipKioskOutcome>>.Fail("Audio clip not found.");
            }

            List<Guid> allowed = await KioskQuery(ctx, scope).Where(x => clientIds.Contains(x.Id)).Select(x => x.Id).ToListAsync();
            if (allowed.Count != clientIds.Distinct().Count())
            {
                return Result<List<AudioClipKioskOutcome>>.Fail("You can only play clips on the kiosks at your own location.");
            }

            return await playback.PlayAsync(new AudioClipPlayRequest(clipId, allowed, companyId, AudioClipPlaySource.Manual, userId));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to play audio clip {ClipId}", clipId);
            return Result<List<AudioClipKioskOutcome>>.Fail($"Failed to play the clip: {ex.Message}");
        }
    }

    public async Task<Result<bool>> StopAsync(LocationScope scope, Guid clientId)
    {
        try
        {
            if (!await CanPlayAsync(scope) || scope.CompanyId is not int companyId)
            {
                return Result<bool>.Fail("You don't have permission to stop audio clips.");
            }

            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            if (!await KioskQuery(ctx, scope).AnyAsync(x => x.Id == clientId))
            {
                return Result<bool>.Fail("You can only stop clips on the kiosks at your own location.");
            }

            return await playback.StopAsync(clientId, companyId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stop the audio clip on kiosk {ClientId}", clientId);
            return Result<bool>.Fail($"Failed to stop the clip: {ex.Message}");
        }
    }

    public async Task<Result<List<AudioClipPlaySummary>>> ListRecentPlaysAsync(LocationScope scope, int take = 20)
    {
        try
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            IQueryable<AudioClipPlay> plays = ctx.AudioClipPlays.AsNoTracking().TagWithCallSite();

            if (!scope.IsAdmin)
            {
                plays = plays.Where(x => x.Client!.LocationId == scope.LocationId);
            }

            var recent = await plays
                .OrderByDescending(x => x.RequestedAtUtc)
                .Take(Math.Clamp(take, 1, 100))
                .Select(x => new
                {
                    x.Id,
                    ClipName = x.AudioClip!.Name,
                    KioskName = x.Client!.Name,
                    x.Source,
                    x.Status,
                    x.Error,
                    FirstName = x.TriggeredByUser == null ? null : x.TriggeredByUser.FirstName,
                    LastName = x.TriggeredByUser == null ? null : x.TriggeredByUser.LastName,
                    x.RequestedAtUtc
                })
                .ToListAsync();

            return Result<List<AudioClipPlaySummary>>.Ok(recent
                .Select(x => new AudioClipPlaySummary(
                    x.Id,
                    x.ClipName,
                    x.KioskName,
                    x.Source,
                    x.Status,
                    x.Error,
                    string.Join(' ', new[] { x.FirstName, x.LastName }.Where(n => !string.IsNullOrWhiteSpace(n))) is { Length: > 0 } name ? name : null,
                    x.RequestedAtUtc))
                .ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list recent audio clip plays");
            return Result<List<AudioClipPlaySummary>>.Fail($"Failed to load recent plays: {ex.Message}");
        }
    }

    // SignalRControlHub.ConnectedIds is process-static, so tests override this.
    protected virtual bool IsOnline(string? connectionId) =>
        !string.IsNullOrEmpty(connectionId) && SignalRControlHub.ConnectedIds.Contains(connectionId);

    private async Task<bool> CanPlayAsync(LocationScope scope) =>
        scope.IsAdmin || scope.IsManager || await securityService.IsCurrentUserInRoleAsync(AudioClipRoles.CanPlayAudioClips);

    // Everyone sees company-wide clips; a clip limited to a location is only seen there (an
    // Admin sees them all).
    private static IQueryable<AudioClip> VisibleClips(ApplicationDbContext ctx, LocationScope scope)
    {
        IQueryable<AudioClip> clips = ctx.AudioClips.Where(x => x.DeleteDate == null);

        return scope.IsAdmin ? clips : clips.Where(x => x.LocationId == null || x.LocationId == scope.LocationId);
    }

    // An Admin can play on any kiosk in the company; anyone else only at their own location.
    private static IQueryable<Client> KioskQuery(ApplicationDbContext ctx, LocationScope scope)
    {
        IQueryable<Client> kiosks = ctx.Clients.Where(x => x.LocationId != null);

        return scope.IsAdmin ? kiosks : kiosks.Where(x => x.LocationId == scope.LocationId);
    }

    private async Task<string?> CheckDetailsAsync(ApplicationDbContext ctx, LocationScope scope, AudioClipDetails details)
    {
        if (!CanManage(scope, details.LocationId))
        {
            return NotYoursError(details.LocationId);
        }

        string name = details.Name?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            return "Give the clip a name.";
        }

        if (name.Length > AudioClip.NameMaxLength)
        {
            return $"The name can be at most {AudioClip.NameMaxLength} characters.";
        }

        if (details.Volume is < 0 or > 100 || details.DuckToPercent is < 0 or > 100)
        {
            return "Volumes go from 0 to 100%.";
        }

        if (details.FadeDownMs is < 0 or > MaxFadeMs || details.FadeUpMs is < 0 or > MaxFadeMs)
        {
            return $"Fades can be at most {MaxFadeMs / 1000} seconds.";
        }

        if (details.LocationId is int locationId && !await ctx.Locations.AnyAsync(x => x.Id == locationId && x.IsActive))
        {
            return "That location doesn't exist.";
        }

        return null;
    }

    private async Task<Result<AudioClip>> AddClipAsync(ApplicationDbContext ctx, AudioClipDetails details, Guid fileId, double durationSeconds, string userId)
    {
        AudioClip clip = new()
        {
            Id = Guid.CreateVersion7(),
            FileMetadataId = fileId,
            DurationSeconds = durationSeconds,
            CreateDate = DateTime.UtcNow,
            CreatedByUserId = userId
        };

        Apply(clip, details);

        ctx.AudioClips.Add(clip);
        await ctx.SaveChangesAsync();

        logger.LogInformation("Audio clip {ClipId} added from file {FileId}", clip.Id, fileId);

        return Result<AudioClip>.Ok(clip);
    }

    private static void Apply(AudioClip clip, AudioClipDetails details)
    {
        clip.Name = details.Name.Trim();
        clip.LocationId = details.LocationId;
        clip.Volume = details.Volume;
        clip.DuckToPercent = details.DuckToPercent;
        clip.FadeDownMs = details.FadeDownMs;
        clip.FadeUpMs = details.FadeUpMs;
    }

    private static string NotYoursError(int? locationId) => locationId is null
        ? "Only an admin can manage clips for every location."
        : "You can only manage clips for your own location.";

    private static bool IsRecentEnough(string? reportedVersion) =>
        ClientVersionFormat.TryNormalize(reportedVersion, out string version)
        && ClientVersionFormat.Compare(version, AudioClipFormats.MinimumClientVersion) >= 0;
}
