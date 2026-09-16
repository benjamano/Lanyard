using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Http;

namespace Lanyard.Application.Services.StaffDocuments;

// A document whose expiry has entered exactly one configured reminder window and hasn't
// already had that specific interval's reminder sent for the document's current ExpiryDate.
public record PendingStaffDocumentReminder(StaffDocument Document, StaffDocumentReminderInterval Interval);

public interface IStaffDocumentService
{
    Task<Result<StaffDocument>> UploadStaffDocumentAsync(string userId, Guid documentTypeId, IFormFile file, DateTime? expiryDate, string uploadedByUserId, CancellationToken cancellationToken);
    Task<Result<List<StaffDocument>>> GetStaffDocumentsForUserAsync(string userId);
    Task<Result<bool>> DeleteStaffDocumentAsync(Guid staffDocumentId, CancellationToken cancellationToken);
    Task<Result<List<PendingStaffDocumentReminder>>> GetDocumentsWithPendingRemindersAsync();
    Task<Result<bool>> MarkReminderSentAsync(Guid staffDocumentId, Guid intervalId, DateTime expiryDateSnapshot);
}
