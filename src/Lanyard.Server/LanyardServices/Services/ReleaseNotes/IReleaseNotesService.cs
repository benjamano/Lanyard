using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.DTO;

namespace Lanyard.Application.Services;

public interface IReleaseNotesService
{
    Task<Result<IEnumerable<ReleaseNote>>> GetReleaseNotesAsync();
}
