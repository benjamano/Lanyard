#nullable enable

using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Lanyard.Application.Services;

public class AutomationEngineService(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    IEnumerable<IActionExecutor> actionExecutors,
    ILogger<AutomationEngineService> logger)
{
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory = contextFactory;
    private readonly IEnumerable<IActionExecutor> _actionExecutors = actionExecutors;
    private readonly ILogger<AutomationEngineService> _logger = logger;

    private readonly Channel<GameStatusTransitionEvent> _transitionChannel =
        Channel.CreateUnbounded<GameStatusTransitionEvent>();

    private readonly ConcurrentDictionary<Guid, GameStatus> _lastKnownStatus = new();
    private volatile bool _isEnabled = false;
    private volatile bool _ruleCacheDirty = true;
    private List<AutomationRule> _ruleCache = [];
    private readonly SemaphoreSlim _ruleCacheLock = new(1, 1);
    private bool _initializedEnabled = false;
    private readonly object _initLock = new();

    public ChannelReader<GameStatusTransitionEvent> Reader => _transitionChannel.Reader;
    public bool IsEnabled => _isEnabled;

    public void SetEnabled(bool enabled)
    {
        _isEnabled = enabled;
    }

    public void InvalidateRuleCache()
    {
        _ruleCacheDirty = true;
    }

    public void EnqueueTransition(Guid clientId, GameStatus newStatus)
    {
        GameStatus previousStatus = _lastKnownStatus.GetOrAdd(clientId, GameStatus.NotStarted);
        if (previousStatus == newStatus)
        {
            return; // ENG-01: edge-triggered — same status does not re-fire
        }
        _lastKnownStatus[clientId] = newStatus;
        GameStatusTransitionEvent ev = new(clientId, previousStatus, newStatus);
        _transitionChannel.Writer.TryWrite(ev);
    }

    private async Task InitializeEnabledAsync(CancellationToken ct)
    {
        try
        {
            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(ct);
            AppSetting? setting = await ctx.AppSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == "AutomationEngine.Enabled", ct);
            _isEnabled = setting?.Value == "true";
            _logger.LogInformation("AutomationEngine initialized — enabled: {IsEnabled}", _isEnabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read AutomationEngine.Enabled setting; defaulting to disabled");
            _isEnabled = false;
        }
    }

    private async Task ReloadRuleCacheAsync(CancellationToken ct)
    {
        await _ruleCacheLock.WaitAsync(ct);
        try
        {
            if (!_ruleCacheDirty) return; // double-check inside lock
            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(ct);
            List<AutomationRule> rules = await ctx.AutomationRules
                .Where(r => r.IsActive)
                .Include(r => r.Actions.Where(a => a.IsActive))
                .AsNoTracking()
                .ToListAsync(ct);
            _ruleCache = rules;
            _ruleCacheDirty = false;
            _logger.LogInformation("AutomationEngine rule cache reloaded — {Count} rules", rules.Count);
        }
        finally
        {
            _ruleCacheLock.Release();
        }
    }

    public async Task ProcessTransitionAsync(GameStatusTransitionEvent ev, CancellationToken ct)
    {
        // Initialize enabled flag from DB on first call (thread-safe one-time init)
        if (!_initializedEnabled)
        {
            lock (_initLock)
            {
                if (!_initializedEnabled)
                {
                    _initializedEnabled = true;
                    // Fire-and-forget init; result stored in _isEnabled
                    InitializeEnabledAsync(ct).GetAwaiter().GetResult();
                }
            }
        }

        if (!_isEnabled)
        {
            _logger.LogDebug("AutomationEngine is disabled — skipping transition for client {ClientId}", ev.ClientId);
            return;
        }

        // Reload rule cache if dirty
        if (_ruleCacheDirty)
        {
            await ReloadRuleCacheAsync(ct);
        }

        // Filter rules matching this transition
        List<AutomationRule> matchingRules = _ruleCache
            .Where(r => r.TriggerClientId == ev.ClientId && r.TriggerEvent == ev.NewStatus)
            .ToList();

        if (matchingRules.Count == 0)
        {
            return;
        }

        // Process each matching rule
        foreach (AutomationRule rule in matchingRules)
        {
            await ExecuteRuleAsync(rule, ev, ct);
        }
    }

    private async Task ExecuteRuleAsync(AutomationRule rule, GameStatusTransitionEvent ev, CancellationToken ct)
    {
        List<AutomationRuleActionExecution> actionExecutions = [];

        foreach (AutomationRuleAction action in rule.Actions.OrderBy(a => a.SortOrder))
        {
            IActionExecutor? executor = _actionExecutors.FirstOrDefault(e => e.CanHandle(action.ActionType));
            if (executor == null)
            {
                actionExecutions.Add(new AutomationRuleActionExecution
                {
                    Id = Guid.NewGuid(),
                    AutomationRuleActionId = action.Id,
                    Success = false,
                    ErrorMessage = $"Action type not supported: {action.ActionType}"
                });
                _logger.LogWarning("No executor found for action type {ActionType} on rule {RuleId}", action.ActionType, rule.Id);
                continue;
            }

            // ENG-04: fault-isolated per-action
            try
            {
                (bool success, string? errorMessage) = await executor.ExecuteAsync(action, ev.ClientId);
                actionExecutions.Add(new AutomationRuleActionExecution
                {
                    Id = Guid.NewGuid(),
                    AutomationRuleActionId = action.Id,
                    Success = success,
                    ErrorMessage = errorMessage
                });
                if (!success)
                {
                    _logger.LogWarning("Action {ActionId} on rule {RuleId} failed: {Error}", action.Id, rule.Id, errorMessage);
                }
            }
            catch (Exception ex)
            {
                actionExecutions.Add(new AutomationRuleActionExecution
                {
                    Id = Guid.NewGuid(),
                    AutomationRuleActionId = action.Id,
                    Success = false,
                    ErrorMessage = $"Music operation failed: {ex.Message}"
                });
                _logger.LogError(ex, "Unhandled error executing action {ActionId} on rule {RuleId}", action.Id, rule.Id);
            }
        }

        // LOG-01: Write execution log
        AutomationRuleExecution execution = new()
        {
            Id = Guid.NewGuid(),
            AutomationRuleId = rule.Id,
            RuleName = rule.Name,
            ExecutedAt = DateTime.UtcNow,
            TriggerEvent = ev.NewStatus.ToString(), // string snapshot per STATE.md decision
            TriggerClientId = ev.ClientId,
            OverallSuccess = actionExecutions.All(a => a.Success),
            ActionExecutions = actionExecutions
        };

        try
        {
            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(ct);
            ctx.AutomationRuleExecutions.Add(execution);
            await ctx.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write execution log for rule {RuleId}", rule.Id);
        }
    }
}
