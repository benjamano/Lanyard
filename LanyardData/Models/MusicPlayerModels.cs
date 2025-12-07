using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;


namespace LanyardData.Models
{
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