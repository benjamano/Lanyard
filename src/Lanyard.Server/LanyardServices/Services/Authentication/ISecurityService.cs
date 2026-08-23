using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using System.Threading.Tasks;

namespace Lanyard.Application.Services.Authentication
{
    public interface ISecurityService
    {
        Task<Result<string>> GetCurrentUserIdAsync();
        Task<bool> IsUserLoggedIn();
        Task<bool> IsCurrentUserInRoleAsync(string role);
        Task<Result<UserProfile>> GetCurrentUserProfileAsync();
        Task<string?> GetCurrentUserName();
        Task<IEnumerable<UserProfile>> GetAllUsersAsync();
        Task UpdateUserProfileAsync(UserProfile updatedUserProfile);
        Task<IEnumerable<UserProfile>> GetActiveUsersAsync();
        Task<Result<UserCreationResult>> CreateUserAsync(UserProfile user, List<int> locationIds);
        Task<Result<bool>> DeleteUserAsync(string userId);
        Task<Result<bool>> ChangePasswordAsync(string userId, string newPassword);
        Task<Result<bool>> SendSetPasswordLinkAsync(string userId);
        Task<Result<bool>> SetPasswordFromTokenAsync(string userId, string token, string newPassword);
        Task<Result<string>> GetUsernameForSetPasswordAsync(string userId);

        Task<Result<TwoFactorStatusDto>> GetTwoFactorStatusAsync();
        Task<Result<AuthenticatorEnrollmentDto>> BeginAuthenticatorEnrollmentAsync();
        Task<Result<List<string>>> ConfirmAuthenticatorEnrollmentAsync(string code);
        Task<Result<List<string>>> EnableEmailTwoFactorAsync();
        Task<Result<bool>> DisableTwoFactorAsync(string currentPassword);
        Task<Result<List<string>>> RegenerateRecoveryCodesAsync();
    }
}
