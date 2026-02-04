using System.ComponentModel.DataAnnotations;

namespace Lanyard.Infrastructure.Models
{
    public class FileMetadata
    {
        public Guid Id { get; set; }

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

        public TimeSpan? VideoLength { get; set; }

        public bool IsActive { get; set; } = true;
    }

    public class Folder
    {
        public Guid Id { get; set; }

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

        public bool IsActive { get; set; } = true;
    }
}
