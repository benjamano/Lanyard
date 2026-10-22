using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.StaffDocuments;

// Staff documents are right-to-work checks, ID and certificates, so their files are uploaded as
// restricted: the File Manager and api/files/download never see them (Security Issue 9.2). Who
// may see or change them is decided here - an Admin, a Manager of that person (the rule
// SchedulingAccess.CanManageUserAsync applies to PINs and positions), and for reading the file,
// the person themselves. The reminder sweep methods at the bottom run as the system and take no scope.
public class StaffDocumentService(
    IDbContextFactory<ApplicationDbContext> factory,
    IFileService fileService) : IStaffDocumentService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IFileService _fileService = fileService;

    public async Task<Result<StaffDocument>> UploadStaffDocumentAsync(LocationScope scope, string userId, Guid documentTypeId, IFormFile file, DateTime? expiryDate, DateTime? reminderDate, string uploadedByUserId, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            if (!await SchedulingAccess.CanManageUserAsync(ctx, scope, userId))
            {
                return Result<StaffDocument>.Fail("You can only upload documents for staff you manage.");
            }

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

            Result<FileMetadata> uploadResult = await _fileService.UploadFileAsync(file, null, uploadedByUserId,
                new FileUploadOptions(AddAudioToMusicLibrary: false, IsRestricted: true), cancellationToken);

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

            try
            {
                await ctx.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                // Nothing points at the file now, and a restricted file can't be found from the
                // File Manager to clean it up by hand.
                await _fileService.DeleteRestrictedFileAsync(uploadResult.Data.Id, CancellationToken.None);
                throw;
            }

            return Result<StaffDocument>.Ok(document);
        }
        catch (Exception ex)
        {
            return Result<StaffDocument>.Fail($"Failed to upload staff document: {ex.Message}");
        }
    }

    public async Task<Result<List<StaffDocument>>> GetStaffDocumentsForUserAsync(LocationScope scope, string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (!await SchedulingAccess.CanManageUserAsync(ctx, scope, userId))
            {
                return Result<List<StaffDocument>>.Fail("You can only see documents for staff you manage.");
            }

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

    public async Task<Result<FileContent>> OpenStaffDocumentFileAsync(Guid staffDocumentId, string callerUserId, LocationScope scope, FileByteRange? range, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            StaffDocument? document = await ctx.StaffDocuments
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == staffDocumentId && x.IsActive, cancellationToken);

            if (document is null)
            {
                return Result<FileContent>.Fail("Document not found.");
            }

            bool allowed = document.UserId == callerUserId || await SchedulingAccess.CanManageUserAsync(ctx, scope, document.UserId);

            if (!allowed)
            {
                // Same error as a missing document, so a guessed id tells the caller nothing.
                return Result<FileContent>.Fail("Document not found.");
            }

            return await _fileService.OpenRestrictedFileContentAsync(document.FileMetadataId, range, cancellationToken);
        }
        catch (Exception ex)
        {
            return Result<FileContent>.Fail($"Failed to open staff document: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteStaffDocumentAsync(LocationScope scope, Guid staffDocumentId, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            StaffDocument? document = await ctx.StaffDocuments.FirstOrDefaultAsync(x => x.Id == staffDocumentId, cancellationToken);

            if (document is null || !await SchedulingAccess.CanManageUserAsync(ctx, scope, document.UserId))
            {
                return Result<bool>.Fail("Document not found.");
            }

            document.IsActive = false;
            await ctx.SaveChangesAsync(cancellationToken);

            Result<bool> deleteFileResult = await _fileService.DeleteRestrictedFileAsync(document.FileMetadataId, cancellationToken);

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

            DateTime today = DateTime.UtcNow.Date;

            // Compared by calendar date, not exact instant - both dates come from date-only
            // pickers, so a document with ReminderDate == ExpiryDate must still fall inside the
            // window on that day rather than describing an empty (instant < same instant) range.
            List<StaffDocument> pending = await ctx.StaffDocuments
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.StaffDocumentType)
                .Where(x => x.IsActive
                    && x.ExpiryDate != null
                    && x.ReminderDate != null
                    && x.ReminderSentDate == null
                    && x.StaffDocumentType!.IsActive
                    && today >= x.ReminderDate!.Value.Date
                    && today <= x.ExpiryDate!.Value.Date)
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

            try
            {
                await ctx.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another sweep (or app instance) already claimed this reminder between our read
                // and our write - ReminderSentDate's [ConcurrencyCheck] turned the UPDATE's WHERE
                // clause into the atomic guard, and this is the losing side of that race.
                return Result<bool>.Fail("Reminder already sent.");
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to mark reminder as sent: {ex.Message}");
        }
    }
}
