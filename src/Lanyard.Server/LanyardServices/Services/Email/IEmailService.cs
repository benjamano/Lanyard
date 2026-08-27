using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Email;

public interface IEmailService
{
    Task<Result<bool>> SendSetPasswordEmailAsync(UserProfile user, string setPasswordUrl, string? logoUrl, string accentColorHex, string? locationName);
    Task<Result<bool>> SendCourseRecurrenceReminderEmailAsync(UserProfile user, string courseName, string trainingUrl, string? logoUrl, string accentColorHex);
    Task<Result<bool>> SendTwoFactorCodeEmailAsync(UserProfile user, string code);
    Task<Result<bool>> SendTrainingAssignedEmailAsync(UserProfile user, string courseName, DateTime? dueDate, string trainingUrl, string? logoUrl, string accentColorHex);
    Task<Result<bool>> SendTrainingDueSoonEmailAsync(UserProfile user, string courseName, DateTime dueDate, string trainingUrl, string? logoUrl, string accentColorHex);
    Task<Result<bool>> SendCourseCompletionCertificateEmailAsync(UserProfile user, string courseName, byte[] certificatePdf, string? logoUrl, string accentColorHex);
}
