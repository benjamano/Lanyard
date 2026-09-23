using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.StaffDocuments;

public class StaffDocumentService(
    IDbContextFactory<ApplicationDbContext> factory,
    IFileService fileService) : IStaffDocumentService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IFileService _fileService = fileService;

    public async Task<Result<StaffDocument>> UploadStaffDocumentAsync(string userId, Guid documentTypeId, IFormFile file, DateTime? expiryDate, DateTime? reminderDate, string uploadedByUserId, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            StaffDocumentType? documentType = await ctx.StaffDocumentTypes
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == documentTypeId && x.IsActive, cancellationToken);

            if (documentType is null)
            {
                return Result<StaffDocument>.Fail("Document type not found.");
            }

            if (documentType.RequiresExpiryDate)
            {
                if (expiryDate is null)
                {
                    return Result<StaffDocument>.Fail($"{documentType.Name} requires an expiry date.");
                }

                if (reminderDate is null)
                {
                    return Result<StaffDocument>.Fail($"{documentType.Name} requires a reminder date.");
                }

                if (reminderDate.Value.Date > expiryDate.Value.Date)
                {
                    return Result<StaffDocument>.Fail("The reminder date must be on or before the expiry date.");
                }
            }

            bool userExists = await ctx.Users.AnyAsync(x => x.Id == userId, cancellationToken);

            if (!userExists)
            {
                return Result<StaffDocument>.Fail("User not found.");
            }

            Result<FileMetadata> uploadResult = await _fileService.UploadFileAsync(file, null, uploadedByUserId, cancellationToken);

            if (!uploadResult.IsSuccess || uploadResult.Data is null)
            {
                return Result<StaffDocument>.Fail(uploadResult.Error ?? "Failed to upload file.");
            }

            StaffDocument document = new()
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                StaffDocumentTypeId = documentTypeId,
                FileMetadataId = uploadResult.Data.Id,
                ExpiryDate = expiryDate.HasValue ? DateTime.SpecifyKind(expiryDate.Value, DateTimeKind.Utc) : null,
                ReminderDate = reminderDate.HasValue ? DateTime.SpecifyKind(reminderDate.Value, DateTimeKind.Utc) : null,
                UploadedDate = DateTime.UtcNow,
                UploadedByUserId = uploadedByUserId,
                IsActive = true
            };

            ctx.StaffDocuments.Add(document);
            await ctx.SaveChangesAsync(cancellationToken);

            return Result<StaffDocument>.Ok(document);
        }
        catch (Exception ex)
        {
            return Result<StaffDocument>.Fail($"Failed to upload staff document: {ex.Message}");
        }
    }

    public async Task<Result<List<StaffDocument>>> GetStaffDocumentsForUserAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<StaffDocument> documents = await ctx.StaffDocuments
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.StaffDocumentType)
                .Include(x => x.FileMetadata)
                .Where(x => x.UserId == userId && x.IsActive)
                .OrderByDescending(x => x.UploadedDate)
                .ToListAsync();

            return Result<List<StaffDocument>>.Ok(documents);
        }
        catch (Exception ex)
        {
            return Result<List<StaffDocument>>.Fail($"Failed to retrieve staff documents: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteStaffDocumentAsync(Guid staffDocumentId, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            StaffDocument? document = await ctx.StaffDocuments.FirstOrDefaultAsync(x => x.Id == staffDocumentId, cancellationToken);

            if (document is null)
            {
                return Result<bool>.Fail("Document not found.");
            }

            document.IsActive = false;
            await ctx.SaveChangesAsync(cancellationToken);

            Result<bool> deleteFileResult = await _fileService.DeleteFileAsync(document.FileMetadataId, cancellationToken);

            if (!deleteFileResult.IsSuccess)
            {
                // The StaffDocument row is already deactivated (it must stop showing up for the
                // staff member/reminders regardless), so a storage-layer failure here is logged by
                // FileService itself and doesn't need to roll this back - matches SecurityService's
                // "auxiliary side effect must never block the primary state change" precedent.
                return Result<bool>.Ok(true);
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to delete staff document: {ex.Message}");
        }
    }

    public async Task<Result<List<StaffDocument>>> GetDocumentsWithPendingRemindersAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            DateTime utcNow = DateTime.UtcNow;

            List<StaffDocument> pending = await ctx.StaffDocuments
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.StaffDocumentType)
                .Where(x => x.IsActive
                    && x.ExpiryDate != null
                    && x.ReminderDate != null
                    && x.ReminderSentDate == null
                    && x.StaffDocumentType!.IsActive
                    && utcNow >= x.ReminderDate
                    && utcNow < x.ExpiryDate)
                .ToListAsync();

            return Result<List<StaffDocument>>.Ok(pending);
        }
        catch (Exception ex)
        {
            return Result<List<StaffDocument>>.Fail($"Failed to retrieve documents with pending reminders: {ex.Message}");
        }
    }

    public async Task<Result<bool>> MarkReminderSentAsync(Guid staffDocumentId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            StaffDocument? document = await ctx.StaffDocuments.FirstOrDefaultAsync(x => x.Id == staffDocumentId);

            if (document is null)
            {
                return Result<bool>.Fail("Document not found.");
            }

            if (document.ReminderSentDate is not null)
            {
                return Result<bool>.Fail("Reminder already sent.");
            }

            document.ReminderSentDate = DateTime.UtcNow;
            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to mark reminder as sent: {ex.Message}");
        }
    }
}
