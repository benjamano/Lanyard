using Lanyard.Infrastructure.Models;

namespace Lanyard.Infrastructure.DTO.Files;

// What api/files/list sends back. Leaves out FileMetadata.FilePath, which is the server's own
// storage location (a filesystem path in development, the bucket key in production) - nothing a
// browser needs, and a map of the server for anyone who reads it.
public sealed record FileListItemDto(
    Guid Id,
    string FileName,
    long FileSize,
    string? ContentType,
    DateTime UploadedAt,
    string? UploadedBy,
    Guid? FolderId,
    TimeSpan? VideoLength)
{
    public static FileListItemDto From(FileMetadata file) =>
        new(file.Id, file.FileName, file.FileSize, file.ContentType, file.UploadedAt, file.UploadedBy, file.FolderId, file.VideoLength);
}

// What api/files/folders/list sends back: the folder itself, not its loaded child collections.
public sealed record FolderListItemDto(Guid Id, string Name, Guid? ParentFolderId, DateTime CreatedAt, string? CreatedBy)
{
    public static FolderListItemDto From(Folder folder) =>
        new(folder.Id, folder.Name, folder.ParentFolderId, folder.CreatedAt, folder.CreatedBy);
}
