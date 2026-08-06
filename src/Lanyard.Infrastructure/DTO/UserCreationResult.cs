using Lanyard.Infrastructure.Models;

namespace Lanyard.Infrastructure.DTO
{
    public record UserCreationResult(UserProfile User, string GeneratedPassword);
}
