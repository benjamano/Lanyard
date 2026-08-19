using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Email;

public interface IEmailService
{
    Task<Result<bool>> SendSetPasswordEmailAsync(UserProfile user, string setPasswordUrl, string? logoUrl, string accentColorHex, string? locationName);
    Task<Result<bool>> SendCourseRecurrenceReminderEmailAsync(UserProfile user, string courseName, string trainingUrl, string? logoUrl, string accentColorHex);
}
