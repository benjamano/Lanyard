namespace Lanyard.Infrastructure.DTO.Files;

/// <summary>One live folder in the Files tree. Counts are the folder's own direct contents.</summary>
public sealed record FolderNode(
    Guid Id,
    string Name,
    Guid? ParentFolderId,
    bool IsSharedWithStaff,
    bool IsSharedEffective,
    int FileCount,
    int FolderCount,
    long Bytes,
    DateTime CreatedAt);

/// <summary>A file as Files lists it. Never carries FilePath (the server's storage location).</summary>
public sealed record FileListItem(
    Guid Id,
    string FileName,
    long FileSize,
    string? ContentType,
    FileTypeGroup TypeGroup,
    string TypeName,
    DateTime UploadedAt,
    string? UploadedByName,
    Guid? FolderId,
    string FolderPath,
    TimeSpan? Duration,
    bool IsInUse,
    bool IsSharedWithStaff,
    bool HasThumbnail = false,
    int? Width = null,
    int? Height = null);

public enum FileSort
{
    Name = 0,
    Uploaded = 1,
    Size = 2,
    Type = 3,
}

/// <summary>
/// What Files shows in its main area. With <see cref="AllFolders"/> false it lists one folder
/// (<see cref="FolderId"/> null = the top level) with its subfolders; with it true it searches
/// every live folder and returns files only.
/// </summary>
public sealed record FileQuery
{
    public Guid? FolderId { get; init; }
    public bool AllFolders { get; init; }
    public string? Search { get; init; }
    public FileTypeGroup? TypeGroup { get; init; }
    public string? UploadedByUserId { get; init; }
    public FileSort Sort { get; init; } = FileSort.Name;
    public bool Descending { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; } = 500;

    /// <summary>Set for staff who don't manage files: only folders shared with staff (and what's in them).</summary>
    public bool SharedOnly { get; init; }
}

public sealed record FileQueryResult(
    IReadOnlyList<FolderNode> Folders,
    IReadOnlyList<FileListItem> Files,
    int TotalFiles);

public enum BinItemKind
{
    File = 0,
    Folder = 1,
}

/// <summary>Something in the Recycle bin. A binned folder's contents go with it and aren't listed separately.</summary>
public sealed record BinItem(
    Guid Id,
    BinItemKind Kind,
    string Name,
    long Bytes,
    int ItemCount,
    DateTime DeletedAt,
    string? DeletedByName,
    DateTime PurgeAfter,
    string OriginalPath,
    FileTypeGroup TypeGroup);

public sealed record StorageSummary(long TotalBytes, int FileCount, IReadOnlyDictionary<FileTypeGroup, long> BytesByGroup, int BinCount, long BinBytes);

/// <summary>The result of an action on several items. Items that couldn't be done are listed with the reason.</summary>
public sealed record BulkOperationResult(int Succeeded, IReadOnlyList<BulkItemFailure> Failures, IReadOnlyList<string> Notes)
{
    public static BulkOperationResult Empty { get; } = new(0, [], []);

    public bool AllSucceeded => Failures.Count == 0;
}

public sealed record BulkItemFailure(Guid Id, string Name, string Reason);

public enum FileUsageKind
{
    Song = 0,
    AudioClip = 1,
    StaffDocument = 2,
    OnboardingAttachment = 3,
    CompanyLogo = 4,
    CompanyBackground = 5,
    PartyPrintout = 6,
    ProjectionProgram = 7,
    TrainingCourse = 8,
}

/// <summary>
/// Something in Lanyard that uses a file. A file is never deleted for good while a usage with
/// <see cref="BlocksPermanentDelete"/> points at it. A song doesn't block: it is retired with its file.
/// </summary>
public sealed record FileUsage(Guid FileId, FileUsageKind Kind, string Description, string? Link, bool BlocksPermanentDelete = true);

/// <summary>One file going into a zip download, with where it sits inside the zip.</summary>
public sealed record ZipEntryPlan(Guid FileId, string EntryPath, long Size);

public sealed record FileSelection(IReadOnlyList<Guid>? FileIds, IReadOnlyList<Guid>? FolderIds)
{
    public IReadOnlyList<Guid> Files => FileIds ?? [];
    public IReadOnlyList<Guid> Folders => FolderIds ?? [];
}

public sealed record MoveRequest(IReadOnlyList<Guid>? FileIds, IReadOnlyList<Guid>? FolderIds, Guid? DestinationFolderId);
