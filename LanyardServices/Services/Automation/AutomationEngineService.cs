#nullable enable

namespace Lanyard.Application.Services;

/// <summary>
/// Stub — full implementation added in Plan 02-03.
/// Provides the type reference needed by AutomationRuleService at compile time.
/// </summary>
public class AutomationEngineService
{
    /// <summary>
    /// Signals that the cached rule set is stale and must be reloaded on next evaluation.
    /// Full implementation in Plan 02-03.
    /// </summary>
    public virtual void InvalidateRuleCache()
    {
    }
}
