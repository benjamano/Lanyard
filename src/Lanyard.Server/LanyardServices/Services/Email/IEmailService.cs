using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;
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
    Task<Result<bool>> SendStaffDocumentExpiryReminderEmailAsync(UserProfile user, string documentTypeName, DateTime expiryDate, int daysBeforeExpiry, string? logoUrl, string accentColorHex);
    Task<Result<bool>> SendOnboardingWelcomeEmailAsync(UserProfile user, string subject, string bodyHtml, string? logoUrl, string accentColorHex, IReadOnlyList<EmailAttachment> attachments);

    // Staff scheduling. Lines and dates arrive pre-formatted (venue-local, UK style) so every
    // email reads exactly like the app.
    Task<Result<bool>> SendRotaChangedEmailAsync(UserProfile user, string locationName, IReadOnlyList<ShiftEmailLine> added, IReadOnlyList<ShiftEmailLine> changed, IReadOnlyList<ShiftEmailLine> removed, string myShiftsUrl, string? logoUrl, string accentColorHex);

    // dayLabel: "today", "tomorrow" or "on Mon 5 Oct".
    Task<Result<bool>> SendShiftReminderEmailAsync(UserProfile user, string locationName, ShiftEmailLine shift, string dayLabel, string myShiftsUrl, string? logoUrl, string accentColorHex);

    Task<Result<bool>> SendTimeOffRequestedEmailAsync(UserProfile manager, string requesterName, string typeName, DateOnly start, DateOnly end, string amount, string? notes, string approvalsUrl, string? logoUrl, string accentColorHex);

    Task<Result<bool>> SendTimeOffDecisionEmailAsync(UserProfile user, string typeName, DateOnly start, DateOnly end, TimeOffEmailOutcome outcome, string? reason, string? decidedByName, string myTimeOffUrl, string? logoUrl, string accentColorHex);
}
