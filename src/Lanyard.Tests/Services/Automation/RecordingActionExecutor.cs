#nullable enable

using Lanyard.Application.Services;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Tests.Services.Automation;

// Records what the engine asked it to do. A Moq setup covers the simple cases, but the
// fault-isolation test needs one action to throw while later ones still run, and an explicit fake
// keeps those ordering assertions readable.
//
// Shared by AutomationEngineServiceTests and AutomationRuleServiceTests: the rule-service tests
// assert cache invalidation by observing what the engine executes on the next transition, so they
// need the same recorder.
internal sealed class RecordingActionExecutor : IActionExecutor
{
    internal const string TestActionType = "TestAction";

    private readonly Func<AutomationRuleAction, (bool Success, string? ErrorMessage)>? _behaviour;

    public RecordingActionExecutor(
        Func<AutomationRuleAction, (bool Success, string? ErrorMessage)>? behaviour = null)
    {
        _behaviour = behaviour;
    }

    public List<Guid> ExecutedActionIds { get; } = [];

    public bool CanHandle(string actionType) => actionType == TestActionType;

    public Task<(bool Success, string? ErrorMessage)> ExecuteAsync(
        AutomationRuleAction action, Guid triggerClientId)
    {
        ExecutedActionIds.Add(action.Id);

        return Task.FromResult(_behaviour?.Invoke(action) ?? (true, (string?)null));
    }
}
