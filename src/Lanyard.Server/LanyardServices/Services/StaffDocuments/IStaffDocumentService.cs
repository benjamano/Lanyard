using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Http;

namespace Lanyard.Application.Services.StaffDocuments;

public interface IStaffDocumentService
{
    Task<Result<StaffDocument>> UploadStaffDocumentAsync(string userId, Guid documentTypeId, IFormFile file, DateTime? expiryDate, DateTime? reminderDate, string uploadedByUserId, CancellationToken cancellationToken);
    Task<Result<List<StaffDocument>>> GetStaffDocumentsForUserAsync(string userId);
    Task<Result<bool>> DeleteStaffDocumentAsync(Guid staffDocumentId, CancellationToken cancellationToken);
    Task<Result<List<StaffDocument>>> GetDocumentsWithPendingRemindersAsync();
    Task<Result<bool>> MarkReminderSentAsync(Guid staffDocumentId);
}
