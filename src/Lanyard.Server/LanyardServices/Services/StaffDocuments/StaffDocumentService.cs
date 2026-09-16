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

    public async Task<Result<StaffDocument>> UploadStaffDocumentAsync(string userId, Guid documentTypeId, IFormFile file, DateTime? expiryDate, string uploadedByUserId, CancellationToken cancellationToken)
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

            if (documentType.RequiresExpiryDate && expiryDate is null)
            {
                return Result<StaffDocument>.Fail($"{documentType.Name} requires an expiry date.");
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

    public async Task<Result<List<PendingStaffDocumentReminder>>> GetDocumentsWithPendingRemindersAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<StaffDocument> candidates = await ctx.StaffDocuments
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.StaffDocumentType!).ThenInclude(t => t.ReminderIntervals.Where(i => i.IsActive))
                .Where(x => x.IsActive && x.ExpiryDate != null && x.StaffDocumentType!.IsActive)
                .ToListAsync();

            if (candidates.Count == 0)
            {
                return Result<List<PendingStaffDocumentReminder>>.Ok([]);
            }

            List<Guid> candidateIds = [.. candidates.Select(x => x.Id)];

            HashSet<(Guid DocumentId, Guid IntervalId, DateTime ExpirySnapshot)> alreadySent = [.. (await ctx.StaffDocumentReminderSents
                .Where(x => candidateIds.Contains(x.StaffDocumentId))
                .Select(x => new { x.StaffDocumentId, x.ReminderIntervalId, x.ExpiryDateSnapshot })
                .ToListAsync())
                .Select(x => (x.StaffDocumentId, x.ReminderIntervalId, x.ExpiryDateSnapshot))];

            DateTime utcNow = DateTime.UtcNow;
            List<PendingStaffDocumentReminder> pending = [];

            foreach (StaffDocument document in candidates)
            {
                DateTime expiryDate = document.ExpiryDate!.Value;

                foreach (StaffDocumentReminderInterval interval in document.StaffDocumentType!.ReminderIntervals)
                {
                    DateTime reminderDate = expiryDate.AddDays(-interval.DaysBeforeExpiry);

                    if (utcNow < reminderDate || utcNow >= expiryDate)
                    {
                        continue;
                    }

                    if (alreadySent.Contains((document.Id, interval.Id, expiryDate)))
                    {
                        continue;
                    }

                    pending.Add(new PendingStaffDocumentReminder(document, interval));
                }
            }

            return Result<List<PendingStaffDocumentReminder>>.Ok(pending);
        }
        catch (Exception ex)
        {
            return Result<List<PendingStaffDocumentReminder>>.Fail($"Failed to retrieve documents with pending reminders: {ex.Message}");
        }
    }

    public async Task<Result<bool>> MarkReminderSentAsync(Guid staffDocumentId, Guid intervalId, DateTime expiryDateSnapshot)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ctx.StaffDocumentReminderSents.Add(new StaffDocumentReminderSent
            {
                Id = Guid.NewGuid(),
                StaffDocumentId = staffDocumentId,
                ReminderIntervalId = intervalId,
                ExpiryDateSnapshot = expiryDateSnapshot,
                SentDate = DateTime.UtcNow
            });

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to mark reminder as sent: {ex.Message}");
        }
    }
}
