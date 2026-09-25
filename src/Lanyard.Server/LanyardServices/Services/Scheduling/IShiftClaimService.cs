using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// Open shifts, call-offs and swaps: staff asking to change who works a shift, and managers
// deciding. Whether pick-ups and swaps wait for a manager is set per location
// (LocationSchedulingSettings.ClaimsNeedApproval); calling off always does.
public interface IShiftClaimService
{
    // ---- Staff -----------------------------------------------------------------------------

    // Open shifts and colleagues' swap requests the person could act on, plus their own open requests.
    Task<Result<ShiftMarketplace>> GetMarketplaceAsync(string userId);

    // The person's own requests that are still open (to mark their shifts on My Shifts).
    Task<Result<List<ShiftClaim>>> GetOpenClaimsForUserAsync(string userId);

    // Asks for an open shift. Returns the claim: Approved when the location doesn't need a
    // manager (the shift is theirs now), Pending otherwise.
    Task<Result<ShiftClaim>> PickUpAsync(string userId, Guid shiftId, string? note);

    // "I can't make it" on the person's own published shift. Reason required; a manager decides.
    Task<Result<ShiftClaim>> RequestDropAsync(string userId, Guid shiftId, string reason);

    // Asks colleagues for swap offers on the person's own published shift.
    Task<Result<ShiftClaim>> RequestSwapAsync(string userId, Guid shiftId, string? note);

    // Answers a colleague's swap request by offering one of the person's own shifts.
    Task<Result<ShiftClaim>> OfferSwapAsync(string userId, Guid swapClaimId, Guid myShiftId);

    // The requester picks an offer. The swap happens now, or waits for a manager.
    Task<Result<ShiftClaim>> AcceptOfferAsync(string userId, Guid offerClaimId);

    // Takes back one of the person's own open requests or offers.
    Task<Result<bool>> WithdrawAsync(string userId, Guid claimId);

    // ---- Managers --------------------------------------------------------------------------

    Task<Result<ShiftClaimReview>> GetReviewAsync(LocationScope scope, int locationId, string viewerUserId);

    // Approves or turns down a pick-up, a call-off, or a swap the requester has accepted.
    Task<Result<bool>> DecideAsync(LocationScope scope, Guid claimId, bool approve, string? reason, string deciderUserId);

    // Takes a published shift off whoever is on it and opens it up (someone phoned in sick), telling
    // them and everyone who could cover it.
    Task<Result<bool>> ReleaseShiftAsync(LocationScope scope, Guid shiftId, string actingUserId);

    // Requests waiting for this manager at their signed-in location (their own excluded).
    Task<Result<int>> CountPendingForNavAsync(LocationScope scope, string? viewerUserId);

    Task<Result<LocationSchedulingSettings>> GetLocationSettingsAsync(int locationId);

    Task<Result<LocationSchedulingSettings>> SaveLocationSettingsAsync(LocationScope scope, int locationId, bool claimsNeedApproval, string actingUserId);
}
