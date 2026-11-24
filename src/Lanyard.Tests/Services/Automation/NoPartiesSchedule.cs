using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DTO;

namespace Lanyard.Tests.Services.Automation;

/// <summary>A party schedule with no parties, for engine tests that aren't about party rules.</summary>
public sealed class NoPartiesSchedule : IPartyScheduleProvider
{
    public Task<Result<IReadOnlyList<PartySlot>>> GetDayAsync(int companyId, int locationId, DateTime nowUtc, CancellationToken cancellationToken) =>
        Task.FromResult(Result<IReadOnlyList<PartySlot>>.Ok([]));

    public void Invalidate(int locationId)
    {
    }
}
