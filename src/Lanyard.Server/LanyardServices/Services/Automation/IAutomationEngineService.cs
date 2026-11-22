#nullable enable

using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;

namespace Lanyard.Application.Services;

/// <summary>
/// What the app (pages, widgets, the hub and the rule service) uses of the automation engine.
/// </summary>
/// <remarks>
/// AutomationEngineService is one singleton registered under both its concrete type and this
/// interface. The hosted services keep the concrete type, because they drain its channels.
/// </remarks>
public interface IAutomationEngineService
{
    bool IsEnabled { get; }

    event Action<Guid, bool>? OnRuleEnabledChanged;

    /// <summary>Raised after a rule's trigger fired and its actions ran. Never for a test run.</summary>
    event Action<Guid, DateTime, bool>? OnRuleExecuted;

    void SetEnabled(bool enabled);
    void InvalidateRuleCache();
    void NotifyRuleEnabledChanged(Guid ruleId, bool isEnabled);
    void EnqueueTransition(Guid clientId, GameStatus newStatus, LaserGameStatusDTO? status = null);
    void EnqueueGameData(Guid clientId, LaserGameStatusDTO status);

    /// <summary>
    /// Runs an enabled rule's actions once, now, against its trigger kiosk, and logs the run as a
    /// test. Works while the engine switch is off. Doesn't affect when the rule next fires for real,
    /// raise Operations alerts or change the dashboard widget's last run.
    /// </summary>
    Task<Result<AutomationTestRunResult>> TestRunRuleAsync(AutomationTestRunRequest request, CancellationToken ct = default);
}
