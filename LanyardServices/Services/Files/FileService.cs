using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Http;
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
    private readonly SecurityService _securityService;
    private readonly string _storageRoot;

    public FileService(IDbContextFactory<ApplicationDbContext> dbFactory, SecurityService securityService)
    {
        _dbFactory = dbFactory;
        _storageRoot = Path.Combine(Directory.GetCurrentDirectory(), "UploadedFiles");
        _securityService = securityService;
    }

    public async Task<Result<FileMetadata>> UploadFileAsync(IFormFile file, Guid? folderId, CancellationToken cancellationToken)
    {
        string? uploadedByUserId = await _securityService.GetCurrentUserIdAsync();

        if (uploadedByUserId == null)
        {
            return Result<FileMetadata>.Fail("User is not logged in");
        }

        return await UploadFileAsync(file, folderId, uploadedByUserId, cancellationToken);
    }

    public async Task<Result<FileMetadata>> UploadFileAsync(IFormFile file, Guid? folderId, string uploadedBy, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(file);
            if (string.IsNullOrWhiteSpace(uploadedBy))
                return Result<FileMetadata>.Fail("UploadedBy is required.");

            string fileName = Path.GetFileName(file.FileName);
            string fileId = Guid.NewGuid().ToString();

            string folderPath = folderId.HasValue
                ? Path.Combine(_storageRoot, folderId.Value.ToString())
                : _storageRoot;

            Directory.CreateDirectory(folderPath);

            string filePath = Path.Combine(folderPath, fileId + "_" + fileName);

            using (FileStream stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            FileMetadata metadata = new()
            {
                Id = Guid.Parse(fileId),
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

            if (File.Exists(file.FilePath))
                File.Delete(file.FilePath);

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

            Directory.CreateDirectory(Path.Combine(_storageRoot, folder.Id.ToString()));

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

            db.Folders.Remove(folder);

            await db.SaveChangesAsync(cancellationToken);

            string folderPath = Path.Combine(_storageRoot, folderId.ToString());

            if (Directory.Exists(folderPath))
                Directory.Delete(folderPath, true);

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

            if (file == null || !File.Exists(file.FilePath))
                return Result<Stream>.Fail("File not found.");

            FileStream stream = new(file.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            return Result<Stream>.Ok(stream);
        }
        catch (Exception ex)
        {
            return Result<Stream>.Fail($"Failed to download file: {ex.Message}");
        }
    }
}
