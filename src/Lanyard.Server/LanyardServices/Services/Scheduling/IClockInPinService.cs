using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;

namespace Lanyard.Application.Services.Scheduling;

public interface IClockInPinService
{
    // Self-service: the signed-in user changing their own PIN (the page supplies their id).
    Task<Result<bool>> SetOwnPinAsync(string userId, string pin);
    Task<Result<bool>> ClearOwnPinAsync(string userId);

    // A manager setting someone else's PIN from the user editor. Scope-checked: the target must
    // be a member of the manager's company. Recorded as SetByUserId so "who set this" is known.
    Task<Result<bool>> SetPinForUserAsync(LocationScope scope, string userId, string pin, string setByUserId);
    Task<Result<bool>> ClearPinForUserAsync(LocationScope scope, string userId);

    Task<Result<ClockInPinStatus>> GetStatusAsync(string userId);

    // Ok(true) = matches, Ok(false) = wrong PIN or no PIN set. Failure only for unexpected errors,
    // so a caller can't distinguish "no PIN" from "wrong PIN" - deliberately, the terminal shows
    // the same message for both.
    Task<Result<bool>> VerifyPinAsync(string userId, string pin);
}
