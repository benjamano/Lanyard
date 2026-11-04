namespace Lanyard.Infrastructure.DTO;

/// <summary>A playlist as a list shows it: no members, just how many and how long.</summary>
public sealed record PlaylistOverview(Guid Id, string Name, string? Description, int SongCount, double TotalSeconds);

/// <summary>One song in a playlist, in playlist order.</summary>
public sealed record PlaylistTrack(Guid SongId, string Name, string AlbumName, double DurationSeconds, DateTime AddedUtc, int Position);

/// <summary>One song in the company's library.</summary>
public sealed record LibrarySong(Guid Id, string Name, string AlbumName, double DurationSeconds, DateTime CreateDate);

/// <summary>One page of a longer list, with how many there are in all.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);
