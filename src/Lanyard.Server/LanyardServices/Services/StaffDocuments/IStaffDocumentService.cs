using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Http;

namespace Lanyard.Application.Services.StaffDocuments;

public interface IStaffDocumentService
{
    Task<Result<StaffDocument>> UploadStaffDocumentAsync(LocationScope scope, string userId, Guid documentTypeId, IFormFile file, DateTime? expiryDate, DateTime? reminderDate, string uploadedByUserId, CancellationToken cancellationToken);
    Task<Result<List<StaffDocument>>> GetStaffDocumentsForUserAsync(LocationScope scope, string userId);
    // The person themselves, an Admin, or a Manager of that person. Any refusal reads as "not found".
    Task<Result<FileContent>> OpenStaffDocumentFileAsync(Guid staffDocumentId, string callerUserId, LocationScope scope, FileByteRange? range, CancellationToken cancellationToken);
    Task<Result<bool>> DeleteStaffDocumentAsync(LocationScope scope, Guid staffDocumentId, CancellationToken cancellationToken);
    Task<Result<List<StaffDocument>>> GetDocumentsWithPendingRemindersAsync();
    Task<Result<bool>> MarkReminderSentAsync(Guid staffDocumentId);
}
