using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DTO;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
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
    private readonly ISecurityService _securityService;
    private readonly string _storageRoot;
    private readonly bool _isDevelopment;
    private readonly string? _bucketName;
    private readonly IAmazonS3? _s3Client;
    private static readonly string[] _audioExtensions = [".mp3", ".wav", ".flac", ".ogg", ".aac", ".m4a", ".wma"];

    public FileService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ISecurityService securityService,
        IWebHostEnvironment environment)
    {
        _dbFactory = dbFactory;
        _storageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lanyard", "UploadedFiles");
        _securityService = securityService;
        _isDevelopment = environment.IsDevelopment();

        if (_isDevelopment)
        {
            return;
        }

        string? endpointUrl = Environment.GetEnvironmentVariable("RAILWAY_BUCKET_ENDPOINT_URL");
        string? accessKey = Environment.GetEnvironmentVariable("RAILWAY_BUCKET_ACCESS_KEY_ID");
        string? secretKey = Environment.GetEnvironmentVariable("RAILWAY_BUCKET_SECRET_ACCESS_KEY");
        string? region = Environment.GetEnvironmentVariable("RAILWAY_BUCKET_REGION");

        if (string.IsNullOrWhiteSpace(endpointUrl))
            throw new InvalidOperationException("RAILWAY_BUCKET_ENDPOINT_URL is required in production.");

        if (string.IsNullOrWhiteSpace(accessKey))
            throw new InvalidOperationException("RAILWAY_BUCKET_ACCESS_KEY_ID is required in production.");

        if (string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("RAILWAY_BUCKET_SECRET_ACCESS_KEY is required in production.");

        _bucketName = Environment.GetEnvironmentVariable("RAILWAY_BUCKET_NAME");

        AmazonS3Config config = new()
        {
            ServiceURL = endpointUrl,
            ForcePathStyle = true,
            AuthenticationRegion = region ?? "auto",
            UseHttp = false
        };

        _s3Client = new AmazonS3Client(accessKey, secretKey, config);
    }

    private void EnsureS3Configured()
    {
        if (_s3Client == null || string.IsNullOrWhiteSpace(_bucketName))
            throw new InvalidOperationException("R2 storage is not configured.");
    }

    private static string BuildR2Key(Guid? folderId, Guid fileId, string fileName)
    {
        string prefix = folderId.HasValue ? folderId.Value.ToString() : "root";
        return $"{prefix}/{fileId}{fileName}";
    }

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
        Result<string> getResult = await _securityService.GetCurrentUserIdAsync();

        if (!getResult.IsSuccess || getResult.Data is null)
        {
            return Result<FileMetadata>.Fail(getResult.Error!);
        }

        return await UploadFileAsync(file, folderId, getResult.Data, cancellationToken);
    }

    public async Task<Result<FileMetadata>> UploadFileAsync(IFormFile file, Guid? folderId, string uploadedBy, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(file);
            if (string.IsNullOrWhiteSpace(uploadedBy))
                return Result<FileMetadata>.Fail("UploadedBy is required.");

            string fileName = Path.GetFileName(file.FileName);
            Guid fileId = Guid.NewGuid();
            string filePath;

            bool isAudio = _audioExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());
            (double durationSeconds, string albumName) = isAudio
                ? TryReadAudioMetadata(file, fileName)
                : (0, string.Empty);

            if (_isDevelopment)
            {
                string folderPath = folderId.HasValue
                    ? Path.Combine(_storageRoot, folderId.Value.ToString())
                    : _storageRoot;

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

                string key = BuildR2Key(folderId, fileId, fileName);

                PutObjectRequest putRequest = new()
                {
                    BucketName = _bucketName,
                    Key = key,
                    InputStream = file.OpenReadStream(),
                    ContentType = file.ContentType,
                    AutoCloseStream = true
                };

                await _s3Client!.PutObjectAsync(putRequest, cancellationToken);
                filePath = key;
            }

            ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            FileMetadata metadata = new()
            {
                Id = fileId,
                FileName = fileName,
                FilePath = filePath,
                FileSize = file.Length,
                ContentType = file.ContentType,
                UploadedAt = DateTime.UtcNow,
                UploadedBy = uploadedBy,
                FolderId = folderId,
                IsActive = true
            };

            db.FileMetadata.Add(metadata);

            // Audio uploads are also registered as songs so they show up in the music library.
            // The file itself lives wherever the upload landed (local disk in dev, Railway bucket in prod);
            // Song.FilePath mirrors FileMetadata.FilePath so MusicService/DownloadFileAsync can resolve it.
            if (isAudio)
            {
                Song song = new()
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

            return Result<FileMetadata>.Ok(metadata);
        }
        catch (Exception ex)
        {
            return Result<FileMetadata>.Fail($"Failed to upload file: {ex.Message}");
        }
    }

    public async Task<Result<FileMetadata>> RenameFileAsync(Guid fileId, string newName, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newName))
                return Result<FileMetadata>.Fail("New name is required.");

            ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            FileMetadata? file = await db.FileMetadata.FindAsync(new object[] { fileId }, cancellationToken);

            if (file == null)
                return Result<FileMetadata>.Fail("File not found.");

            file.FileName = newName;

            await db.SaveChangesAsync(cancellationToken);

            return Result<FileMetadata>.Ok(file);
        }
        catch (Exception ex)
        {
            return Result<FileMetadata>.Fail($"Failed to rename file: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteFileAsync(Guid fileId, CancellationToken cancellationToken)
    {
        try
        {
            ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            FileMetadata? file = await db.FileMetadata.FindAsync(new object[] { fileId }, cancellationToken);

            if (file == null)
                return Result<bool>.Fail("File not found.");

            if (_isDevelopment)
            {
                if (File.Exists(file.FilePath))
                    File.Delete(file.FilePath);
            }
            else
            {
                EnsureS3Configured();

                DeleteObjectRequest deleteRequest = new()
                {
                    BucketName = _bucketName,
                    Key = file.FilePath
                };

                await _s3Client!.DeleteObjectAsync(deleteRequest, cancellationToken);
            }

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

            await db.SaveChangesAsync(cancellationToken);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to delete file: {ex.Message}");
        }
    }

    public async Task<Result<FileMetadata>> GetFileMetadataAsync(Guid fileId, CancellationToken cancellationToken)
    {
        try
        {
            ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            FileMetadata? file = await db.FileMetadata.FindAsync(new object[] { fileId }, cancellationToken);

            if (file == null)
                return Result<FileMetadata>.Fail("File not found.");

            return Result<FileMetadata>.Ok(file);
        }
        catch (Exception ex)
        {
            return Result<FileMetadata>.Fail($"Failed to get file metadata: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<FileMetadata>>> ListFilesAsync(Guid? folderId, CancellationToken cancellationToken)
    {
        try
        {
            ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            List<FileMetadata> files = await db.FileMetadata
                .Where(f => !folderId.HasValue || f.FolderId == folderId)
                .ToListAsync(cancellationToken);

            return Result<IReadOnlyList<FileMetadata>>.Ok(files);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<FileMetadata>>.Fail($"Failed to list files: {ex.Message}");
        }
    }

    public async Task<Result<Folder>> CreateFolderAsync(string name, Guid? parentFolderId, string createdBy, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return Result<Folder>.Fail("Folder name is required.");

            ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

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

            if (_isDevelopment)
            {
                Directory.CreateDirectory(Path.Combine(_storageRoot, folder.Id.ToString()));
            }

            return Result<Folder>.Ok(folder);
        }
        catch (Exception ex)
        {
            return Result<Folder>.Fail($"Failed to create folder: {ex.Message}");
        }
    }

    public async Task<Result<Folder>> RenameFolderAsync(Guid folderId, string newName, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newName))
                return Result<Folder>.Fail("New name is required.");

            ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            Folder? folder = await db.Folders.FindAsync(new object[] { folderId }, cancellationToken);

            if (folder == null)
                return Result<Folder>.Fail("Folder not found.");

            folder.Name = newName;

            await db.SaveChangesAsync(cancellationToken);

            return Result<Folder>.Ok(folder);
        }
        catch (Exception ex)
        {
            return Result<Folder>.Fail($"Failed to rename folder: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteFolderAsync(Guid folderId, CancellationToken cancellationToken)
    {
        try
        {
            ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            Folder? folder = await db.Folders.FindAsync(new object[] { folderId }, cancellationToken);

            if (folder == null)
                return Result<bool>.Fail("Folder not found.");

            if (_isDevelopment)
            {
                db.Folders.Remove(folder);

                await db.SaveChangesAsync(cancellationToken);

                string folderPath = Path.Combine(_storageRoot, folderId.ToString());

                if (Directory.Exists(folderPath))
                    Directory.Delete(folderPath, true);
            }
            else
            {
                EnsureS3Configured();

                string prefix = folderId + "/";
                string? continuationToken = null;

                do
                {
                    ListObjectsV2Request listRequest = new()
                    {
                        BucketName = _bucketName,
                        Prefix = prefix,
                        ContinuationToken = continuationToken
                    };

                    ListObjectsV2Response listResponse = await _s3Client!.ListObjectsV2Async(listRequest, cancellationToken);

                    if (listResponse.S3Objects.Count > 0)
                    {
                        DeleteObjectsRequest deleteRequest = new()
                        {
                            BucketName = _bucketName,
                            Objects = listResponse.S3Objects.Select(o => new KeyVersion { Key = o.Key }).ToList()
                        };

                        await _s3Client.DeleteObjectsAsync(deleteRequest, cancellationToken);
                    }

                    continuationToken = listResponse.IsTruncated == true ? listResponse.NextContinuationToken : null;
                }
                while (!string.IsNullOrWhiteSpace(continuationToken));

                db.Folders.Remove(folder);

                await db.SaveChangesAsync(cancellationToken);
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to delete folder: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<Folder>>> ListFoldersAsync(Guid? parentFolderId, CancellationToken cancellationToken)
    {
        try
        {
            ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            List<Folder> folders = await db.Folders
                .Where(f => !parentFolderId.HasValue || f.ParentFolderId == parentFolderId)
                .ToListAsync(cancellationToken);

            return Result<IReadOnlyList<Folder>>.Ok(folders);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<Folder>>.Fail($"Failed to list folders: {ex.Message}");
        }
    }

    public async Task<Result<Stream>> DownloadFileAsync(Guid fileId, CancellationToken cancellationToken)
    {
        try
        {
            ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            FileMetadata? file = await db.FileMetadata.FindAsync(new object[] { fileId }, cancellationToken);

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
            return Result<Stream>.Fail($"Failed to download file: {ex.Message}");
        }
    }
}
