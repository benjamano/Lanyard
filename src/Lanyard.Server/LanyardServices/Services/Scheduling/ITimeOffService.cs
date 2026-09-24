using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// Time-off requests, balances and decisions. Allowance amounts come from ITimeOffPolicyService.
public interface ITimeOffService
{
    // Allowance, approved and pending hours per allowance-deducting type for the holiday year
    // containing onDate.
    Task<Result<TimeOffBalances>> GetBalancesAsync(string userId, int companyId, DateOnly onDate);

    // What a draft would cost and any warnings, without saving - for the live request dialog.
    // Fails with the same message SubmitAsync would for something that can't be requested at all
    // (wrong dates, overlaps an existing request, crosses into the next holiday year).
    // forManager: checked as a manager recording it (past dates allowed, third-person wording).
    Task<Result<TimeOffPreview>> PreviewAsync(string userId, int companyId, TimeOffRequestDraft draft, bool forManager = false);

    // A staff member asking for their own time off, from the location they're signed in to.
    // Starts Pending. Exceeding an allowance comes back as a warning, not a failure.
    Task<Result<TimeOffSubmitResult>> SubmitAsync(string userId, int locationId, TimeOffRequestDraft draft);

    // A manager recording time off for someone (sickness called in, say). Saved as Approved, and
    // may be in the past.
    Task<Result<TimeOffSubmitResult>> RecordForUserAsync(LocationScope scope, string userId, int locationId, TimeOffRequestDraft draft, string managerUserId);

    // The person's own request: pending ones any time, approved ones until the day they start.
    Task<Result<bool>> CancelAsync(string userId, Guid requestId);

    // The person's requests that end on or after fromDate, soonest first.
    Task<Result<List<TimeOffRequest>>> GetRequestsForUserAsync(string userId, DateOnly fromDate);

    Task<Result<List<TimeOffRequestView>>> GetRequestsForLocationAsync(LocationScope scope, int locationId, TimeOffListFilter filter);

    Task<Result<int>> CountPendingAsync(LocationScope scope, int locationId);

    // Approve a pending request, or reject a pending one - or an approved one that hasn't ended
    // yet, to withdraw it. Rejecting needs a reason, which the person sees.
    Task<Result<TimeOffRequest>> DecideAsync(LocationScope scope, Guid requestId, bool approve, string? reason, string deciderUserId);

    // Pending and approved requests for these people overlapping [from, to], with their type -
    // what the rota shows beside the shifts.
    Task<Result<List<TimeOffRequest>>> GetLiveTimeOffAsync(IReadOnlyCollection<string> userIds, DateOnly from, DateOnly to);
}
