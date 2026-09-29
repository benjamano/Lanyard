using Lanyard.Infrastructure.Models;

namespace Lanyard.Infrastructure.DTO.Scheduling;

// ---- Staff side (My Shifts) -------------------------------------------------------------------

// An open shift someone could ask for. NotEligibleReason says why they can't ("You're already
// working then"), and is null when they can; MyClaim is their request if they've made one.
public record OpenShiftView(Shift Shift, bool NeedsApproval, ShiftClaim? MyClaim, string? NotEligibleReason);

// A colleague's swap request. ShiftsICanOffer are the viewer's own shifts they could give in
// return; empty means they have nothing to offer.
public record SwapRequestView(ShiftClaim Swap, Shift Shift, string RequesterName, ShiftClaim? MyOffer, List<Shift> ShiftsICanOffer);

// One of the offers on the viewer's own swap request.
public record SwapOfferView(ShiftClaim Offer, Shift OfferedShift, string OffererName);

// A request the viewer made that's still open: a pick-up or call-off waiting for a manager, or a
// swap collecting offers (with the offers so far).
public record MyClaimView(ShiftClaim Claim, Shift Shift, Shift? OfferedShift, List<SwapOfferView> Offers);

public record ShiftMarketplace(List<OpenShiftView> OpenShifts, List<SwapRequestView> SwapRequests, List<MyClaimView> MyClaims)
{
    // The viewer's locations: only changes at these need the page refreshed.
    public List<int> LocationIds { get; init; } = [];

    // For the "Up for grabs" badge: things the viewer could act on right now.
    public int ActionableCount =>
        OpenShifts.Count(x => x.MyClaim is null && x.NotEligibleReason is null)
        + SwapRequests.Count(x => x.MyOffer is null && x.ShiftsICanOffer.Count > 0)
        + MyClaims.Sum(x => x.Offers.Count(o => o.Offer.Status == Enum.ShiftClaimStatus.Pending));
}

// ---- Manager side (Shift Requests) ------------------------------------------------------------

// One request for a manager to decide. For a swap, Claim is the Swap, Person works Shift today,
// and OtherPerson works OtherShift and would take Shift. Problem is set when approving would break
// a rule (now overlapping, off that day, no longer at the location) - shown, and approval refused.
public record ClaimReviewItem(ShiftClaim Claim, Shift Shift, string PersonName, Shift? OtherShift, string? OtherPersonName, ShiftClaim? AcceptedOffer, string? Problem);

// Everyone asking for the same open shift, so the manager picks one of them.
public record PickupGroup(Shift Shift, List<ClaimReviewItem> Claims);

public record ShiftClaimReview(List<PickupGroup> Pickups, List<ClaimReviewItem> Drops, List<ClaimReviewItem> Swaps, bool ClaimsNeedApproval)
{
    public int Count => Pickups.Sum(x => x.Claims.Count) + Drops.Count + Swaps.Count;
}
