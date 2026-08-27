using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Gdpr;

public interface IGdprService
{
    Task<Result<bool>> EraseUserDataAsync(string userId);
}
