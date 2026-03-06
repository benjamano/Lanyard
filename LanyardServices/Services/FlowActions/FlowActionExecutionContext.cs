using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.FlowActions;

public sealed class FlowActionExecutionContext
{
    public Guid? TriggerClientId { get; init; }

    public IDictionary<string, string?> Values { get; init; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public bool TryGetValue(string key, out string? value)
    {
        return Values.TryGetValue(key, out value);
    }
}
