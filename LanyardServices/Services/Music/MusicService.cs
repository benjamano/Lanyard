using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services;

public class MusicService(IDbContextFactory<ApplicationDbContext> _factory) : IMusicService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = _factory;

    public async Task<Result<IEnumerable<Song>>> GetSongsAsync()
    {
        try
        {
            ApplicationDbContext context = await _factory.CreateDbContextAsync();

            IEnumerable<Song> songs = await context.Songs
                .Where(x=> x.IsActive)
                .ToListAsync();

            return Result<IEnumerable<Song>>.Ok(songs);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<Song>>.Fail($"An error occurred while retrieving songs: {ex.Message}");
        }
    }
}
