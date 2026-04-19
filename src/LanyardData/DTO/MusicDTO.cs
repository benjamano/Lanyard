using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Infrastructure.DTO
{
    public class SongDTO
    {
        public Guid Id { get; set; }

        public required string Name { get; set; }
        public required string AlbumName { get; set; }

        public required string FilePath { get; set; }

        public double DurationSeconds { get; set; }
    }
}
