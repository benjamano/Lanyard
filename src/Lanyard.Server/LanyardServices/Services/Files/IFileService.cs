using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DTO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;

namespace Lanyard.Application.Services;

public interface IFileService
{
    Task<Result<FileMetadata>> UploadFileAsync(IFormFile file, Guid? folderId, string uploadedBy, CancellationToken cancellationToken);
    Task<Result<FileMetadata>> UploadFileAsync(IFormFile file, Guid? folderId, CancellationToken cancellationToken);
    Task<Result<FileMetadata>> RenameFileAsync(Guid fileId, string newName, CancellationToken cancellationToken);
    Task<Result<bool>> DeleteFileAsync(Guid fileId, CancellationToken cancellationToken);
    Task<Result<FileMetadata>> GetFileMetadataAsync(Guid fileId, CancellationToken cancellationToken);
    Task<Result<IReadOnlyList<FileMetadata>>> ListFilesAsync(Guid? folderId, CancellationToken cancellationToken);
    Task<Result<Folder>> CreateFolderAsync(string name, Guid? parentFolderId, string createdBy, CancellationToken cancellationToken);
    Task<Result<Folder>> RenameFolderAsync(Guid folderId, string newName, CancellationToken cancellationToken);
    Task<Result<bool>> DeleteFolderAsync(Guid folderId, CancellationToken cancellationToken);
    Task<Result<IReadOnlyList<Folder>>> ListFoldersAsync(Guid? parentFolderId, CancellationToken cancellationToken);
    Task<Result<Stream>> DownloadFileAsync(Guid fileId, CancellationToken cancellationToken);
}
