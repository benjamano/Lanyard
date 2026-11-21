using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Lanyard.Application.Services;

public class PlaylistService(
    IDbContextFactory<ApplicationDbContext> factory,
    IMusicLibraryEvents libraryEvents,
    ISecurityService securityService,
    ILogger<PlaylistService> logger) : IPlaylistService
{
    public const int NameMaxLength = 100;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IMusicLibraryEvents _libraryEvents = libraryEvents;
    private readonly ILogger<PlaylistService> _logger = logger;
    private readonly ISecurityService _securityService = securityService;

    // Every change to a playlist is checked here as well as hidden in the UI, so a hand-made
    // request from someone who may only play music can't edit playlists.
    private async Task<string?> CheckCanManageAsync()
    {
        foreach (string role in MusicRoles.ManagePlaylists.Split(','))
        {
            if (await _securityService.IsCurrentUserInRoleAsync(role))
            {
                return null;
            }
        }

        _logger.LogWarning("Refused a playlist change by someone without {Roles}", MusicRoles.ManagePlaylists);
        return "You don't have permission to change playlists.";
    }

    public async Task<Result<IEnumerable<Playlist>>> GetActivePlaylistSummariesAsync()
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            IEnumerable<Playlist> playlists = await context.Playlists
                .AsNoTracking()
                .TagWithCallSite()
                .Where(playlist => playlist.DeleteDate == null)
                .OrderBy(playlist => playlist.Name)
                .ToListAsync();

            return Result<IEnumerable<Playlist>>.Ok(playlists);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<Playlist>>.Fail($"An error occurred while retrieving active playlists: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<PlaylistOverview>>> GetPlaylistOverviewsAsync()
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<PlaylistOverview> playlists = await context.Playlists
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.DeleteDate == null)
                .OrderBy(x => x.Name)
                .Select(x => new PlaylistOverview(
                    x.Id,
                    x.Name,
                    x.Description,
                    x.Members!.Count(m => m.DeleteDate == null && m.Song!.IsActive),
                    x.Members!.Where(m => m.DeleteDate == null && m.Song!.IsActive).Sum(m => m.Song!.DurationSeconds)))
                .ToListAsync();

            return Result<IReadOnlyList<PlaylistOverview>>.Ok(playlists);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<PlaylistOverview>>.Fail($"An error occurred while retrieving playlists: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<PlaylistTrack>>> GetPlaylistTracksAsync(Guid playlistId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<PlaylistTrack> tracks = await context.PlaylistSongMembers
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.PlaylistId == playlistId && x.DeleteDate == null && x.Song!.IsActive)
                .OrderBy(x => x.Position)
                .ThenByDescending(x => x.Song!.CreateDate)
                .Select(x => new PlaylistTrack(x.SongId, x.Song!.Name, x.Song.AlbumName, x.Song.DurationSeconds, x.CreateDate, x.Position))
                .ToListAsync();

            return Result<IReadOnlyList<PlaylistTrack>>.Ok(tracks);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<PlaylistTrack>>.Fail($"An error occurred while retrieving the playlist's songs: {ex.Message}");
        }
    }

    public async Task<Result<IEnumerable<PlaylistSongMember>>> GetPlaylistMembersAsync(Guid playlistId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            IEnumerable<PlaylistSongMember> members = await context.PlaylistSongMembers
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Song)
                .Include(x => x.Playlist)
                .Where(x => x.PlaylistId == playlistId && x.DeleteDate == null && x.Song!.IsActive)
                .OrderBy(x => x.Position)
                .ThenByDescending(x => x.Song!.CreateDate)
                .ToListAsync();

            return Result<IEnumerable<PlaylistSongMember>>.Ok(members);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<PlaylistSongMember>>.Fail($"An error occurred while retrieving the playlist members: {ex.Message}");
        }
    }

    public async Task<Result<Playlist>> CreatePlaylistAsync(string name, string? description)
    {
        try
        {
            if (await CheckCanManageAsync() is string denied)
            {
                return Result<Playlist>.Fail(denied);
            }

            if (ValidateName(name) is string error)
            {
                return Result<Playlist>.Fail(error);
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            Playlist playlist = new()
            {
                Name = name.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                CreateDate = DateTime.UtcNow
            };

            context.Playlists.Add(playlist);
            await context.SaveChangesAsync();

            _logger.LogInformation("Created playlist {PlaylistId} {Name}", playlist.Id, playlist.Name);
            _libraryEvents.Raise(playlist.CompanyId, null);

            return Result<Playlist>.Ok(playlist);
        }
        catch (Exception ex)
        {
            return Result<Playlist>.Fail($"An error occurred while creating the playlist: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RenamePlaylistAsync(Guid playlistId, string name, string? description)
    {
        try
        {
            if (await CheckCanManageAsync() is string denied)
            {
                return Result<bool>.Fail(denied);
            }

            if (ValidateName(name) is string error)
            {
                return Result<bool>.Fail(error);
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            Playlist? playlist = await context.Playlists
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == playlistId && x.DeleteDate == null);

            if (playlist is null)
            {
                return Result<bool>.Fail("That playlist no longer exists.");
            }

            playlist.Name = name.Trim();
            playlist.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            await context.SaveChangesAsync();

            _libraryEvents.Raise(playlist.CompanyId, null);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"An error occurred while renaming the playlist: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeletePlaylistAsync(Guid playlistId)
    {
        try
        {
            if (await CheckCanManageAsync() is string denied)
            {
                return Result<bool>.Fail(denied);
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            Playlist? playlist = await context.Playlists
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == playlistId && x.DeleteDate == null);

            if (playlist is null)
            {
                return Result<bool>.Fail("That playlist no longer exists.");
            }

            List<string> usedBy = await FindUsesAsync(context, playlistId);
            if (usedBy.Count > 0)
            {
                return Result<bool>.Fail($"\"{playlist.Name}\" can't be deleted while it's used by {string.Join(", ", usedBy)}. Choose another playlist there first.");
            }

            playlist.DeleteDate = DateTime.UtcNow;
            await context.SaveChangesAsync();

            _logger.LogInformation("Deleted playlist {PlaylistId} {Name}", playlist.Id, playlist.Name);
            _libraryEvents.Raise(playlist.CompanyId, null);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"An error occurred while deleting the playlist: {ex.Message}");
        }
    }

    // What would be left playing a playlist that's gone: parts of the day in a music schedule, and
    // music automation rules. Described the way the screens that edit them name them.
    private static async Task<List<string>> FindUsesAsync(ApplicationDbContext context, Guid playlistId)
    {
        List<string> uses = await context.MusicDayparts
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.PlaylistId == playlistId && x.IsActive)
            .Select(x => "the music schedule's \"" + x.Name + "\"")
            .Distinct()
            .ToListAsync();

        var musicActions = await context.AutomationRuleActions
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsActive && x.ActionType == AutomationActionTypes.MusicControl && x.AutomationRule!.IsActive)
            .Select(x => new { x.ParametersJson, RuleName = x.AutomationRule!.Name })
            .ToListAsync();

        uses.AddRange(musicActions
            .Where(x => ReadPlaylistId(x.ParametersJson) == playlistId)
            .Select(x => $"the automation rule \"{x.RuleName}\"")
            .Distinct());

        return uses;
    }

    private static Guid? ReadPlaylistId(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(parametersJson);

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "PlaylistId", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(property.Value.GetString(), out Guid id))
                {
                    return id;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    public async Task<Result<bool>> AddSongToPlaylistAsync(Guid songId, Guid playlistId)
    {
        try
        {
            if (await CheckCanManageAsync() is string denied)
            {
                return Result<bool>.Fail(denied);
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            Playlist? playlist = await context.Playlists
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == playlistId && x.DeleteDate == null);

            if (playlist is null)
            {
                return Result<bool>.Fail("That playlist no longer exists.");
            }

            bool songExists = await context.Songs
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(x => x.Id == songId && x.IsActive);

            if (!songExists)
            {
                return Result<bool>.Fail("That song is no longer in the library.");
            }

            // One row per song and playlist, ever (it's the key), so a removed song is restored
            // rather than added again.
            PlaylistSongMember? member = await context.PlaylistSongMembers
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.SongId == songId && x.PlaylistId == playlistId);

            if (member is { DeleteDate: null })
            {
                return Result<bool>.Ok(false);
            }

            int nextPosition = await context.PlaylistSongMembers
                .Where(x => x.PlaylistId == playlistId && x.DeleteDate == null)
                .Select(x => (int?)x.Position)
                .MaxAsync() is int max ? max + 1 : 0;

            if (member is null)
            {
                context.PlaylistSongMembers.Add(new PlaylistSongMember
                {
                    SongId = songId,
                    PlaylistId = playlistId,
                    CreateDate = DateTime.UtcNow,
                    Position = nextPosition
                });
            }
            else
            {
                member.DeleteDate = null;
                member.DeleteByUserId = null;
                member.CreateDate = DateTime.UtcNow;
                member.Position = nextPosition;
            }

            await context.SaveChangesAsync();

            _libraryEvents.Raise(playlist.CompanyId, playlistId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"An error occurred while adding the song to the playlist: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RemoveSongFromPlaylistAsync(Guid playlistId, Guid songId)
    {
        try
        {
            if (await CheckCanManageAsync() is string denied)
            {
                return Result<bool>.Fail(denied);
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            PlaylistSongMember? member = await context.PlaylistSongMembers
                .TagWithCallSite()
                .Include(x => x.Playlist)
                .Where(x => x.PlaylistId == playlistId && x.SongId == songId && x.DeleteDate == null)
                .FirstOrDefaultAsync();

            if (member is null)
            {
                return Result<bool>.Fail("Song is not in this playlist.");
            }

            member.DeleteDate = DateTime.UtcNow;

            await context.SaveChangesAsync();

            _libraryEvents.Raise(member.Playlist!.CompanyId, playlistId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"An error occurred while removing the song from the playlist: {ex.Message}");
        }
    }

    public async Task<Result<bool>> MoveSongAsync(Guid playlistId, Guid songId, int newIndex)
    {
        try
        {
            if (await CheckCanManageAsync() is string denied)
            {
                return Result<bool>.Fail(denied);
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<PlaylistSongMember> members = await context.PlaylistSongMembers
                .TagWithCallSite()
                .Include(x => x.Playlist)
                .Where(x => x.PlaylistId == playlistId && x.DeleteDate == null && x.Song!.IsActive)
                .OrderBy(x => x.Position)
                .ThenByDescending(x => x.Song!.CreateDate)
                .ToListAsync();

            PlaylistSongMember? moving = members.FirstOrDefault(x => x.SongId == songId);
            if (moving is null)
            {
                return Result<bool>.Fail("Song is not in this playlist.");
            }

            members.Remove(moving);
            members.Insert(Math.Clamp(newIndex, 0, members.Count), moving);

            for (int i = 0; i < members.Count; i++)
            {
                members[i].Position = i;
            }

            await context.SaveChangesAsync();

            _libraryEvents.Raise(moving.Playlist!.CompanyId, playlistId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"An error occurred while moving the song: {ex.Message}");
        }
    }

    private static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "A playlist name is required.";
        }

        return name.Trim().Length > NameMaxLength ? $"A playlist name can be at most {NameMaxLength} characters." : null;
    }
}
