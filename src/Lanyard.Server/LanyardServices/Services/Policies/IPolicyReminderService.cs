using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Policies;

public interface IPolicyReminderService
{
    // Sends every reminder that's due: a first "please read" once someone has a policy to read, an
    // "overdue" once it's past the date, then weekly while it stays overdue - three at most for each
    // time someone is asked. Each one is claimed before it's queued, so overlapping runs can't send
    // twice. One policy, or every policy in every company with Policies switched on.
    Task<Result<int>> SendDueRemindersAsync(Guid? policyId = null, CancellationToken cancellationToken = default);

    // A manager's "Remind them now": nudges everyone they can see who still has to read the
    // policy, except anyone already reminded in the last day. Returns how many were reminded.
    Task<Result<int>> RemindOutstandingAsync(Guid policyId, LocationScope scope, CancellationToken cancellationToken = default);
}
