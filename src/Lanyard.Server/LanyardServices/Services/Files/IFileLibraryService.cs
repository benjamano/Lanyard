using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Files;

namespace Lanyard.Application.Services;

/// <summary>
/// What the Files page is built on: browsing and searching the library, the folder tree,
/// sharing folders with staff, moving things about and the Recycle bin. <see cref="IFileService"/>
/// still owns storing, opening and permanently deleting a single file.
///
/// Every method only ever sees the library: restricted files (staff documents) and system files
/// (audio clip sounds) are invisible here, as is anything inside a folder that is in the bin.
/// Methods that take <c>sharedOnly</c> are limited to folders shared with staff, for people who
/// don't manage files.
/// </summary>
public interface IFileLibraryService
{
    Task<Result<IReadOnlyList<FolderNode>>> GetTreeAsync(bool sharedOnly, CancellationToken cancellationToken);

    Task<Result<FileQueryResult>> QueryAsync(FileQuery query, CancellationToken cancellationToken);

    Task<Result<FileListItem>> GetFileAsync(Guid fileId, bool sharedOnly, CancellationToken cancellationToken);

    /// <summary>The folders from the top level down to <paramref name="folderId"/>, for a breadcrumb.</summary>
    Task<Result<IReadOnlyList<FolderNode>>> GetPathAsync(Guid folderId, bool sharedOnly, CancellationToken cancellationToken);

    Task<Result<StorageSummary>> GetStorageSummaryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The files a zip of the selection holds, folders included with everything in them, laid out
    /// as they are in Files. Fails when it would be bigger than <see cref="FileLibraryRules.MaxZipBytes"/>
    /// or hold more than <see cref="FileLibraryRules.MaxZipFiles"/> files.
    /// </summary>
    Task<Result<IReadOnlyList<ZipEntryPlan>>> PlanZipAsync(IReadOnlyCollection<Guid> fileIds, IReadOnlyCollection<Guid> folderIds, bool sharedOnly, CancellationToken cancellationToken);

    Task<Result<BulkOperationResult>> MoveAsync(IReadOnlyCollection<Guid> fileIds, IReadOnlyCollection<Guid> folderIds, Guid? destinationFolderId, CancellationToken cancellationToken);

    Task<Result<FolderNode>> SetFolderSharedAsync(Guid folderId, bool isShared, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<BinItem>>> ListBinAsync(CancellationToken cancellationToken);

    Task<Result<BulkOperationResult>> MoveToBinAsync(IReadOnlyCollection<Guid> fileIds, IReadOnlyCollection<Guid> folderIds, CancellationToken cancellationToken);

    Task<Result<BulkOperationResult>> RestoreAsync(IReadOnlyCollection<Guid> fileIds, IReadOnlyCollection<Guid> folderIds, CancellationToken cancellationToken);

    /// <summary>Deletes items in the bin for good. Refused for anything still in use.</summary>
    Task<Result<BulkOperationResult>> DeleteForeverAsync(IReadOnlyCollection<Guid> fileIds, IReadOnlyCollection<Guid> folderIds, CancellationToken cancellationToken);

    Task<Result<BulkOperationResult>> EmptyBinAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes for good everything that has been in the bin longer than <see cref="FileLibraryRules.BinRetention"/>.
    /// Run by <see cref="FileBinPurgeHostedService"/> outside any user's session, so across companies.
    /// </summary>
    Task<Result<BulkOperationResult>> PurgeExpiredAsync(DateTime utcNow, CancellationToken cancellationToken);
}

public static class FileLibraryRules
{
    public static readonly TimeSpan BinRetention = TimeSpan.FromDays(30);

    public const long MaxZipBytes = 2L * 1024 * 1024 * 1024;

    public const int MaxZipFiles = 500;
}
