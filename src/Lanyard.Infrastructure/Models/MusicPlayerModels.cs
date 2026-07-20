using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;


namespace Lanyard.Infrastructure.Models
{
    /// <summary>
    /// How playback behaves when it runs off the end of a track or the queue.
    /// </summary>
    public enum RepeatMode
    {
        /// <summary>Play the queue through once and stop at the end.</summary>
        Off = 0,

        /// <summary>Loop the queue: the track after the last one is the first one.</summary>
        All = 1,

        /// <summary>Replay the current track each time it ends. Skipping still moves on.</summary>
        One = 2
    }

    /// <summary>
    /// Outcome of server-side BPM analysis for a song. Terminal states
    /// (everything except <see cref="NotAnalyzed"/>) are never retried automatically.
    /// </summary>
    public enum BpmAnalysisStatus
    {
        /// <summary>Not yet analyzed — picked up by the startup backfill.</summary>
        NotAnalyzed = 0,

        /// <summary>Full onset analysis succeeded: Bpm and FirstBeatOffsetSeconds are set.</summary>
        Analyzed = 1,

        /// <summary>BPM read from the file's TBPM tag but audio was not decodable — rate is known, beat phase is not.</summary>
        TagOnly = 2,

        /// <summary>Decode or analysis failed.</summary>
        Failed = 3,

        /// <summary>Format not decodable on this platform and no TBPM tag present.</summary>
        Unsupported = 4
    }

    public class Song
    {
        public Guid Id { get; set; }

        public required string Name { get; set; }
        public required string AlbumName { get; set; }

        public required string FilePath { get; set; }

        public double DurationSeconds { get; set; }

        public DateTime CreateDate { get; set; }
        public bool IsDownloaded { get; set; }
        public bool IsActive { get; set; }

        public double? Bpm { get; set; }

        // Seconds from the start of the track to the first beat, reduced into [0, beatLength).
        // Null when only the tag BPM is known (TagOnly) — sync then matches rate but not phase.
        public double? FirstBeatOffsetSeconds { get; set; }

        public BpmAnalysisStatus BpmAnalysisStatus { get; set; }
        public DateTime? BpmAnalysisDate { get; set; }

        // Set when the song originates from a file upload (null for songs discovered by the
        // local dev music-folder scan). Links back to the stored file in the bucket / on disk.
        public Guid? FileMetadataId { get; set; }
        public FileMetadata? FileMetadata { get; set; }
    }

    public class Playlist
    {
        public Guid Id { get; set; }

        public required string Name { get; set; }
        public string? Description { get; set; }
        
        public UserProfile? CreateByUser { get; set; }
        public string? CreateByUserId { get; set; }
        public DateTime CreateDate { get; set; }
        
        public UserProfile? DeleteByUser { get; set; }
        public string? DeleteByUserId { get; set; }
        public DateTime? DeleteDate { get; set; }

        public ICollection<PlaylistSongMember>? Members { get; set; }
    }

    [PrimaryKey(nameof(SongId), nameof(PlaylistId))]
    public class PlaylistSongMember
    {
        public Guid SongId { get; set; }
        public Song? Song { get; set; }

        public Guid PlaylistId { get; set; }
        public Playlist? Playlist { get; set; }

        public UserProfile? CreateByUser { get; set; }
        public string? CreateByUserId { get; set; }
        public DateTime CreateDate { get; set; }

        public UserProfile? DeleteByUser { get; set; }
        public string? DeleteByUserId { get; set; }
        public DateTime? DeleteDate { get; set; }
    }
}