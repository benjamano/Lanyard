using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// The dates of birth of staff under 18, so the rota can keep them within the UK young worker
// limits. Only Managers and Admins can read or change them: staff never see them, and nobody can
// change their own. Only kept while the person is under 18.
public interface IYoungWorkerService
{
    // Someone's date of birth and the dates worked out from it, or null if none is held.
    Task<Result<YoungWorkerStatusView?>> GetAsync(LocationScope scope, string userId, string actingUserId);

    // Sets the date of birth, or with null clears it (the row is deleted). Only for someone under 18
    // today. Refused for your own record, for anyone you don't manage, and for an Admin unless you
    // are one.
    Task<Result<YoungWorkerStatusView?>> SaveAsync(LocationScope scope, string userId, DateOnly? dateOfBirth, string actingUserId);

    // Everyone in the signed-in company who is still a young worker on `today`, by user id - for
    // the staff list and the rota.
    Task<Result<Dictionary<string, YoungWorkerStatus>>> GetForCompanyAsync(LocationScope scope, DateOnly today);

    // The young worker limits this draft shift (not yet saved) would break for draft.UserId, for the
    // shift dialog's warning. Empty for an open shift or anyone who isn't a young worker. Managers
    // of the shift's location only.
    Task<Result<List<YoungWorkerBreach>>> CheckShiftAsync(LocationScope scope, Shift draft);

    // True if the person is under 18 on `today` - all staff learn is that the limits apply to them,
    // never their date of birth.
    Task<Result<bool>> IsYoungWorkerAsync(string userId, int companyId, DateOnly today);
}
