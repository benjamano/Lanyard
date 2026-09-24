using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;

namespace Lanyard.Application.Services.Scheduling;

public interface IClockInPinService
{
    // 4-6 digits. setByUserId is null when the user set it themselves.
    Task<Result<bool>> SetPinAsync(string userId, string pin, string? setByUserId);
    Task<Result<bool>> ClearPinAsync(string userId);
    Task<Result<ClockInPinStatus>> GetStatusAsync(string userId);

    // Ok(true) = matches, Ok(false) = wrong PIN or no PIN set. Failure only for unexpected errors,
    // so a caller can't distinguish "no PIN" from "wrong PIN" - deliberately, the terminal shows
    // the same message for both.
    Task<Result<bool>> VerifyPinAsync(string userId, string pin);
}
