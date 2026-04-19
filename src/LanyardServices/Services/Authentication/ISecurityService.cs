using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using System.Threading.Tasks;

namespace Lanyard.Application.Services.Authentication
{
    public interface ISecurityService
    {
        Task<Result<string>> GetCurrentUserIdAsync();
        Task<bool> IsUserLoggedIn();
        Task<Result<UserProfile>> GetCurrentUserProfileAsync();
        Task<string?> GetCurrentUserName();
        Task<IEnumerable<UserProfile>> GetAllUsersAsync();
        Task UpdateUserProfileAsync(UserProfile updatedUserProfile);
        Task<IEnumerable<UserProfile>> GetActiveUsersAsync();
        Task<Result<UserProfile>> CreateUserAsync(UserProfile user);
        Task<Result<bool>> DeleteUserAsync(string userId);
        Task<Result<bool>> ChangePasswordAsync(string userId, string newPassword);
    }
}
