using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Email;

public interface IEmailService
{
    Task<Result<bool>> SendWelcomeEmailAsync(UserProfile user, string setPasswordUrl);
}
