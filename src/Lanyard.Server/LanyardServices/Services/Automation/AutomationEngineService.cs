#nullable enable

using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
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

    // When each client last changed status, and whether its current idle stretch has already
    // fired. Both are written in EnqueueTransition alongside _lastKnownStatus so they can never
    // drift out of step with it.
    private readonly ConcurrentDictionary<Guid, DateTime> _lastTransitionUtc = new();
    private readonly ConcurrentDictionary<Guid, bool> _idleFiredForCurrentStretch = new();
    private volatile bool _isEnabled = false;
    private volatile bool _ruleCacheDirty = true;
    private List<AutomationRule> _ruleCache = [];
    private readonly SemaphoreSlim _ruleCacheLock = new(1, 1);
    private bool _initializedEnabled = false;
    private readonly object _initLock = new();

    public ChannelReader<GameStatusTransitionEvent> Reader => _transitionChannel.Reader;
    public bool IsEnabled => _isEnabled;

    public event Action<Guid, bool>? OnRuleEnabledChanged;
    public event Action<Guid, DateTime, bool>? OnRuleExecuted;

    public void SetEnabled(bool enabled)
    {
        _isEnabled = enabled;
    }

    public void InvalidateRuleCache()
    {
        _ruleCacheDirty = true;
    }

    public void NotifyRuleEnabledChanged(Guid ruleId, bool isEnabled)
    {
        OnRuleEnabledChanged?.Invoke(ruleId, isEnabled);
    }

    public void EnqueueTransition(Guid clientId, GameStatus newStatus)
    {
        // Start the idle clock the first time a client is ever seen, before the edge check below.
        // A kiosk that comes up idle and stays idle never produces a transition, so without this
        // it would have no timestamp at all and its idle rules could never fire - which is
        // precisely the quiet-zone case those rules exist for.
        _lastTransitionUtc.GetOrAdd(clientId, _ => DateTime.UtcNow);
        _idleFiredForCurrentStretch.GetOrAdd(clientId, false);

        GameStatus previousStatus = _lastKnownStatus.GetOrAdd(clientId, GameStatus.NotStarted);
        if (previousStatus == newStatus)
        {
            return; // ENG-01: edge-triggered - same status does not re-fire
        }
        _lastKnownStatus[clientId] = newStatus;
        _lastTransitionUtc[clientId] = DateTime.UtcNow;

        // Any status change starts a fresh idle stretch, so an idle rule that already fired
        // becomes eligible again.
        _idleFiredForCurrentStretch[clientId] = false;

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
            _logger.LogInformation("AutomationEngine initialized - enabled: {IsEnabled}", _isEnabled);
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
                .Where(r => r.IsActive && r.IsEnabled)
                .Include(r => r.Actions.Where(a => a.IsActive))
                .AsNoTracking()
                .ToListAsync(ct);
            _ruleCache = rules;
            _ruleCacheDirty = false;
            _logger.LogInformation("AutomationEngine rule cache reloaded - {Count} rules", rules.Count);
        }
        finally
        {
            _ruleCacheLock.Release();
        }
    }

    private void EnsureEnabledInitialized(CancellationToken ct)
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
    }

    public async Task ProcessTransitionAsync(GameStatusTransitionEvent ev, CancellationToken ct)
    {
        EnsureEnabledInitialized(ct);

        if (!_isEnabled)
        {
            _logger.LogDebug("AutomationEngine is disabled - skipping transition for client {ClientId}", ev.ClientId);
            return;
        }

        // Reload rule cache if dirty
        if (_ruleCacheDirty)
        {
            await ReloadRuleCacheAsync(ct);
        }

        // Filter rules matching this transition
        // The TriggerType clause matters: an idle rule keeps whatever stale TriggerEvent it was
        // saved with, and without this it would also fire on a matching transition.
        List<AutomationRule> matchingRules = _ruleCache
            .Where(r => r.TriggerType == AutomationTriggerType.GameStatusTransition
                && r.TriggerClientId == ev.ClientId
                && r.TriggerEvent == ev.NewStatus)
            .ToList();

        if (matchingRules.Count == 0)
        {
            return;
        }

        // Process each matching rule
        foreach (AutomationRule rule in matchingRules)
        {
            await ExecuteRuleAsync(rule, ev.ClientId, ev.NewStatus.ToString(), ct);
        }
    }

    /// <summary>
    /// Fires any ClientIdle rule whose trigger client has now been idle past its threshold.
    /// </summary>
    /// <remarks>
    /// Called on a timer by IdleTriggerHostedService. Each idle stretch fires at most once -
    /// re-firing every tick would restart the same dashboard over and over - and a client only
    /// becomes eligible again after EnqueueTransition records a new status change.
    /// </remarks>
    public async Task ProcessIdleRulesAsync(DateTime nowUtc, CancellationToken ct)
    {
        EnsureEnabledInitialized(ct);

        if (!_isEnabled)
        {
            return;
        }

        if (_ruleCacheDirty)
        {
            await ReloadRuleCacheAsync(ct);
        }

        List<AutomationRule> idleRules = _ruleCache
            .Where(r => r.TriggerType == AutomationTriggerType.ClientIdle && r.IdleThresholdMinutes.HasValue)
            .ToList();

        if (idleRules.Count == 0)
        {
            return;
        }

        foreach (AutomationRule rule in idleRules)
        {
            // A client the server has never heard from has no idle stretch to measure - staying
            // silent is better than firing a lobby screen for a kiosk that may not be powered on.
            if (!_lastTransitionUtc.TryGetValue(rule.TriggerClientId, out DateTime lastTransitionUtc))
            {
                continue;
            }

            if (!IsIdleThresholdReached(lastTransitionUtc, nowUtc, rule.IdleThresholdMinutes!.Value))
            {
                continue;
            }

            if (_idleFiredForCurrentStretch.GetValueOrDefault(rule.TriggerClientId))
            {
                continue;
            }

            _idleFiredForCurrentStretch[rule.TriggerClientId] = true;

            _logger.LogInformation("Client {ClientId} idle for {ThresholdMinutes} minute(s) - firing rule {RuleId}", rule.TriggerClientId, rule.IdleThresholdMinutes, rule.Id);

            await ExecuteRuleAsync(rule, rule.TriggerClientId, nameof(AutomationTriggerType.ClientIdle), ct);
        }
    }

    /// <summary>
    /// Pure so the threshold comparison can be unit tested at fixed instants rather than by
    /// waiting on the hosted service's real timer.
    /// </summary>
    public static bool IsIdleThresholdReached(DateTime lastTransitionUtc, DateTime nowUtc, int thresholdMinutes)
    {
        if (thresholdMinutes <= 0)
        {
            return false;
        }

        return nowUtc - lastTransitionUtc >= TimeSpan.FromMinutes(thresholdMinutes);
    }

    private async Task ExecuteRuleAsync(AutomationRule rule, Guid clientId, string triggerEventLabel, CancellationToken ct)
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
                (bool success, string? errorMessage) = await executor.ExecuteAsync(action, clientId);
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
                    ErrorMessage = $"Action '{action.ActionType}' failed: {ex.Message}"
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
            TriggerEvent = triggerEventLabel, // string snapshot per STATE.md decision
            TriggerClientId = clientId,
            OverallSuccess = actionExecutions.All(a => a.Success),
            ActionExecutions = actionExecutions
        };

        try
        {
            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(ct);
            ctx.AutomationRuleExecutions.Add(execution);
            await ctx.SaveChangesAsync(ct);

            OnRuleExecuted?.Invoke(rule.Id, execution.ExecutedAt, execution.OverallSuccess);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write execution log for rule {RuleId}", rule.Id);
        }
    }
}
