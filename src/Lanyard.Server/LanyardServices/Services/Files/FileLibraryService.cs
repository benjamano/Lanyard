using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Demo;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Files;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public sealed class FileLibraryService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IFileService fileService,
    IFileUsageService usageService,
    ICurrentUserAccessor currentUserAccessor,
    ILogger<FileLibraryService> logger,
    IDemoGuard? demoGuard = null,
    Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor = null,
    Lanyard.Infrastructure.DataAccess.Tenancy.ITenantProvider? tenant = null,
    Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider? authState = null) : IFileLibraryService
{
    private const string DemoRefusal = "Changing files isn't available in the demo.";

    private const string ManagersOnly = "Only people who manage files can do that.";

    // ---------- Reading ----------

    public async Task<Result<IReadOnlyList<FolderNode>>> GetTreeAsync(bool sharedOnly, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            FolderGraph graph = await FolderGraph.LoadAsync(db, cancellationToken);
            Dictionary<Guid, (int Count, long Bytes)> counts = await CountFilesByFolderAsync(db, cancellationToken);

            List<FolderNode> nodes = graph.LiveFolders
                .Where(x => !sharedOnly || graph.IsShared(x.Id))
                .Select(x => ToNode(graph, counts, x, sharedOnly))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Result<IReadOnlyList<FolderNode>>.Ok(nodes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the folder tree");
            return Result<IReadOnlyList<FolderNode>>.Fail("Couldn't load the folders.");
        }
    }

    public async Task<Result<FileQueryResult>> QueryAsync(FileQuery query, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            FolderGraph graph = await FolderGraph.LoadAsync(db, cancellationToken);

            if (!query.AllFolders && query.FolderId is Guid folderId && !graph.IsVisible(folderId, query.SharedOnly))
                return Result<FileQueryResult>.Fail("Folder not found.");

            string? search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim().ToLowerInvariant();

            IQueryable<FileMetadata> files = Library(db).Where(x => x.IsActive);

            if (!query.AllFolders)
                files = files.Where(x => x.FolderId == query.FolderId);

            if (search is not null)
                files = files.Where(x => x.FileName.ToLower().Contains(search));

            if (!string.IsNullOrWhiteSpace(query.UploadedByUserId))
                files = files.Where(x => x.UploadedBy == query.UploadedByUserId);

            List<FileRow> rows = await SelectRows(files).ToListAsync(cancellationToken);

            IEnumerable<FileRow> visible = rows.Where(x => graph.IsFileVisible(x.FolderId, query.SharedOnly));

            if (query.TypeGroup is FileTypeGroup group)
                visible = visible.Where(x => GroupOf(x) == group);

            List<FileRow> matched = Sort(visible, query.Sort, query.Descending).ToList();
            List<FileRow> page = matched.Skip(Math.Max(0, query.Skip)).Take(Math.Clamp(query.Take, 1, 2000)).ToList();

            List<FileListItem> items = await ToItemsAsync(db, graph, page, cancellationToken);

            // Folders: the folder's own subfolders when browsing, or every visible folder whose
            // name matches when searching.
            Dictionary<Guid, (int Count, long Bytes)> counts = await CountFilesByFolderAsync(db, cancellationToken);
            IEnumerable<FolderRow> folders;

            if (search is not null)
            {
                folders = graph.LiveFolders.Where(x => x.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
            }
            else if (query.AllFolders)
            {
                folders = [];
            }
            else if (query.SharedOnly && query.FolderId is null)
            {
                // "Shared files" top level: the highest shared folder of each shared branch.
                folders = graph.LiveFolders.Where(x => graph.IsShared(x.Id) && (x.ParentFolderId is null || !graph.IsShared(x.ParentFolderId.Value)));
            }
            else
            {
                folders = graph.LiveFolders.Where(x => x.ParentFolderId == query.FolderId);
            }

            List<FolderNode> folderNodes = query.TypeGroup is null
                ? folders
                    .Where(x => !query.SharedOnly || graph.IsShared(x.Id))
                    .Select(x => ToNode(graph, counts, x, query.SharedOnly))
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];

            return Result<FileQueryResult>.Ok(new FileQueryResult(folderNodes, items, matched.Count));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to query files in {FolderId}", query.FolderId);
            return Result<FileQueryResult>.Fail("Couldn't load the files.");
        }
    }

    public async Task<Result<FileListItem>> GetFileAsync(Guid fileId, bool sharedOnly, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            FolderGraph graph = await FolderGraph.LoadAsync(db, cancellationToken);

            FileRow? row = await SelectRows(Library(db).Where(x => x.Id == fileId && x.IsActive)).FirstOrDefaultAsync(cancellationToken);

            if (row is null || !graph.IsFileVisible(row.FolderId, sharedOnly))
                return Result<FileListItem>.Fail("File not found.");

            return Result<FileListItem>.Ok((await ToItemsAsync(db, graph, [row], cancellationToken))[0]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load file {FileId}", fileId);
            return Result<FileListItem>.Fail("Couldn't load the file.");
        }
    }

    public async Task<Result<IReadOnlyList<FolderNode>>> GetPathAsync(Guid folderId, bool sharedOnly, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            FolderGraph graph = await FolderGraph.LoadAsync(db, cancellationToken);

            if (!graph.IsVisible(folderId, sharedOnly))
                return Result<IReadOnlyList<FolderNode>>.Fail("Folder not found.");

            Dictionary<Guid, (int Count, long Bytes)> counts = await CountFilesByFolderAsync(db, cancellationToken);

            List<FolderNode> path = graph.PathTo(folderId)
                .Where(x => !sharedOnly || graph.IsShared(x.Id))
                .Select(x => ToNode(graph, counts, x, sharedOnly))
                .ToList();

            return Result<IReadOnlyList<FolderNode>>.Ok(path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the path to folder {FolderId}", folderId);
            return Result<IReadOnlyList<FolderNode>>.Fail("Couldn't load the folder.");
        }
    }

    public async Task<Result<StorageSummary>> GetStorageSummaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            FolderGraph graph = await FolderGraph.LoadAsync(db, cancellationToken);

            List<FileRow> rows = await SelectRows(Library(db)).ToListAsync(cancellationToken);

            List<FileRow> live = rows.Where(x => x.IsActive && graph.IsFileVisible(x.FolderId, sharedOnly: false)).ToList();
            List<FileRow> binned = rows.Where(x => !x.IsActive || (x.FolderId is Guid f && !graph.IsLive(f))).ToList();

            Dictionary<FileTypeGroup, long> byGroup = Enum.GetValues<FileTypeGroup>().ToDictionary(x => x, _ => 0L);

            foreach (FileRow row in live)
                byGroup[GroupOf(row)] += row.FileSize;

            int binCount = BinTopLevel(graph, rows).Count;

            return Result<StorageSummary>.Ok(new StorageSummary(live.Sum(x => x.FileSize), live.Count, byGroup, binCount, binned.Sum(x => x.FileSize)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to work out file storage use");
            return Result<StorageSummary>.Fail("Couldn't work out how much space files use.");
        }
    }

    public async Task<Result<IReadOnlyList<ZipEntryPlan>>> PlanZipAsync(IReadOnlyCollection<Guid> fileIds, IReadOnlyCollection<Guid> folderIds, bool sharedOnly, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            FolderGraph graph = await FolderGraph.LoadAsync(db, cancellationToken);

            List<Guid> folders = folderIds.Where(x => graph.IsVisible(x, sharedOnly)).ToList();
            List<Guid> subtree = folders.SelectMany(graph.SubtreeOf).Where(graph.IsLive).Distinct().ToList();

            List<FileRow> rows = await SelectRows(Library(db).Where(x => x.IsActive && (fileIds.Contains(x.Id) || (x.FolderId != null && subtree.Contains(x.FolderId.Value)))))
                .ToListAsync(cancellationToken);

            List<ZipEntryPlan> plan = [];
            Dictionary<string, HashSet<string>> namesByDirectory = new(StringComparer.OrdinalIgnoreCase);

            void Add(FileRow row, string directory)
            {
                if (!namesByDirectory.TryGetValue(directory, out HashSet<string>? names))
                    namesByDirectory[directory] = names = new(StringComparer.OrdinalIgnoreCase);

                // Two selected files with the same name from different folders.
                string name = FileNameRules.MakeUnique(row.FileName, names);
                names.Add(name);

                plan.Add(new ZipEntryPlan(row.Id, directory.Length == 0 ? name : $"{directory}/{name}", row.FileSize));
            }

            // Each selected folder becomes a top-level directory in the zip, with its own
            // subfolders under it.
            foreach (Guid folderId in folders)
            {
                List<FolderRow> folderPath = graph.PathTo(folderId);
                int depth = folderPath.Count - 1;

                foreach (Guid inside in graph.SubtreeOf(folderId).Where(graph.IsLive))
                {
                    string directory = string.Join("/", graph.PathTo(inside).Skip(depth).Select(x => x.Name));

                    foreach (FileRow row in rows.Where(x => x.FolderId == inside).OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase))
                        Add(row, directory);
                }
            }

            HashSet<Guid> planned = [.. plan.Select(x => x.FileId)];

            foreach (FileRow row in rows.Where(x => fileIds.Contains(x.Id) && !planned.Contains(x.Id) && graph.IsFileVisible(x.FolderId, sharedOnly)))
                Add(row, string.Empty);

            if (plan.Count == 0)
                return Result<IReadOnlyList<ZipEntryPlan>>.Fail("There's nothing to download.");

            if (plan.Count > FileLibraryRules.MaxZipFiles)
                return Result<IReadOnlyList<ZipEntryPlan>>.Fail($"A download can hold up to {FileLibraryRules.MaxZipFiles} files. Choose fewer.");

            if (plan.Sum(x => x.Size) > FileLibraryRules.MaxZipBytes)
                return Result<IReadOnlyList<ZipEntryPlan>>.Fail($"A download can be up to {FileTypeInfo.FormatSize(FileLibraryRules.MaxZipBytes)}. Choose fewer files.");

            return Result<IReadOnlyList<ZipEntryPlan>>.Ok(plan);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to plan a zip of {Files} files and {Folders} folders", fileIds.Count, folderIds.Count);
            return Result<IReadOnlyList<ZipEntryPlan>>.Fail("Couldn't prepare the download.");
        }
    }

    public async Task<Result<IReadOnlyList<Guid>>> GetFileIdsInFoldersAsync(IReadOnlyCollection<Guid> folderIds, CancellationToken cancellationToken)
    {
        try
        {
            if (folderIds.Count == 0)
                return Result<IReadOnlyList<Guid>>.Ok([]);

            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            FolderGraph graph = await FolderGraph.LoadAsync(db, cancellationToken);
            List<Guid> subtree = folderIds.SelectMany(graph.SubtreeOf).Distinct().ToList();

            List<Guid> ids = await Library(db).AsNoTracking()
                .Where(x => x.FolderId != null && subtree.Contains(x.FolderId.Value))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            return Result<IReadOnlyList<Guid>>.Ok(ids);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list the files in {Count} folders", folderIds.Count);
            return Result<IReadOnlyList<Guid>>.Fail("Couldn't check what's in those folders.");
        }
    }

    // ---------- Organising ----------

    public async Task<Result<BulkOperationResult>> MoveAsync(IReadOnlyCollection<Guid> fileIds, IReadOnlyCollection<Guid> folderIds, Guid? destinationFolderId, CancellationToken cancellationToken)
    {
        try
        {
            if (await IsDemoAsync())
                return Result<BulkOperationResult>.Fail(DemoRefusal);

            if (!await CanManageAsync())
                return Result<BulkOperationResult>.Fail(ManagersOnly);

            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            FolderGraph graph = await FolderGraph.LoadAsync(db, cancellationToken);

            if (destinationFolderId is Guid destination && !graph.IsLive(destination))
                return Result<BulkOperationResult>.Fail("That folder doesn't exist any more.");

            List<BulkItemFailure> failures = [];
            int succeeded = 0;

            HashSet<string> folderNamesThere = new(graph.LiveFolders.Where(x => x.ParentFolderId == destinationFolderId).Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            List<Folder> folders = await db.Folders.Where(x => folderIds.Contains(x.Id)).ToListAsync(cancellationToken);

            foreach (Guid id in folderIds)
            {
                Folder? folder = folders.FirstOrDefault(x => x.Id == id);

                if (folder is null || !graph.IsLive(id))
                {
                    failures.Add(new BulkItemFailure(id, folder?.Name ?? "Folder", "It doesn't exist any more."));
                    continue;
                }

                if (folder.ParentFolderId == destinationFolderId)
                {
                    succeeded++;
                    continue;
                }

                if (destinationFolderId is Guid target && graph.SubtreeOf(id).Contains(target))
                {
                    failures.Add(new BulkItemFailure(id, folder.Name, "A folder can't go inside itself."));
                    continue;
                }

                if (!folderNamesThere.Add(folder.Name))
                {
                    failures.Add(new BulkItemFailure(id, folder.Name, $"There's already a folder called \"{folder.Name}\" there."));
                    continue;
                }

                folder.ParentFolderId = destinationFolderId;
                succeeded++;
            }

            HashSet<string> fileNamesThere = new(
                await Library(db).Where(x => x.IsActive && x.FolderId == destinationFolderId && !fileIds.Contains(x.Id)).Select(x => x.FileName).ToListAsync(cancellationToken),
                StringComparer.OrdinalIgnoreCase);
            List<FileMetadata> files = await Library(db).Where(x => fileIds.Contains(x.Id)).ToListAsync(cancellationToken);

            foreach (Guid id in fileIds)
            {
                FileMetadata? file = files.FirstOrDefault(x => x.Id == id);

                if (file is null || !file.IsActive || !graph.IsFileVisible(file.FolderId, sharedOnly: false))
                {
                    failures.Add(new BulkItemFailure(id, file?.FileName ?? "File", "It doesn't exist any more."));
                    continue;
                }

                if (file.FolderId == destinationFolderId)
                {
                    fileNamesThere.Add(file.FileName);
                    succeeded++;
                    continue;
                }

                if (!fileNamesThere.Add(file.FileName))
                {
                    failures.Add(new BulkItemFailure(id, file.FileName, $"There's already a file called \"{file.FileName}\" there."));
                    continue;
                }

                file.FolderId = destinationFolderId;
                succeeded++;
            }

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Moved {Succeeded} items to folder {FolderId}; {Failed} couldn't move", succeeded, destinationFolderId, failures.Count);
            return Result<BulkOperationResult>.Ok(new BulkOperationResult(succeeded, failures, []));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to move items to folder {FolderId}", destinationFolderId);
            return Result<BulkOperationResult>.Fail("Couldn't move those items.");
        }
    }

    public async Task<Result<FolderNode>> SetFolderSharedAsync(Guid folderId, bool isShared, CancellationToken cancellationToken)
    {
        try
        {
            if (await IsDemoAsync())
                return Result<FolderNode>.Fail(DemoRefusal);

            if (!await CanManageAsync())
                return Result<FolderNode>.Fail(ManagersOnly);

            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            FolderGraph graph = await FolderGraph.LoadAsync(db, cancellationToken);

            Folder? folder = await db.Folders.FirstOrDefaultAsync(x => x.Id == folderId, cancellationToken);

            if (folder is null || !graph.IsLive(folderId))
                return Result<FolderNode>.Fail("Folder not found.");

            folder.IsSharedWithStaff = isShared;
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Folder {FolderId} is now {Sharing}", folderId, isShared ? "shared with staff" : "for file managers only");

            graph = await FolderGraph.LoadAsync(db, cancellationToken);
            return Result<FolderNode>.Ok(ToNode(graph, await CountFilesByFolderAsync(db, cancellationToken), graph.Get(folderId)!, sharedOnly: false));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to change sharing for folder {FolderId}", folderId);
            return Result<FolderNode>.Fail("Couldn't change who can see the folder.");
        }
    }

    // ---------- Recycle bin ----------

    public async Task<Result<IReadOnlyList<BinItem>>> ListBinAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await CanManageAsync())
                return Result<IReadOnlyList<BinItem>>.Fail(ManagersOnly);

            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            return Result<IReadOnlyList<BinItem>>.Ok(await LoadBinAsync(db, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list the Recycle bin");
            return Result<IReadOnlyList<BinItem>>.Fail("Couldn't load the Recycle bin.");
        }
    }

    public async Task<Result<BulkOperationResult>> MoveToBinAsync(IReadOnlyCollection<Guid> fileIds, IReadOnlyCollection<Guid> folderIds, CancellationToken cancellationToken)
    {
        try
        {
            if (await IsDemoAsync())
                return Result<BulkOperationResult>.Fail(DemoRefusal);

            if (!await CanManageAsync())
                return Result<BulkOperationResult>.Fail(ManagersOnly);

            string? userId = await CurrentUserIdAsync();
            DateTime now = DateTime.UtcNow;

            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            FolderGraph graph = await FolderGraph.LoadAsync(db, cancellationToken);

            List<BulkItemFailure> failures = [];
            int succeeded = 0;

            List<Folder> folders = await db.Folders.Where(x => folderIds.Contains(x.Id)).ToListAsync(cancellationToken);

            foreach (Guid id in folderIds)
            {
                Folder? folder = folders.FirstOrDefault(x => x.Id == id);

                if (folder is null || !graph.IsLive(id))
                {
                    failures.Add(new BulkItemFailure(id, folder?.Name ?? "Folder", "It's already gone."));
                    continue;
                }

                folder.IsActive = false;
                folder.DeletedAt = now;
                folder.DeletedByUserId = userId;
                succeeded++;
            }

            List<FileMetadata> files = await Library(db).Where(x => fileIds.Contains(x.Id)).ToListAsync(cancellationToken);

            foreach (Guid id in fileIds)
            {
                FileMetadata? file = files.FirstOrDefault(x => x.Id == id);

                if (file is null || !file.IsActive)
                {
                    failures.Add(new BulkItemFailure(id, file?.FileName ?? "File", "It's already gone."));
                    continue;
                }

                file.IsActive = false;
                file.DeletedAt = now;
                file.DeletedByUserId = userId;
                succeeded++;
            }

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Moved {Succeeded} items to the Recycle bin", succeeded);
            return Result<BulkOperationResult>.Ok(new BulkOperationResult(succeeded, failures, []));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to move items to the Recycle bin");
            return Result<BulkOperationResult>.Fail("Couldn't delete those items.");
        }
    }

    public async Task<Result<BulkOperationResult>> RestoreAsync(IReadOnlyCollection<Guid> fileIds, IReadOnlyCollection<Guid> folderIds, CancellationToken cancellationToken)
    {
        try
        {
            if (await IsDemoAsync())
                return Result<BulkOperationResult>.Fail(DemoRefusal);

            if (!await CanManageAsync())
                return Result<BulkOperationResult>.Fail(ManagersOnly);

            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            FolderGraph graph = await FolderGraph.LoadAsync(db, cancellationToken);

            List<BulkItemFailure> failures = [];
            List<string> notes = [];
            int succeeded = 0;

            // Folders first, so a file restored in the same go can land back in its restored folder.
            List<Folder> folders = await db.Folders.Where(x => folderIds.Contains(x.Id)).ToListAsync(cancellationToken);
            HashSet<Guid> restoredFolders = [];

            foreach (Guid id in folderIds)
            {
                Folder? folder = folders.FirstOrDefault(x => x.Id == id);

                if (folder is null || folder.IsActive)
                {
                    failures.Add(new BulkItemFailure(id, folder?.Name ?? "Folder", "It isn't in the Recycle bin."));
                    continue;
                }

                if (folder.ParentFolderId is Guid parent && !graph.IsLive(parent) && !restoredFolders.Contains(parent))
                {
                    folder.ParentFolderId = null;
                    notes.Add($"\"{folder.Name}\" went back to the top level, because the folder it was in has been deleted.");
                }

                List<string> taken = graph.LiveFolders.Where(x => x.ParentFolderId == folder.ParentFolderId && x.Id != id).Select(x => x.Name)
                    .Concat(folders.Where(x => restoredFolders.Contains(x.Id) && x.ParentFolderId == folder.ParentFolderId).Select(x => x.Name))
                    .ToList();
                string name = FileNameRules.MakeUnique(folder.Name, taken);

                if (name != folder.Name)
                {
                    notes.Add($"\"{folder.Name}\" was restored as \"{name}\", because that name is in use now.");
                    folder.Name = name;
                }

                folder.IsActive = true;
                folder.DeletedAt = null;
                folder.DeletedByUserId = null;
                restoredFolders.Add(id);
                succeeded++;
            }

            List<FileMetadata> files = await Library(db).Where(x => fileIds.Contains(x.Id)).ToListAsync(cancellationToken);
            // Keyed by folder, with Guid.Empty for the top level (a dictionary can't take a null key).
            Dictionary<Guid, HashSet<string>> namesByFolder = [];

            async Task<HashSet<string>> NamesIn(Guid? folderId)
            {
                Guid key = folderId ?? Guid.Empty;

                if (!namesByFolder.TryGetValue(key, out HashSet<string>? names))
                {
                    names = new(await Library(db).Where(x => x.IsActive && x.FolderId == folderId).Select(x => x.FileName).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);
                    namesByFolder[key] = names;
                }

                return names;
            }

            foreach (Guid id in fileIds)
            {
                FileMetadata? file = files.FirstOrDefault(x => x.Id == id);

                if (file is null || file.IsActive)
                {
                    failures.Add(new BulkItemFailure(id, file?.FileName ?? "File", "It isn't in the Recycle bin."));
                    continue;
                }

                // Back where it was, unless that folder (or one above it) is still in the bin.
                if (file.FolderId is Guid folderId && !graph.PathTo(folderId).All(x => x.IsActive || restoredFolders.Contains(x.Id)))
                {
                    file.FolderId = null;
                    notes.Add($"\"{file.FileName}\" went back to the top level, because its folder has been deleted.");
                }

                HashSet<string> taken = await NamesIn(file.FolderId);
                string name = FileNameRules.MakeUnique(file.FileName, taken);

                if (name != file.FileName)
                {
                    notes.Add($"\"{file.FileName}\" was restored as \"{name}\", because that name is in use now.");
                    file.FileName = name;
                }

                taken.Add(name);
                file.IsActive = true;
                file.DeletedAt = null;
                file.DeletedByUserId = null;
                succeeded++;
            }

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Restored {Succeeded} items from the Recycle bin", succeeded);
            return Result<BulkOperationResult>.Ok(new BulkOperationResult(succeeded, failures, notes));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to restore items from the Recycle bin");
            return Result<BulkOperationResult>.Fail("Couldn't restore those items.");
        }
    }

    public async Task<Result<BulkOperationResult>> DeleteForeverAsync(IReadOnlyCollection<Guid> fileIds, IReadOnlyCollection<Guid> folderIds, CancellationToken cancellationToken)
    {
        if (await IsDemoAsync())
            return Result<BulkOperationResult>.Fail(DemoRefusal);

        if (!await CanManageAsync())
            return Result<BulkOperationResult>.Fail(ManagersOnly);

        return await DeleteForeverCoreAsync(fileIds, folderIds, cancellationToken);
    }

    public async Task<Result<BulkOperationResult>> EmptyBinAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await IsDemoAsync())
                return Result<BulkOperationResult>.Fail(DemoRefusal);

            if (!await CanManageAsync())
                return Result<BulkOperationResult>.Fail(ManagersOnly);

            List<BinItem> bin;

            await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken))
                bin = await LoadBinAsync(db, cancellationToken);

            return await DeleteForeverCoreAsync(
                bin.Where(x => x.Kind == BinItemKind.File).Select(x => x.Id).ToList(),
                bin.Where(x => x.Kind == BinItemKind.Folder).Select(x => x.Id).ToList(),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to empty the Recycle bin");
            return Result<BulkOperationResult>.Fail("Couldn't empty the Recycle bin.");
        }
    }

    public async Task<Result<BulkOperationResult>> PurgeExpiredAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        try
        {
            List<BinItem> expired;

            await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken))
                expired = (await LoadBinAsync(db, cancellationToken)).Where(x => x.PurgeAfter <= utcNow).ToList();

            if (expired.Count == 0)
                return Result<BulkOperationResult>.Ok(BulkOperationResult.Empty);

            Result<BulkOperationResult> result = await DeleteForeverCoreAsync(
                expired.Where(x => x.Kind == BinItemKind.File).Select(x => x.Id).ToList(),
                expired.Where(x => x.Kind == BinItemKind.Folder).Select(x => x.Id).ToList(),
                cancellationToken);

            if (result.Data is BulkOperationResult done)
            {
                logger.LogInformation("Recycle bin purge removed {Succeeded} items older than {Days} days; kept {Kept} still in use",
                    done.Succeeded, FileLibraryRules.BinRetention.TotalDays, done.Failures.Count);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to purge the Recycle bin");
            return Result<BulkOperationResult>.Fail("Couldn't purge the Recycle bin.");
        }
    }

    private async Task<Result<BulkOperationResult>> DeleteForeverCoreAsync(IReadOnlyCollection<Guid> fileIds, IReadOnlyCollection<Guid> folderIds, CancellationToken cancellationToken)
    {
        try
        {
            List<BulkItemFailure> failures = [];
            int succeeded = 0;

            FolderGraph graph;
            List<FileRow> rows;

            await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                graph = await FolderGraph.LoadAsync(db, cancellationToken);

                List<Guid> subtreeFolders = folderIds.SelectMany(graph.SubtreeOf).ToList();

                rows = await SelectRows(db.FileMetadata.Where(x => fileIds.Contains(x.Id) || (x.FolderId != null && subtreeFolders.Contains(x.FolderId.Value))))
                    .ToListAsync(cancellationToken);
            }

            Result<IReadOnlyDictionary<Guid, IReadOnlyList<FileUsage>>> usages = await usageService.GetUsagesAsync(rows.Select(x => x.Id).ToList(), cancellationToken);

            if (!usages.IsSuccess || usages.Data is null)
                return Result<BulkOperationResult>.Fail(usages.Error ?? "Couldn't check what uses these files.");

            string? BlockedBy(Guid fileId) =>
                usages.Data.TryGetValue(fileId, out IReadOnlyList<FileUsage>? list) && list.Any(x => x.BlocksPermanentDelete)
                    ? string.Join("; ", list.Where(x => x.BlocksPermanentDelete).Select(x => x.Description))
                    : null;

            foreach (Guid id in fileIds)
            {
                FileRow? row = rows.FirstOrDefault(x => x.Id == id);

                if (row is null || row.IsRestricted || row.IsSystemFile)
                {
                    failures.Add(new BulkItemFailure(id, row?.FileName ?? "File", "It's already gone."));
                    continue;
                }

                if (row.IsActive && graph.IsFileVisible(row.FolderId, sharedOnly: false))
                {
                    failures.Add(new BulkItemFailure(id, row.FileName, "Move it to the Recycle bin first."));
                    continue;
                }

                if (BlockedBy(id) is string blocked)
                {
                    failures.Add(new BulkItemFailure(id, row.FileName, $"It's still used by: {blocked}."));
                    continue;
                }

                Result<bool> deleted = await fileService.DeleteFileAsync(id, cancellationToken);

                if (deleted.IsSuccess)
                    succeeded++;
                else
                    failures.Add(new BulkItemFailure(id, row.FileName, deleted.Error ?? "Couldn't delete it."));
            }

            foreach (Guid folderId in folderIds)
            {
                FolderRow? folder = graph.Get(folderId);

                if (folder is null)
                {
                    failures.Add(new BulkItemFailure(folderId, "Folder", "It's already gone."));
                    continue;
                }

                if (graph.IsLive(folderId))
                {
                    failures.Add(new BulkItemFailure(folderId, folder.Name, "Move it to the Recycle bin first."));
                    continue;
                }

                HashSet<Guid> subtree = [.. graph.SubtreeOf(folderId)];
                List<FileRow> inside = rows.Where(x => x.FolderId is Guid f && subtree.Contains(f)).ToList();
                List<FileRow> blocked = inside.Where(x => BlockedBy(x.Id) is not null).ToList();

                if (blocked.Count > 0)
                {
                    failures.Add(new BulkItemFailure(folderId, folder.Name,
                        $"{blocked.Count} file{(blocked.Count == 1 ? " in it is" : "s in it are")} still used (\"{blocked[0].FileName}\": {BlockedBy(blocked[0].Id)})."));
                    continue;
                }

                bool allGone = true;

                foreach (FileRow file in inside)
                {
                    // Restricted and system files never live in folders, but if one does it
                    // mustn't be deleted with the folder: it goes back to the top level instead.
                    if (file.IsRestricted || file.IsSystemFile)
                        continue;

                    Result<bool> deleted = await fileService.DeleteFileAsync(file.Id, cancellationToken);

                    if (!deleted.IsSuccess)
                    {
                        allGone = false;
                        failures.Add(new BulkItemFailure(folderId, folder.Name, $"\"{file.FileName}\" couldn't be deleted: {deleted.Error}"));
                        break;
                    }
                }

                if (!allGone)
                    continue;

                await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

                List<FileMetadata> leftovers = await db.FileMetadata.Where(x => x.FolderId != null && subtree.Contains(x.FolderId.Value)).ToListAsync(cancellationToken);

                foreach (FileMetadata leftover in leftovers)
                    leftover.FolderId = null;

                await db.SaveChangesAsync(cancellationToken);

                // Deepest first, so no folder is removed while a subfolder still points at it.
                foreach (Guid id in graph.SubtreeOf(folderId).Reverse())
                {
                    Folder? doomed = await db.Folders.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

                    if (doomed is null)
                        continue;

                    db.Folders.Remove(doomed);
                    await db.SaveChangesAsync(cancellationToken);
                }

                succeeded++;
            }

            logger.LogInformation("Deleted {Succeeded} items for good; {Failed} were kept", succeeded, failures.Count);
            return Result<BulkOperationResult>.Ok(new BulkOperationResult(succeeded, failures, []));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete items for good");
            return Result<BulkOperationResult>.Fail("Couldn't delete those items.");
        }
    }

    private async Task<List<BinItem>> LoadBinAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        FolderGraph graph = await FolderGraph.LoadAsync(db, cancellationToken);
        List<Guid> deadFolders = graph.AllFolders.Where(x => !graph.IsLive(x.Id)).Select(x => x.Id).ToList();

        List<FileRow> rows = await SelectRows(Library(db).Where(x => !x.IsActive || (x.FolderId != null && deadFolders.Contains(x.FolderId.Value))))
            .ToListAsync(cancellationToken);

        List<BinItem> items = BinTopLevel(graph, rows);

        Dictionary<string, string> names = await ResolveNamesAsync(db, items.Select(x => x.DeletedByName), cancellationToken);

        return items
            .Select(x => x with { DeletedByName = x.DeletedByName is string id && names.TryGetValue(id, out string? name) ? name : null })
            .OrderByDescending(x => x.DeletedAt)
            .ToList();
    }

    // The bin's top level: binned folders whose parent is still live, and binned files whose
    // folder is still live (or that were at the top level). DeletedByName holds the user id until
    // LoadBinAsync swaps in the name.
    private static List<BinItem> BinTopLevel(FolderGraph graph, List<FileRow> rows)
    {
        List<BinItem> items = [];

        foreach (FolderRow folder in graph.AllFolders.Where(x => !x.IsActive && (x.ParentFolderId is null || graph.IsLive(x.ParentFolderId.Value))))
        {
            HashSet<Guid> subtree = [.. graph.SubtreeOf(folder.Id)];
            List<FileRow> inside = rows.Where(x => x.FolderId is Guid f && subtree.Contains(f)).ToList();
            DateTime deletedAt = folder.DeletedAt ?? DateTime.UtcNow;

            items.Add(new BinItem(folder.Id, BinItemKind.Folder, folder.Name, inside.Sum(x => x.FileSize), inside.Count + subtree.Count - 1,
                deletedAt, folder.DeletedByUserId, deletedAt + FileLibraryRules.BinRetention, graph.PathOf(folder.ParentFolderId), FileTypeGroup.Other));
        }

        foreach (FileRow file in rows.Where(x => !x.IsActive && (x.FolderId is null || graph.IsLive(x.FolderId.Value))))
        {
            DateTime deletedAt = file.DeletedAt ?? DateTime.UtcNow;

            items.Add(new BinItem(file.Id, BinItemKind.File, file.FileName, file.FileSize, 1,
                deletedAt, file.DeletedByUserId, deletedAt + FileLibraryRules.BinRetention, graph.PathOf(file.FolderId), GroupOf(file)));
        }

        return items;
    }

    // ---------- Helpers ----------

    private static IQueryable<FileMetadata> Library(ApplicationDbContext db) =>
        db.FileMetadata.TagWithCallSite().Where(x => !x.IsRestricted && !x.IsSystemFile);

    private sealed record FileRow(
        Guid Id, string FileName, long FileSize, string? ContentType, DateTime UploadedAt, string? UploadedBy,
        Guid? FolderId, TimeSpan? Duration, bool IsActive, DateTime? DeletedAt, string? DeletedByUserId,
        bool IsRestricted, bool IsSystemFile, bool HasThumbnail = false, int? Width = null, int? Height = null);

    private static IQueryable<FileRow> SelectRows(IQueryable<FileMetadata> files) =>
        files.AsNoTracking().Select(x => new FileRow(
            x.Id, x.FileName, x.FileSize, x.ContentType, x.UploadedAt, x.UploadedBy,
            x.FolderId, x.VideoLength, x.IsActive, x.DeletedAt, x.DeletedByUserId,
            x.IsRestricted, x.IsSystemFile, x.ThumbnailPath != null && x.ThumbnailPath != "", x.Width, x.Height));

    private static FileTypeGroup GroupOf(FileRow row)
    {
        FileTypeGroup group = FileTypeInfo.GroupOf(row.FileName);

        if (group != FileTypeGroup.Other || row.ContentType is null)
            return group;

        // Older uploads kept whatever type the browser sent, sometimes with an odd extension.
        return row.ContentType switch
        {
            string t when t.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => FileTypeGroup.Image,
            string t when t.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => FileTypeGroup.Video,
            string t when t.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => FileTypeGroup.Audio,
            _ => FileTypeGroup.Other,
        };
    }

    private static IEnumerable<FileRow> Sort(IEnumerable<FileRow> rows, FileSort sort, bool descending)
    {
        IOrderedEnumerable<FileRow> ordered = sort switch
        {
            FileSort.Uploaded => rows.OrderBy(x => x.UploadedAt),
            FileSort.Size => rows.OrderBy(x => x.FileSize),
            FileSort.Type => rows.OrderBy(x => FileTypeInfo.FriendlyName(x.FileName), StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase),
        };

        if (descending)
        {
            ordered = sort switch
            {
                FileSort.Uploaded => rows.OrderByDescending(x => x.UploadedAt),
                FileSort.Size => rows.OrderByDescending(x => x.FileSize),
                FileSort.Type => rows.OrderByDescending(x => FileTypeInfo.FriendlyName(x.FileName), StringComparer.OrdinalIgnoreCase),
                _ => rows.OrderByDescending(x => x.FileName, StringComparer.OrdinalIgnoreCase),
            };
        }

        return ordered.ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<FileListItem>> ToItemsAsync(ApplicationDbContext db, FolderGraph graph, List<FileRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
            return [];

        Result<IReadOnlyDictionary<Guid, IReadOnlyList<FileUsage>>> usages = await usageService.GetUsagesAsync(rows.Select(x => x.Id).ToList(), cancellationToken);
        Dictionary<string, string> names = await ResolveNamesAsync(db, rows.Select(x => x.UploadedBy), cancellationToken);

        return rows.Select(x => new FileListItem(
            x.Id,
            x.FileName,
            x.FileSize,
            x.ContentType,
            GroupOf(x),
            FileTypeInfo.FriendlyName(x.FileName),
            x.UploadedAt,
            x.UploadedBy is string by && names.TryGetValue(by, out string? name) ? name : null,
            x.FolderId,
            graph.PathOf(x.FolderId),
            x.Duration,
            usages.Data?.TryGetValue(x.Id, out IReadOnlyList<FileUsage>? list) == true && list.Any(u => u.BlocksPermanentDelete),
            x.FolderId is Guid f && graph.IsShared(f),
            x.HasThumbnail,
            x.Width,
            x.Height)).ToList();
    }

    // UploadedBy holds a user id (or, for uploads through the old API, a username).
    private static async Task<Dictionary<string, string>> ResolveNamesAsync(ApplicationDbContext db, IEnumerable<string?> keys, CancellationToken cancellationToken)
    {
        List<string> wanted = keys.OfType<string>().Where(x => x.Length > 0).Distinct().ToList();

        if (wanted.Count == 0)
            return [];

        var users = await db.Users.AsNoTracking().TagWithCallSite()
            .Where(x => wanted.Contains(x.Id) || (x.UserName != null && wanted.Contains(x.UserName)))
            .Select(x => new { x.Id, x.UserName, x.FirstName, x.LastName })
            .ToListAsync(cancellationToken);

        Dictionary<string, string> names = [];

        foreach (var user in users)
        {
            string full = $"{user.FirstName} {user.LastName}".Trim();
            string display = full.Length > 0 ? full : user.UserName ?? "Someone";

            names[user.Id] = display;

            if (user.UserName is not null)
                names.TryAdd(user.UserName, display);
        }

        return names;
    }

    private static async Task<Dictionary<Guid, (int Count, long Bytes)>> CountFilesByFolderAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var counts = await Library(db).AsNoTracking()
            .Where(x => x.IsActive && x.FolderId != null)
            .GroupBy(x => x.FolderId!.Value)
            .Select(g => new { FolderId = g.Key, Count = g.Count(), Bytes = g.Sum(x => x.FileSize) })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(x => x.FolderId, x => (x.Count, x.Bytes));
    }

    private static FolderNode ToNode(FolderGraph graph, Dictionary<Guid, (int Count, long Bytes)> counts, FolderRow folder, bool sharedOnly)
    {
        (int count, long bytes) = counts.TryGetValue(folder.Id, out (int Count, long Bytes) c) ? c : (0, 0);

        // Staff only see the shared branch, so a shared folder under an unshared one is a top-level folder to them.
        Guid? parent = sharedOnly && folder.ParentFolderId is Guid p && !graph.IsShared(p) ? null : folder.ParentFolderId;

        return new FolderNode(folder.Id, folder.Name, parent, folder.IsSharedWithStaff, graph.IsShared(folder.Id),
            count, graph.LiveFolders.Count(x => x.ParentFolderId == folder.Id), bytes, folder.CreatedAt);
    }

    private async Task<bool> IsDemoAsync() => demoGuard is not null && await demoGuard.IsDemoSessionAsync();

    // Defence in depth: the API and the Files page already only offer these to Admins and
    // CanManageFiles, but every caller (pickers included) gets the same answer here. Background
    // work (the purge sweep, nobody signed in) is the system and may.
    private async Task<bool> CanManageAsync()
    {
        if (tenant is null || tenant.IsSystem)
            return true;

        System.Security.Claims.ClaimsPrincipal? user = httpContextAccessor?.HttpContext?.User;

        if (user?.Identity?.IsAuthenticated != true && authState is not null)
        {
            try
            {
                user = (await authState.GetAuthenticationStateAsync()).User;
            }
            catch (InvalidOperationException)
            {
                // No Blazor auth state in this scope.
            }
        }

        return user is not null && (user.IsInRole("Admin") || user.IsInRole("CanManageFiles"));
    }

    private async Task<string?> CurrentUserIdAsync()
    {
        try
        {
            // An API request has no Blazor auth state; its user is on the HttpContext.
            string? fromRequest = httpContextAccessor?.HttpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            if (fromRequest is not null)
                return fromRequest;

            Result<string> result = await currentUserAccessor.GetCurrentUserIdAsync();
            return result.IsSuccess ? result.Data : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No signed-in user to record against a delete");
            return null;
        }
    }

    // Every folder in the company (folders are few), with what's worked out from the whole
    // hierarchy: whether a folder is live (it and all its ancestors are out of the bin), whether
    // it's shared with staff (it or an ancestor is), its path and its subtree.
    private sealed record FolderRow(Guid Id, string Name, Guid? ParentFolderId, bool IsActive, bool IsSharedWithStaff, DateTime CreatedAt, DateTime? DeletedAt, string? DeletedByUserId);

    private sealed class FolderGraph
    {
        private readonly Dictionary<Guid, FolderRow> _byId;
        private readonly Dictionary<Guid, List<FolderRow>> _children = [];
        private readonly Dictionary<Guid, bool> _live = [];
        private readonly Dictionary<Guid, bool> _shared = [];

        private FolderGraph(List<FolderRow> folders)
        {
            _byId = folders.ToDictionary(x => x.Id);

            foreach (FolderRow folder in folders)
            {
                if (folder.ParentFolderId is Guid parent)
                {
                    if (!_children.TryGetValue(parent, out List<FolderRow>? list))
                        _children[parent] = list = [];

                    list.Add(folder);
                }
            }

            LiveFolders = folders.Where(x => IsLive(x.Id)).ToList();
        }

        public static async Task<FolderGraph> LoadAsync(ApplicationDbContext db, CancellationToken cancellationToken) =>
            new(await db.Folders.AsNoTracking().TagWithCallSite()
                .Select(x => new FolderRow(x.Id, x.Name, x.ParentFolderId, x.IsActive, x.IsSharedWithStaff, x.CreatedAt, x.DeletedAt, x.DeletedByUserId))
                .ToListAsync(cancellationToken));

        public IReadOnlyCollection<FolderRow> AllFolders => _byId.Values;

        public IReadOnlyList<FolderRow> LiveFolders { get; }

        public FolderRow? Get(Guid id) => _byId.GetValueOrDefault(id);

        public bool IsLive(Guid id) => Resolve(id, _live, x => x.IsActive, requireAll: true);

        public bool IsShared(Guid id) => IsLive(id) && Resolve(id, _shared, x => x.IsSharedWithStaff, requireAll: false);

        public bool IsVisible(Guid folderId, bool sharedOnly) => IsLive(folderId) && (!sharedOnly || IsShared(folderId));

        // Top-level files are never shared: sharing is a folder setting.
        public bool IsFileVisible(Guid? folderId, bool sharedOnly) => folderId is Guid f ? IsVisible(f, sharedOnly) : !sharedOnly;

        // requireAll: true when every folder up the chain must pass (live), false when any may (shared).
        private bool Resolve(Guid id, Dictionary<Guid, bool> memo, Func<FolderRow, bool> test, bool requireAll)
        {
            if (memo.TryGetValue(id, out bool known))
                return known;

            List<Guid> chain = [];
            HashSet<Guid> seen = [];
            Guid? current = id;
            bool? answer = null;

            while (current is Guid c)
            {
                if (memo.TryGetValue(c, out bool cached))
                {
                    answer = cached;
                    break;
                }

                // A missing folder, or a loop in the data, counts as not live / not shared.
                if (!_byId.TryGetValue(c, out FolderRow? row) || !seen.Add(c))
                {
                    answer = false;
                    break;
                }

                chain.Add(c);

                bool passes = test(row);

                if (requireAll && !passes)
                {
                    answer = false;
                    break;
                }

                if (!requireAll && passes)
                {
                    answer = true;
                    break;
                }

                current = row.ParentFolderId;
            }

            // Reached the top: everything passed (requireAll) or nothing did (any).
            bool result = answer ?? requireAll;

            // Only the folders below the deciding point share the answer for "any"; for "all"
            // a failure part way up fails everything below it too. Either way the whole chain
            // walked gets the same answer.
            foreach (Guid walked in chain)
                memo[walked] = result;

            return result;
        }

        /// <summary>The folder and everything under it, parents before children.</summary>
        public IEnumerable<Guid> SubtreeOf(Guid id)
        {
            List<Guid> result = [];
            HashSet<Guid> seen = [];
            Queue<Guid> queue = new([id]);

            while (queue.TryDequeue(out Guid current))
            {
                if (!seen.Add(current) || !_byId.ContainsKey(current))
                    continue;

                result.Add(current);

                foreach (FolderRow child in _children.GetValueOrDefault(current) ?? [])
                    queue.Enqueue(child.Id);
            }

            return result;
        }

        /// <summary>From the top level down to the folder itself.</summary>
        public List<FolderRow> PathTo(Guid id)
        {
            List<FolderRow> path = [];
            HashSet<Guid> seen = [];
            Guid? current = id;

            while (current is Guid c && _byId.TryGetValue(c, out FolderRow? row) && seen.Add(c))
            {
                path.Insert(0, row);
                current = row.ParentFolderId;
            }

            return path;
        }

        public string PathOf(Guid? folderId) =>
            folderId is Guid f ? string.Join(" / ", PathTo(f).Select(x => x.Name)) : string.Empty;
    }
}
