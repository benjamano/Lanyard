using Microsoft.AspNetCore.Identity;
using System;
using System.Collections.Generic;
using System.Text;
using System.ComponentModel.DataAnnotations;


namespace LanyardData.Models
{
    public class Song
    {

        public Guid Id { get; set; }

        public required string Name { get; set; }
        public required string FilePath { get; set; }

        public double DurationSeconds { get; set; }

        public bool IsDownloaded { get; set; }
        public bool IsActive { get; set; }
    }

    public class Playlist
    {
        public Guid Id { get; set; }

        public string Name { get; set; }
        public string Description { get; set; }

        public DateTime CreateDate { get; set; }
        public IdentityUser CreateByUser { get; set; }
        public Guid CreateByUserId { get; set; }

        public DateTime DeleteDate { get; set; }
        public IdentityUser DeleteByUser { get; set; }
        public Guid DeleteByUserId { get; set; }
    }

    public class PlaylistSongMember
    {
        [Key]
        public Guid SongId { get; set; }
        public Song Song { get; set; }

        [Key]
        public Guid PlaylistId { get; set; }
        public Playlist Playlist { get; set; }

        public DateTime CreateDate { get; set; }
        public IdentityUser CreateByUser { get; set; }
        public Guid CreateByUserId { get; set; }

        public DateTime DeleteDate { get; set; }
        public IdentityUser DeleteByUser { get; set; }
        public Guid DeleteByUserId { get; set; }
    }
}