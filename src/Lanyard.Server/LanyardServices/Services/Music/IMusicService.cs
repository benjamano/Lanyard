using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Application.Services;

public interface IMusicService
{
    Task<Result<IEnumerable<Song>>> GetSongsAsync();
    Task<Result<Song>> GetSongAsync(Guid songId);

    /// <summary>
    /// A page of the library's active songs by name, optionally only those whose name or album
    /// contains <paramref name="search"/>. For a virtualised list: ask for the rows on screen.
    /// </summary>
    Task<Result<PagedResult<LibrarySong>>> SearchSongsAsync(string? search, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// The company's kiosks for the Music page, connected ones first, then by name. Just what the
    /// page shows - not the capability checks the Clients page needs.
    /// </summary>
    Task<Result<IReadOnlyList<MusicKiosk>>> GetKiosksAsync();

    /// <summary>Names for a set of song ids in one query (missing ids are simply absent).</summary>
    Task<Result<IReadOnlyDictionary<Guid, string>>> GetSongNamesAsync(IReadOnlyCollection<Guid> songIds);
}
