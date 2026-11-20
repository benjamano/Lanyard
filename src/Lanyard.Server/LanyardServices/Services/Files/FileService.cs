using Lanyard.Application.Services.Demo;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Files;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Application.Services.Authentication;

namespace Lanyard.Application.Services;


public class FileService : IFileService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IDemoGuard? _demoGuard;
    private readonly IFileUsageService? _usage;
    private readonly IFileLibraryEvents? _events;

    // Tells open Files pages in the company to refresh.
    private void Changed(int companyId) => _events?.Publish(companyId);
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ISongAnalysisQueue _analysisQueue;
    private readonly string _storageRoot;
    private readonly bool _isDevelopment;
    private readonly string? _bucketName;
    private readonly IAmazonS3? _s3Client;
    private readonly ILogger<FileService> _logger;
    private static readonly string[] _audioExtensions = [".mp3", ".wav", ".flac", ".ogg", ".aac", ".m4a", ".wma"];

    public const string RangeNotSatisfiableError = "Requested range not satisfiable.";

    // s3Client is the app-wide singleton registered in Program.cs outside Development (see
    // S3StorageClientFactory); it is optional so the Development path and the unit tests can
    // construct the service without one.
    public FileService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ICurrentUserAccessor currentUserAccessor,
        ISongAnalysisQueue analysisQueue,
        IWebHostEnvironment environment,
        IAmazonS3? s3Client = null,
        IDemoGuard? demoGuard = null,
        ILogger<FileService>? logger = null,
        IFileUsageService? usage = null,
        IFileLibraryEvents? events = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<FileService>.Instance;
        _demoGuard = demoGuard;
        _usage = usage;
        _events = events;
        _storageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lanyard", "UploadedFiles");
        _currentUserAccessor = currentUserAccessor;
        _analysisQueue = analysisQueue;
        _isDevelopment = environment.IsDevelopment();

        if (_isDevelopment)
        {
            return;
        }

        _bucketName = Environment.GetEnvironmentVariable("RAILWAY_BUCKET_NAME");
        _s3Client = s3Client ?? throw new InvalidOperationException("IAmazonS3 is not registered. Outside Development, Program.cs must register S3StorageClientFactory.CreateFromEnvironment().");
    }

    private void EnsureS3Configured()
    {
        if (_s3Client == null || string.IsNullOrWhiteSpace(_bucketName))
            throw new InvalidOperationException("R2 storage is not configured.");
    }

    // Where a new upload's bytes live. Deliberately not tied to the folder: a file can move
    // between folders (and in and out of the Recycle bin) without its bytes moving. Older uploads
    // were stored under "{folderId}/..." and keep the key saved in their FilePath.
    private static string BuildR2Key(Guid fileId, string fileName) => $"files/{fileId}{Path.GetExtension(fileName).ToLowerInvariant()}";

    // The names already used by live, browsable files in a folder, which a new name must avoid.
    private static Task<List<string>> TakenFileNamesAsync(ApplicationDbContext db, Guid? folderId, Guid? exceptFileId, CancellationToken cancellationToken) =>
        db.FileMetadata.AsNoTracking().TagWithCallSite()
            .Where(x => x.FolderId == folderId && x.IsActive && !x.IsRestricted && !x.IsSystemFile && x.Id != exceptFileId)
            .Select(x => x.FileName)
            .ToListAsync(cancellationToken);

    private static Task<List<string>> TakenFolderNamesAsync(ApplicationDbContext db, Guid? parentFolderId, Guid? exceptFolderId, CancellationToken cancellationToken) =>
        db.Folders.AsNoTracking().TagWithCallSite()
            .Where(x => x.ParentFolderId == parentFolderId && x.IsActive && x.Id != exceptFolderId)
            .Select(x => x.Name)
            .ToListAsync(cancellationToken);

    private static (double durationSeconds, string albumName) TryReadAudioMetadata(IFormFile file, string fileName)
    {
        try
        {
            using Stream stream = file.OpenReadStream();
            using TagLib.File tag = TagLib.File.Create(new StreamFileAbstraction(fileName, stream));

            double durationSeconds = tag.Properties.Duration.TotalSeconds;
            string albumName = tag.Tag.Album ?? string.Empty;

            return (durationSeconds, albumName);
        }
        catch
        {
            return (0, string.Empty);
        }
    }

    private sealed class StreamFileAbstraction : TagLib.File.IFileAbstraction
    {
        private readonly Stream _stream;

        public StreamFileAbstraction(string name, Stream stream)
        {
            Name = name;
            _stream = stream;
        }

        public string Name { get; }
        public Stream ReadStream => _stream;
        public Stream WriteStream => _stream;

        public void CloseStream(Stream stream)
        {
            // The caller owns the stream lifetime; do not close it here.
        }
    }

    public async Task<Result<FileMetadata>> UploadFileAsync(IFormFile file, Guid? folderId, CancellationToken cancellationToken)
    {
        Result<string> getResult = await _currentUserAccessor.GetCurrentUserIdAsync();

        if (!getResult.IsSuccess || getResult.Data is null)
        {
            return Result<FileMetadata>.Fail(getResult.Error!);
        }

        return await UploadFileAsync(file, folderId, getResult.Data, cancellationToken);
    }

    public Task<Result<FileMetadata>> UploadFileAsync(IFormFile file, Guid? folderId, string uploadedBy, CancellationToken cancellationToken) =>
        UploadFileAsync(file, folderId, uploadedBy, new FileUploadOptions(), cancellationToken);

    public async Task<Result<FileMetadata>> UploadFileAsync(IFormFile file, Guid? folderId, string uploadedBy, FileUploadOptions options, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(file);
            if (string.IsNullOrWhiteSpace(uploadedBy))
                return Result<FileMetadata>.Fail("UploadedBy is required.");

            // The demo is public: uploads would put strangers' files in the real storage bucket.
            if (_demoGuard is not null && await _demoGuard.IsDemoSessionAsync())
                return Result<FileMetadata>.Fail("Uploading files isn't available in the demo.");

            string fileName = Path.GetFileName(file.FileName);

            if (FileUploadPolicy.Check(fileName, file.Length) is string refusal)
                return Result<FileMetadata>.Fail(refusal);

            if (FileNameRules.Validate(fileName, "file name") is string badName)
                return Result<FileMetadata>.Fail(badName);

            string contentType = FileUploadPolicy.ContentTypeFor(fileName);

            // The tenant filter hides other companies' folders, so this also stops a folder id
            // from another company being used as the destination (and as the storage prefix).
            if (folderId.HasValue)
            {
                await using ApplicationDbContext folderCheck = await _dbFactory.CreateDbContextAsync(cancellationToken);

                // A folder in the Recycle bin can't take new files either.
                if (!await folderCheck.Folders.AnyAsync(x => x.Id == folderId.Value && x.IsActive, cancellationToken))
                    return Result<FileMetadata>.Fail("Folder not found.");
            }

            bool browsable = !options.IsRestricted && !options.IsSystemFile;

            // Uploads never fail over a name clash: "poster.png" becomes "poster (2).png".
            if (browsable)
            {
                await using ApplicationDbContext nameCheck = await _dbFactory.CreateDbContextAsync(cancellationToken);
                fileName = FileNameRules.MakeUnique(fileName, await TakenFileNamesAsync(nameCheck, folderId, null, cancellationToken));
            }

            Guid fileId = Guid.NewGuid();
            string filePath;

            bool isAudio = options.AddAudioToMusicLibrary && _audioExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());
            (double durationSeconds, string albumName) = isAudio
                ? TryReadAudioMetadata(file, fileName)
                : (0, string.Empty);

            if (_isDevelopment)
            {
                string folderPath = Path.Combine(_storageRoot, "files");

                Directory.CreateDirectory(folderPath);

                filePath = Path.Combine(folderPath, fileId + "_" + fileName);

                using (FileStream stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream, cancellationToken);
                }
            }
            else
            {
                EnsureS3Configured();

                string key = BuildR2Key(fileId, fileName);

                PutObjectRequest putRequest = new()
                {
                    BucketName = _bucketName,
                    Key = key,
                    InputStream = file.OpenReadStream(),
                    ContentType = contentType,
                    AutoCloseStream = true
                };

                await _s3Client!.PutObjectAsync(putRequest, cancellationToken);
                filePath = key;
            }

            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            // The grid's thumbnail: the picture itself, or the frame the browser took from a video.
            (string? thumbnailPath, int? sourceWidth, int? sourceHeight) = await StoreThumbnailForUploadAsync(fileId, file, contentType, options.Poster, cancellationToken);

            double? duration = options.Media?.DurationSeconds is > 0 ? options.Media.DurationSeconds : durationSeconds > 0 ? durationSeconds : null;

            FileMetadata metadata = new()
            {
                Id = fileId,
                FileName = fileName,
                FilePath = filePath,
                FileSize = file.Length,
                ContentType = contentType,
                UploadedAt = DateTime.UtcNow,
                UploadedBy = uploadedBy,
                FolderId = folderId,
                IsActive = true,
                IsRestricted = options.IsRestricted,
                IsSystemFile = options.IsSystemFile,
                Width = options.Media?.Width is > 0 ? options.Media.Width : sourceWidth,
                Height = options.Media?.Height is > 0 ? options.Media.Height : sourceHeight,
                VideoLength = duration is double seconds ? TimeSpan.FromSeconds(Math.Round(seconds, 1)) : null,
                ThumbnailPath = thumbnailPath
            };

            db.FileMetadata.Add(metadata);

            // Audio uploads are also registered as songs so they show up in the music library.
            // The file itself lives wherever the upload landed (local disk in dev, Railway bucket in prod);
            // Song.FilePath mirrors FileMetadata.FilePath so MusicService/DownloadFileAsync can resolve it.
            Song? song = null;

            if (isAudio)
            {
                song = new()
                {
                    Id = Guid.NewGuid(),
                    Name = Path.GetFileNameWithoutExtension(fileName),
                    AlbumName = albumName,
                    FilePath = filePath,
                    DurationSeconds = durationSeconds,
                    CreateDate = DateTime.UtcNow,
                    IsDownloaded = true,
                    IsActive = true,
                    FileMetadataId = fileId
                };

                db.Songs.Add(song);
            }

            await db.SaveChangesAsync(cancellationToken);

            // The name was made unique before the bytes went up; another upload of the same name
            // into the same folder (a second tab) may have landed meanwhile. Check again now both
            // rows exist, and the later one takes the next number.
            if (browsable)
            {
                string lowered = metadata.FileName.ToLower();
                bool clash = await db.FileMetadata.AsNoTracking()
                    .AnyAsync(x => x.FolderId == folderId && x.IsActive && !x.IsRestricted && !x.IsSystemFile
                        && x.Id != metadata.Id && x.FileName.ToLower() == lowered && x.UploadedAt <= metadata.UploadedAt, cancellationToken);

                if (clash)
                {
                    metadata.FileName = FileNameRules.MakeUnique(metadata.FileName, await TakenFileNamesAsync(db, folderId, metadata.Id, cancellationToken));
                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            // Queue BPM analysis only after the row is committed - the background
            // worker loads the song by ID from a fresh context.
            if (song != null)
            {
                _analysisQueue.Enqueue(song.Id);
            }

            if (browsable)
                Changed(metadata.CompanyId);

            return Result<FileMetadata>.Ok(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload file");
            return Result<FileMetadata>.Fail("Failed to upload file.");
        }
    }

    public async Task<Result<FileMetadata>> RenameFileAsync(Guid fileId, string newName, CancellationToken cancellationToken)
    {
        try
        {
            if (FileNameRules.Validate(newName, "file name") is string badName)
                return Result<FileMetadata>.Fail(badName);

            newName = FileNameRules.Clean(newName);

            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            FileMetadata? file = await db.FileMetadata.FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);

            if (file == null || file.IsRestricted || !file.IsActive)
                return Result<FileMetadata>.Fail("File not found.");

            if (!file.IsSystemFile && (await TakenFileNamesAsync(db, file.FolderId, file.Id, cancellationToken)).Any(x => FileNameRules.SameName(x, newName)))
                return Result<FileMetadata>.Fail($"There's already a file called \"{newName}\" here.");

            file.FileName = newName;

            await db.SaveChangesAsync(cancellationToken);
            Changed(file.CompanyId);

            return Result<FileMetadata>.Ok(file);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename file");
            return Result<FileMetadata>.Fail("Failed to rename file.");
        }
    }

    public Task<Result<bool>> DeleteFileAsync(Guid fileId, CancellationToken cancellationToken) =>
        DeleteFileCoreAsync(fileId, restricted: false, cancellationToken);

    public Task<Result<bool>> DeleteRestrictedFileAsync(Guid fileId, CancellationToken cancellationToken) =>
        DeleteFileCoreAsync(fileId, restricted: true, cancellationToken);

    private async Task<Result<bool>> DeleteFileCoreAsync(Guid fileId, bool restricted, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            FileMetadata? file = await db.FileMetadata.FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);

            if (file == null || file.IsRestricted != restricted)
                return Result<bool>.Fail("File not found.");

            // Checked up front for a clear message rather than leaving it to the FK's Restrict
            // behaviour at SaveChangesAsync.
            bool isReferenced = await db.StaffDocuments.AnyAsync(x => x.FileMetadataId == file.Id && x.IsActive, cancellationToken)
                || await db.CompanyOnboardingStandingAttachments.AnyAsync(x => x.FileMetadataId == file.Id && x.IsActive, cancellationToken);

            if (isReferenced)
                return Result<bool>.Fail("This file is attached to a staff document or an onboarding email and cannot be deleted.");

            if (await db.AudioClips.AnyAsync(x => x.FileMetadataId == file.Id && x.DeleteDate == null, cancellationToken))
                return Result<bool>.Fail("This file is an audio clip. Delete the clip first.");

            // Deleted clips keep their play history, so they stay; they just let go of the file.
            List<AudioClip> deletedClips = await db.AudioClips
                .Where(x => x.FileMetadataId == file.Id)
                .ToListAsync(cancellationToken);

            foreach (AudioClip deletedClip in deletedClips)
            {
                deletedClip.FileMetadataId = null;
            }

            // StaffDocument/CompanyOnboardingStandingAttachment are soft-delete models, so an
            // inactive row is never actually removed from the table - it would otherwise sit
            // there permanently holding the FK's Restrict behavior against this file, blocking
            // the delete below even though the row is no longer active. Once the file itself is
            // being deleted, a stale reference to it has no further use, so these are hard-deleted
            // here rather than left as orphaned rows pointing at nothing.
            List<StaffDocument> inactiveStaffDocuments = await db.StaffDocuments
                .Where(x => x.FileMetadataId == file.Id && !x.IsActive)
                .ToListAsync(cancellationToken);
            db.StaffDocuments.RemoveRange(inactiveStaffDocuments);

            List<CompanyOnboardingStandingAttachment> inactiveStandingAttachments = await db.CompanyOnboardingStandingAttachments
                .Where(x => x.FileMetadataId == file.Id && !x.IsActive)
                .ToListAsync(cancellationToken);
            db.CompanyOnboardingStandingAttachments.RemoveRange(inactiveStandingAttachments);

            // Retire the song backed by this file (if any) so it drops out of the library.
            // Done before the file row is removed; the FK is configured to null on delete.
            List<Song> songs = await db.Songs
                .Where(s => s.FileMetadataId == file.Id && s.IsActive)
                .ToListAsync(cancellationToken);

            foreach (Song song in songs)
            {
                song.IsActive = false;
            }

            db.FileMetadata.Remove(file);

            // The rows go first and the bytes after (Security Issue 9.4). If the save fails -
            // a reference added meanwhile, or a write the tenant check refuses - nothing has
            // been lost; if the storage delete fails afterwards, the worst case is unreferenced
            // bytes, which is logged, never a row pointing at a file that no longer exists.
            await db.SaveChangesAsync(cancellationToken);

            await DeleteStoredFileAsync(file.FilePath);

            if (!string.IsNullOrEmpty(file.ThumbnailPath))
                await DeleteStoredFileAsync(file.ThumbnailPath);

            if (!file.IsRestricted && !file.IsSystemFile)
                Changed(file.CompanyId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete file");
            return Result<bool>.Fail("Failed to delete file.");
        }
    }

    private async Task DeleteStoredFileAsync(string filePath)
    {
        try
        {
            if (_isDevelopment)
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);

                return;
            }

            EnsureS3Configured();

            await _s3Client!.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucketName, Key = filePath }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deleted the file row but couldn't remove {FilePath} from storage; the bytes are left unreferenced", filePath);
        }
    }

    public async Task<Result<FileMetadata>> GetFileMetadataAsync(Guid fileId, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            FileMetadata? file = await db.FileMetadata
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(f => f.Id == fileId && !f.IsRestricted, cancellationToken);

            if (file == null)
                return Result<FileMetadata>.Fail("File not found.");

            return Result<FileMetadata>.Ok(file);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get file metadata");
            return Result<FileMetadata>.Fail("Failed to get file metadata.");
        }
    }

    public async Task<Result<IReadOnlyList<FileMetadata>>> ListFilesAsync(Guid? folderId, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            // null means the root folder, not "everything": the old predicate returned every
            // file in every folder for the root listing (and tracked them all).
            List<FileMetadata> files = await db.FileMetadata
                .AsNoTracking()
                .TagWithCallSite()
                .Where(f => f.FolderId == folderId && f.IsActive && !f.IsRestricted && !f.IsSystemFile)
                .OrderBy(f => f.FileName)
                .ToListAsync(cancellationToken);

            return Result<IReadOnlyList<FileMetadata>>.Ok(files);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list files");
            return Result<IReadOnlyList<FileMetadata>>.Fail("Failed to list files.");
        }
    }

    public async Task<Result<Folder>> CreateFolderAsync(string name, Guid? parentFolderId, CancellationToken cancellationToken)
    {
        Result<string> getResult = await _currentUserAccessor.GetCurrentUserIdAsync();

        if (!getResult.IsSuccess || getResult.Data is null)
        {
            return Result<Folder>.Fail(getResult.Error!);
        }

        return await CreateFolderAsync(name, parentFolderId, getResult.Data, cancellationToken);
    }

    public async Task<Result<Folder>> CreateFolderAsync(string name, Guid? parentFolderId, string createdBy, CancellationToken cancellationToken)
    {
        try
        {
            if (FileNameRules.Validate(name, "folder name") is string badName)
                return Result<Folder>.Fail(badName);

            name = FileNameRules.Clean(name);

            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            if (parentFolderId.HasValue && !await db.Folders.AnyAsync(x => x.Id == parentFolderId.Value && x.IsActive, cancellationToken))
                return Result<Folder>.Fail("Parent folder not found.");

            if ((await TakenFolderNamesAsync(db, parentFolderId, null, cancellationToken)).Any(x => FileNameRules.SameName(x, name)))
                return Result<Folder>.Fail($"There's already a folder called \"{name}\" here.");

            Folder folder = new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                ParentFolderId = parentFolderId,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = createdBy,
                IsActive = true
            };

            db.Folders.Add(folder);

            await db.SaveChangesAsync(cancellationToken);
            Changed(folder.CompanyId);

            return Result<Folder>.Ok(folder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create folder");
            return Result<Folder>.Fail("Failed to create folder.");
        }
    }

    public async Task<Result<Folder>> RenameFolderAsync(Guid folderId, string newName, CancellationToken cancellationToken)
    {
        try
        {
            if (FileNameRules.Validate(newName, "folder name") is string badName)
                return Result<Folder>.Fail(badName);

            newName = FileNameRules.Clean(newName);

            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            Folder? folder = await db.Folders.FirstOrDefaultAsync(x => x.Id == folderId && x.IsActive, cancellationToken);

            if (folder == null)
                return Result<Folder>.Fail("Folder not found.");

            if ((await TakenFolderNamesAsync(db, folder.ParentFolderId, folder.Id, cancellationToken)).Any(x => FileNameRules.SameName(x, newName)))
                return Result<Folder>.Fail($"There's already a folder called \"{newName}\" here.");

            folder.Name = newName;

            await db.SaveChangesAsync(cancellationToken);
            Changed(folder.CompanyId);

            return Result<Folder>.Ok(folder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename folder");
            return Result<Folder>.Fail("Failed to rename folder.");
        }
    }

    // Only an empty folder can be deleted. Its storage prefix is never wiped: moving a file
    // changes its FolderId but not where its bytes live, so files moved out of this folder still
    // sit under its prefix, and deleting the prefix used to destroy them (Security Issue 9.4).
    public async Task<Result<bool>> DeleteFolderAsync(Guid folderId, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            Folder? folder = await db.Folders.FirstOrDefaultAsync(x => x.Id == folderId, cancellationToken);

            if (folder == null)
                return Result<bool>.Fail("Folder not found.");

            bool hasContents = await db.FileMetadata.AnyAsync(x => x.FolderId == folderId, cancellationToken)
                || await db.Folders.AnyAsync(x => x.ParentFolderId == folderId, cancellationToken);

            if (hasContents)
                return Result<bool>.Fail("Only an empty folder can be deleted. Move or delete what's in it first.");

            db.Folders.Remove(folder);

            await db.SaveChangesAsync(cancellationToken);

            if (_isDevelopment)
            {
                string folderPath = Path.Combine(_storageRoot, folderId.ToString());

                // Not recursive, for the same reason as above.
                if (Directory.Exists(folderPath) && !Directory.EnumerateFileSystemEntries(folderPath).Any())
                    Directory.Delete(folderPath);
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete folder {FolderId}", folderId);
            return Result<bool>.Fail("Failed to delete folder.");
        }
    }

    public async Task<Result<IReadOnlyList<Folder>>> ListFoldersAsync(Guid? parentFolderId, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            // null means top-level folders only, for the same reason as ListFilesAsync.
            List<Folder> folders = await db.Folders
                .AsNoTracking()
                .TagWithCallSite()
                .Where(f => f.ParentFolderId == parentFolderId && f.IsActive)
                .OrderBy(f => f.Name)
                .ToListAsync(cancellationToken);

            return Result<IReadOnlyList<Folder>>.Ok(folders);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list folders");
            return Result<IReadOnlyList<Folder>>.Fail("Failed to list folders.");
        }
    }

    public async Task<Result<Folder>> GetFolderAsync(Guid folderId, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            Folder? folder = await db.Folders
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(f => f.Id == folderId && f.IsActive, cancellationToken);

            if (folder == null)
                return Result<Folder>.Fail("Folder not found.");

            return Result<Folder>.Ok(folder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get folder");
            return Result<Folder>.Fail("Failed to get folder.");
        }
    }

    public async Task<Result<Stream>> DownloadFileAsync(Guid fileId, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            FileMetadata? file = await db.FileMetadata
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(f => f.Id == fileId && !f.IsRestricted, cancellationToken);

            if (file != null && await IsDeletedAndUnusedAsync(db, file, cancellationToken))
                file = null;

            if (_isDevelopment)
            {
                if (file == null || !File.Exists(file.FilePath))
                    return Result<Stream>.Fail("File not found.");

                FileStream stream = new(file.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

                return Result<Stream>.Ok(stream);
            }

            if (file == null)
                return Result<Stream>.Fail("File not found.");

            EnsureS3Configured();

            GetObjectRequest request = new()
            {
                BucketName = _bucketName,
                Key = file.FilePath
            };

            GetObjectResponse response = await _s3Client!.GetObjectAsync(request, cancellationToken);

            return Result<Stream>.Ok(response.ResponseStream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file");
            return Result<Stream>.Fail("Failed to download file.");
        }
    }

    public Task<Result<FileContent>> OpenFileContentAsync(Guid fileId, FileByteRange? range, CancellationToken cancellationToken) =>
        OpenFileContentCoreAsync(fileId, range, restricted: false, cancellationToken);

    public Task<Result<FileContent>> OpenRestrictedFileContentAsync(Guid fileId, FileByteRange? range, CancellationToken cancellationToken) =>
        OpenFileContentCoreAsync(fileId, range, restricted: true, cancellationToken);

    private async Task<Result<FileContent>> OpenFileContentCoreAsync(Guid fileId, FileByteRange? range, bool restricted, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            FileMetadata? file = await db.FileMetadata
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(f => f.Id == fileId && f.IsRestricted == restricted, cancellationToken);

            if (file == null || await IsDeletedAndUnusedAsync(db, file, cancellationToken))
                return Result<FileContent>.Fail("File not found.");

            if (_isDevelopment)
            {
                if (!File.Exists(file.FilePath))
                    return Result<FileContent>.Fail("File not found.");

                // Seekable, so the HTTP layer's own range processing serves any Range request.
                FileStream stream = new(file.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);

                return Result<FileContent>.Ok(new FileContent
                {
                    Stream = stream,
                    Metadata = file,
                    TotalLength = stream.Length
                });
            }

            EnsureS3Configured();

            GetObjectRequest request = new()
            {
                BucketName = _bucketName,
                Key = file.FilePath
            };

            // The bucket stream can't seek, so the only way to honour a Range request is to ask
            // the bucket for exactly those bytes. Without this every seek in a video or song
            // re-downloaded the entire object through the server.
            if (range is FileByteRange requested)
            {
                request.ByteRange = requested.End.HasValue
                    ? new ByteRange(requested.Start, requested.End.Value)
                    : new ByteRange($"bytes={requested.Start}-");
            }

            GetObjectResponse response = await _s3Client!.GetObjectAsync(request, cancellationToken);

            (long totalLength, long? rangeStart, long? rangeEnd) = ParseContentRange(response.ContentRange, response.ContentLength, file.FileSize);

            return Result<FileContent>.Ok(new FileContent
            {
                Stream = response.ResponseStream,
                Metadata = file,
                TotalLength = totalLength,
                RangeStart = rangeStart,
                RangeEnd = rangeEnd,
                Owner = response
            });
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return Result<FileContent>.Fail(RangeNotSatisfiableError);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open file");
            return Result<FileContent>.Fail("Failed to open file.");
        }
    }

    // A file in the Recycle bin (or inside a folder that is) is still served while something uses
    // it, so the logo or a projection video doesn't vanish mid-show. Otherwise deleting it means
    // it can't be opened any more, even by someone who kept the link.
    private async Task<bool> IsDeletedAndUnusedAsync(ApplicationDbContext db, FileMetadata file, CancellationToken cancellationToken)
    {
        if (_usage is null)
            return false;

        bool binned = !file.IsActive;
        Guid? folderId = file.FolderId;

        for (int depth = 0; !binned && folderId is Guid current && depth < 64; depth++)
        {
            var folder = await db.Folders.AsNoTracking().TagWithCallSite()
                .Where(x => x.Id == current)
                .Select(x => new { x.IsActive, x.ParentFolderId })
                .FirstOrDefaultAsync(cancellationToken);

            if (folder is null)
                break;

            binned = !folder.IsActive;
            folderId = folder.ParentFolderId;
        }

        if (!binned)
            return false;

        Result<IReadOnlyDictionary<Guid, IReadOnlyList<FileUsage>>> usages = await _usage.GetUsagesAsync([file.Id], cancellationToken);

        // If the check itself fails, keep serving rather than break something that uses it.
        return usages.IsSuccess && usages.Data is not null && !usages.Data.ContainsKey(file.Id);
    }

    // ---------- Thumbnails ----------

    private static string ThumbnailKey(Guid fileId) => $"thumbs/{fileId}.webp";

    private async Task<(string? Path, int? Width, int? Height)> StoreThumbnailForUploadAsync(Guid fileId, IFormFile file, string contentType, IFormFile? poster, CancellationToken cancellationToken)
    {
        try
        {
            RenderedThumbnail? rendered = null;

            if (contentType.StartsWith("image/", StringComparison.Ordinal) && contentType != "image/svg+xml")
            {
                await using Stream source = file.OpenReadStream();
                rendered = ThumbnailRenderer.Render(source);
            }
            else if (contentType.StartsWith("video/", StringComparison.Ordinal) && poster is { Length: > 0 and < 5 * 1024 * 1024 })
            {
                await using Stream source = poster.OpenReadStream();
                rendered = ThumbnailRenderer.Render(source);
            }

            if (rendered is null)
                return (contentType.StartsWith("image/", StringComparison.Ordinal) ? string.Empty : null, null, null);

            string path = await WriteObjectAsync(ThumbnailKey(fileId), new MemoryStream(rendered.WebP), "image/webp", cancellationToken);

            // A poster frame's size is the video's size.
            return (path, rendered.SourceWidth, rendered.SourceHeight);
        }
        catch (Exception ex)
        {
            // A missing thumbnail only means Files shows an icon; never fail the upload over it.
            _logger.LogWarning(ex, "Couldn't make a thumbnail for {FileId}", fileId);
            return (null, null, null);
        }
    }

    // Writes bytes that aren't a library file in their own right (a thumbnail) and returns where they went.
    private async Task<string> WriteObjectAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        if (_isDevelopment)
        {
            string path = Path.Combine(_storageRoot, key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using FileStream stream = new(path, FileMode.Create);
            await content.CopyToAsync(stream, cancellationToken);
            return path;
        }

        EnsureS3Configured();

        await _s3Client!.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = true
        }, cancellationToken);

        return key;
    }

    public async Task<Result<Stream>> OpenThumbnailAsync(Guid fileId, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            FileMetadata? file = await db.FileMetadata.AsNoTracking().TagWithCallSite()
                .FirstOrDefaultAsync(f => f.Id == fileId && !f.IsRestricted, cancellationToken);

            string? path = file?.ThumbnailPath;

            if (file is null || string.IsNullOrEmpty(path) || await IsDeletedAndUnusedAsync(db, file, cancellationToken))
                return Result<Stream>.Fail("No thumbnail.");

            if (_isDevelopment)
            {
                return File.Exists(path)
                    ? Result<Stream>.Ok(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    : Result<Stream>.Fail("No thumbnail.");
            }

            EnsureS3Configured();

            GetObjectResponse response = await _s3Client!.GetObjectAsync(new GetObjectRequest { BucketName = _bucketName, Key = path }, cancellationToken);
            return Result<Stream>.Ok(response.ResponseStream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't open the thumbnail for {FileId}", fileId);
            return Result<Stream>.Fail("No thumbnail.");
        }
    }

    public async Task<Result<bool>> CreateMissingThumbnailAsync(Guid fileId, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            FileMetadata? file = await db.FileMetadata.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);

            if (file is null || file.ThumbnailPath is not null)
                return Result<bool>.Ok(false);

            RenderedThumbnail? rendered = null;

            if (file.ContentType?.StartsWith("image/", StringComparison.Ordinal) == true && file.ContentType != "image/svg+xml")
            {
                Result<FileContent> content = await OpenFileContentAsync(fileId, null, cancellationToken);

                if (content.IsSuccess && content.Data is not null)
                {
                    await using FileContent opened = content.Data;

                    // SKCodec wants to seek; the bucket stream can't.
                    await using MemoryStream buffer = new();
                    await opened.Stream.CopyToAsync(buffer, cancellationToken);
                    buffer.Position = 0;
                    rendered = ThumbnailRenderer.Render(buffer);
                }
            }

            if (rendered is null)
            {
                file.ThumbnailPath = string.Empty;
            }
            else
            {
                file.ThumbnailPath = await WriteObjectAsync(ThumbnailKey(fileId), new MemoryStream(rendered.WebP), "image/webp", cancellationToken);
                file.Width ??= rendered.SourceWidth;
                file.Height ??= rendered.SourceHeight;
            }

            await db.SaveChangesAsync(cancellationToken);
            return Result<bool>.Ok(rendered is not null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't make a thumbnail for {FileId}", fileId);
            return Result<bool>.Fail("Couldn't make a thumbnail.");
        }
    }

    /// <summary>
    /// Reads an S3 Content-Range header ("bytes 0-99/1000"). A full-object response has no
    /// Content-Range, in which case the object's length comes from the response (or the
    /// metadata row as a last resort) and no range is reported.
    /// </summary>
    public static (long totalLength, long? rangeStart, long? rangeEnd) ParseContentRange(string? contentRange, long? contentLength, long fallbackLength)
    {
        if (!string.IsNullOrWhiteSpace(contentRange)
            && System.Net.Http.Headers.ContentRangeHeaderValue.TryParse(contentRange, out System.Net.Http.Headers.ContentRangeHeaderValue? parsed)
            && parsed.HasRange)
        {
            long total = parsed.HasLength ? parsed.Length!.Value : fallbackLength;
            return (total, parsed.From, parsed.To);
        }

        return (contentLength is > 0 ? contentLength.Value : fallbackLength, null, null);
    }

    public async Task<Result<FileMetadata>> MoveFileAsync(Guid fileId, Guid? destinationFolderId, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            FileMetadata? file = await db.FileMetadata.FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);

            if (file == null || file.IsRestricted || !file.IsActive)
                return Result<FileMetadata>.Fail("File not found.");

            if (file.FolderId == destinationFolderId)
                return Result<FileMetadata>.Ok(file);

            if (destinationFolderId.HasValue)
            {
                Folder? destinationFolder = await db.Folders.FirstOrDefaultAsync(x => x.Id == destinationFolderId.Value && x.IsActive, cancellationToken);

                if (destinationFolder == null)
                    return Result<FileMetadata>.Fail("Destination folder not found.");
            }

            if (!file.IsSystemFile && (await TakenFileNamesAsync(db, destinationFolderId, file.Id, cancellationToken)).Any(x => FileNameRules.SameName(x, file.FileName)))
                return Result<FileMetadata>.Fail($"There's already a file called \"{file.FileName}\" there.");

            file.FolderId = destinationFolderId;

            await db.SaveChangesAsync(cancellationToken);
            Changed(file.CompanyId);

            return Result<FileMetadata>.Ok(file);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move file");
            return Result<FileMetadata>.Fail("Failed to move file.");
        }
    }
}
