using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services;

public class MusicService(IDbContextFactory<ApplicationDbContext> factory) : IMusicService
{
    public const int MaxPageSize = 200;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    // Every environment reads the library from the database. Development used to scan the
    // machine's Music folder instead, but those songs got new ids on every call, so they could
    // never be played on a kiosk or kept in a playlist. Upload songs in the File Manager.
    public async Task<Result<IEnumerable<Song>>> GetSongsAsync()
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            IEnumerable<Song> songs = await context.Songs
                .AsNoTracking()
                .TagWithCallSite()
                .Where(song => song.IsActive)
                .OrderBy(song => song.AlbumName)
                .ThenBy(song => song.Name)
                .ToListAsync();

            return Result<IEnumerable<Song>>.Ok(songs);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<Song>>.Fail($"An error occurred while retrieving songs: {ex.Message}");
        }
    }

    public async Task<Result<PagedResult<LibrarySong>>> SearchSongsAsync(string? search, int skip, int take, CancellationToken cancellationToken = default)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync(cancellationToken);

            IQueryable<Song> query = context.Songs
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive);

            if (!string.IsNullOrWhiteSpace(search))
            {
                string term = search.Trim().ToLower();
                query = query.Where(x => x.Name.ToLower().Contains(term) || x.AlbumName.ToLower().Contains(term));
            }

            int total = await query.CountAsync(cancellationToken);

            List<LibrarySong> items = await query
                .OrderBy(x => x.Name)
                .ThenBy(x => x.Id)
                .Skip(Math.Max(0, skip))
                .Take(Math.Clamp(take, 1, MaxPageSize))
                .Select(x => new LibrarySong(x.Id, x.Name, x.AlbumName, x.DurationSeconds, x.CreateDate))
                .ToListAsync(cancellationToken);

            return Result<PagedResult<LibrarySong>>.Ok(new PagedResult<LibrarySong>(items, total));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<PagedResult<LibrarySong>>.Fail($"An error occurred while searching songs: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<MusicKiosk>>> GetKiosksAsync()
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            var rows = await context.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Select(x => new { x.Id, x.Name, x.MostRecentConnectionId })
                .ToListAsync();

            List<MusicKiosk> kiosks = rows
                .Select(x => new MusicKiosk(x.Id, x.Name, IsConnected(x.MostRecentConnectionId)))
                .OrderByDescending(x => x.IsConnected)
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return Result<IReadOnlyList<MusicKiosk>>.Ok(kiosks);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<MusicKiosk>>.Fail($"An error occurred while loading kiosks: {ex.Message}");
        }
    }

    // SignalRControlHub.ConnectedIds is process-static, so tests override this.
    protected virtual bool IsConnected(string? connectionId) =>
        !string.IsNullOrEmpty(connectionId) && Lanyard.Application.SignalR.SignalRControlHub.ConnectedIds.Contains(connectionId);

    public async Task<Result<IReadOnlyDictionary<Guid, string>>> GetSongNamesAsync(IReadOnlyCollection<Guid> songIds)
    {
        try
        {
            if (songIds.Count == 0)
            {
                return Result<IReadOnlyDictionary<Guid, string>>.Ok(new Dictionary<Guid, string>());
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            Dictionary<Guid, string> names = await context.Songs
                .AsNoTracking()
                .TagWithCallSite()
                .Where(s => songIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Name })
                .ToDictionaryAsync(s => s.Id, s => s.Name);

            return Result<IReadOnlyDictionary<Guid, string>>.Ok(names);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyDictionary<Guid, string>>.Fail($"An error occurred while retrieving song names: {ex.Message}");
        }
    }

    public async Task<Result<Song>> GetSongAsync(Guid songId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            Song? song = await context.Songs.AsNoTracking().TagWithCallSite().FirstOrDefaultAsync(s => s.Id == songId);

            if (song == null)
            {
                return Result<Song>.Fail("Song not found.");
            }

            return Result<Song>.Ok(song);
        }
        catch (Exception ex)
        {
            return Result<Song>.Fail($"An error occurred while retrieving the song: {ex.Message}");
        }
    }
}
