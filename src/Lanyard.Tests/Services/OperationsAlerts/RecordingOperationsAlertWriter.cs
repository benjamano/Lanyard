using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Infrastructure.DTO;

namespace Lanyard.Tests.Services.OperationsAlerts;

// Stands in for the real writer in producer tests: records what would have been raised or cleared.
internal sealed class RecordingOperationsAlertWriter : IOperationsAlertWriter
{
    public List<OperationsAlertRaise> Raised { get; } = [];

    public List<(int CompanyId, string DedupKey, string? Note)> Resolved { get; } = [];

    // Set to make every call fail, to check a producer carries on regardless.
    public bool Fail { get; set; }

    public Task<Result<OperationsAlertRaiseOutcome>> RaiseAsync(OperationsAlertRaise raise, CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new InvalidOperationException("Alert store unavailable.");
        }

        Raised.Add(raise);
        return Task.FromResult(Result<OperationsAlertRaiseOutcome>.Ok(OperationsAlertRaiseOutcome.Created));
    }

    public Task<Result<bool>> ResolveAsync(int companyId, string dedupKey, string? note = null, CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new InvalidOperationException("Alert store unavailable.");
        }

        Resolved.Add((companyId, dedupKey, note));
        return Task.FromResult(Result<bool>.Ok(true));
    }
}
