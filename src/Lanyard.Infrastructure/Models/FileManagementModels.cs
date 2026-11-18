using System.ComponentModel.DataAnnotations;

namespace Lanyard.Infrastructure.Models
{
    public class FileMetadata : ICompanyOwned
    {
        public Guid Id { get; set; }
        public int CompanyId { get; set; }

        [Required]
        [MaxLength(255)]
        public required string FileName { get; set; }

        [Required]
        [MaxLength(500)]
        public required string FilePath { get; set; }

        public long FileSize { get; set; }

        [MaxLength(100)]
        public string? ContentType { get; set; }

        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string? UploadedBy { get; set; }

        public Guid? FolderId { get; set; }
        public Folder? Folder { get; set; }

        // Length of a video or audio file, as the browser measured it while uploading.
        public TimeSpan? VideoLength { get; set; }

        // An image's or video's size in pixels, when known.
        public int? Width { get; set; }

        public int? Height { get; set; }

        // Storage key of the small WebP shown in Files' grid (FileThumbnailService). Null = not made
        // yet; empty = tried and there isn't one (an SVG, a format SkiaSharp can't read).
        [MaxLength(500)]
        public string? ThumbnailPath { get; set; }

        public bool IsActive { get; set; } = true;

        // A file that belongs to a feature which decides for itself who may read it (a staff
        // member's HR document). The File Manager list/download/rename/move/delete paths treat it
        // as not found: api/files/download lets in every signed-in user and every kiosk in the company.
        public bool IsRestricted { get; set; }

        // Owned by a feature that manages it on its own page (an audio clip's sound). Unlike
        // IsRestricted it can still be served by id, but it never appears when browsing Files,
        // in search or in a file picker.
        public bool IsSystemFile { get; set; }

        // IsActive = false means the file is in the Recycle bin. DeletedAt is when it went there,
        // which the purge sweep uses to remove it for good after FileLibraryRules.BinRetention.
        // A binned file can still be served by id, so anything that uses it keeps working until
        // it is deleted for good (and that is refused while something still uses it).
        public DateTime? DeletedAt { get; set; }

        [MaxLength(450)]
        public string? DeletedByUserId { get; set; }
    }

    public class Folder : ICompanyOwned
    {
        public Guid Id { get; set; }
        public int CompanyId { get; set; }

        [Required]
        [MaxLength(255)]
        public required string Name { get; set; }

        public Guid? ParentFolderId { get; set; }
        public Folder? ParentFolder { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string? CreatedBy { get; set; }

        public virtual ICollection<Folder> SubFolders { get; set; } = [];
        public virtual ICollection<FileMetadata> Files { get; set; } = [];

        // False while the folder is in the Recycle bin. Its contents keep their own IsActive:
        // they are hidden because an ancestor is binned, come back when it is restored, and are
        // deleted for good with it.
        public bool IsActive { get; set; } = true;

        public DateTime? DeletedAt { get; set; }

        [MaxLength(450)]
        public string? DeletedByUserId { get; set; }

        // Every signed-in staff member can browse and download this folder and everything under
        // it, read-only. Inherited: a subfolder of a shared folder is shared too.
        public bool IsSharedWithStaff { get; set; }
    }
}
