namespace Lanyard.Infrastructure.Models
{
    public class SongPlaylistSelection
    {
        public required Song Song { get; set; }
        public required Playlist Playlist { get; set; }
    }
}
