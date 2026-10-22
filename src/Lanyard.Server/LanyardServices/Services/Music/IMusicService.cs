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

    /// <summary>Names for a set of song ids in one query (missing ids are simply absent).</summary>
    // companyId narrows the lookup explicitly, for callers whose context isn't tenant-filtered.
    Task<Result<IReadOnlyDictionary<Guid, string>>> GetSongNamesAsync(IReadOnlyCollection<Guid> songIds, int? companyId = null);
}
