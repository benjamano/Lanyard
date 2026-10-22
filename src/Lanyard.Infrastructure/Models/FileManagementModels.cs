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

        public TimeSpan? VideoLength { get; set; }

        public bool IsActive { get; set; } = true;

        // A file that belongs to a feature which decides for itself who may read it (a staff
        // member's HR document). The File Manager list/download/rename/move/delete paths treat it
        // as not found: api/files/download lets in every signed-in user and every kiosk in the company.
        public bool IsRestricted { get; set; }
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

        public bool IsActive { get; set; } = true;
    }
}
