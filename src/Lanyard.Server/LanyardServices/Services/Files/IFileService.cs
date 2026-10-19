using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DTO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;

namespace Lanyard.Application.Services;

// IsRestricted: the file belongs to a feature that decides who may read it (a policy PDF). The
// File Manager paths below treat it as not found; only the *Restricted* methods reach it, and only
// the owning feature's services call those.
public sealed record FileUploadOptions(bool AddAudioToMusicLibrary = true, bool IsRestricted = false);

public interface IFileService
{
    Task<Result<FileMetadata>> UploadFileAsync(IFormFile file, Guid? folderId, string uploadedBy, CancellationToken cancellationToken);

    /// <summary>
    /// As above, but lets the caller turn off the music-library side effect: an audio file is
    /// normally also added as a Song, which an audio clip mustn't be.
    /// </summary>
    Task<Result<FileMetadata>> UploadFileAsync(IFormFile file, Guid? folderId, string uploadedBy, FileUploadOptions options, CancellationToken cancellationToken);
    Task<Result<FileMetadata>> UploadFileAsync(IFormFile file, Guid? folderId, CancellationToken cancellationToken);
    Task<Result<FileMetadata>> RenameFileAsync(Guid fileId, string newName, CancellationToken cancellationToken);
    Task<Result<bool>> DeleteFileAsync(Guid fileId, CancellationToken cancellationToken);
    Task<Result<FileMetadata>> GetFileMetadataAsync(Guid fileId, CancellationToken cancellationToken);
    Task<Result<IReadOnlyList<FileMetadata>>> ListFilesAsync(Guid? folderId, CancellationToken cancellationToken);
    Task<Result<Folder>> CreateFolderAsync(string name, Guid? parentFolderId, string createdBy, CancellationToken cancellationToken);
    Task<Result<Folder>> CreateFolderAsync(string name, Guid? parentFolderId, CancellationToken cancellationToken);
    Task<Result<Folder>> RenameFolderAsync(Guid folderId, string newName, CancellationToken cancellationToken);
    Task<Result<bool>> DeleteFolderAsync(Guid folderId, CancellationToken cancellationToken);
    Task<Result<IReadOnlyList<Folder>>> ListFoldersAsync(Guid? parentFolderId, CancellationToken cancellationToken);
    Task<Result<Folder>> GetFolderAsync(Guid folderId, CancellationToken cancellationToken);
    Task<Result<Stream>> DownloadFileAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a file for streaming to an HTTP response in one metadata lookup. In production the
    /// optional byte range is pushed down to object storage so a seek in a video or song fetches
    /// only the bytes asked for instead of the whole object; locally the returned stream is
    /// seekable and the HTTP layer applies the range itself. Fails with
    /// <see cref="FileService.RangeNotSatisfiableError"/> when the range lies outside the file.
    /// </summary>
    Task<Result<FileContent>> OpenFileContentAsync(Guid fileId, FileByteRange? range, CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="OpenFileContentAsync"/>, but only for restricted files. No access check of its
    /// own: the caller (e.g. PolicyService) must already have decided the reader may see it.
    /// </summary>
    Task<Result<FileContent>> OpenRestrictedFileContentAsync(Guid fileId, FileByteRange? range, CancellationToken cancellationToken);

    /// <summary>Removes a restricted file, e.g. when the record it was uploaded for couldn't be saved.</summary>
    Task<Result<bool>> DeleteRestrictedFileAsync(Guid fileId, CancellationToken cancellationToken);
    Task<Result<FileMetadata>> MoveFileAsync(Guid fileId, Guid? destinationFolderId, CancellationToken cancellationToken);
}
