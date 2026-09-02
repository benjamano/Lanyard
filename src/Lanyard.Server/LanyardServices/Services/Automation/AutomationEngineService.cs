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

    // When each client last changed status. Written in EnqueueTransition alongside
    // _lastKnownStatus so the two can never drift out of step.
    private readonly ConcurrentDictionary<Guid, DateTime> _lastTransitionUtc = new();

    // Idle bookkeeping is keyed by RULE, not by client: two idle rules can target the same kiosk,
    // and a per-client flag would let the first one fired permanently starve the second.
    //
    // A stretch is identified by the instant it began, so "already fired" needs no explicit reset -
    // the next transition moves _lastTransitionUtc and the stored value simply stops matching.
    private readonly ConcurrentDictionary<Guid, DateTime> _idleFiredForStretchStart = new();

    // When each idle rule was last attempted, successful or not. Gates retries after a failure
    // (e.g. the kiosk was offline) to one per threshold window, instead of one per 60s tick
    // writing an execution-log row each time.
    private readonly ConcurrentDictionary<Guid, DateTime> _idleLastAttemptUtc = new();

    // The local calendar date a scheduled rule last fired successfully. Keyed by RULE, same
    // reasoning as the idle dictionaries above - two schedule rules could share a trigger client.
    private readonly ConcurrentDictionary<Guid, DateOnly> _scheduledFiredForLocalDate = new();

    // Tracks which scheduled rules have already been reseeded from AutomationRuleExecutions, so
    // EnsureScheduledRuleSeededAsync only queries once per rule per process lifetime rather than
    // on every tick.
    private readonly ConcurrentDictionary<Guid, byte> _scheduledSeeded = new();

    // A 60s tick rarely lands on the exact scheduled second, and an unbounded "fire if we're past
    // it" would replay hours-old rules after a restart (a server booting at 22:00 must not turn
    // the venue lights back on for a 09:00 rule). This window bounds both how late a tick can
    // still catch a fire and how many times a failed attempt retries (~5 tries at 60s cadence).
    private static readonly TimeSpan ScheduleCatchUpWindow = TimeSpan.FromMinutes(5);

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

        GameStatus previousStatus = _lastKnownStatus.GetOrAdd(clientId, GameStatus.NotStarted);
        if (previousStatus == newStatus)
        {
            return; // ENG-01: edge-triggered - same status does not re-fire
        }
        _lastKnownStatus[clientId] = newStatus;

        // Starts a fresh idle stretch. Idle rules key off this instant, so moving it is what
        // makes an already-fired rule eligible again.
        _lastTransitionUtc[clientId] = DateTime.UtcNow;

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

            // A game in progress is not idle. _lastTransitionUtc only records the last status
            // *change*, so without this a threshold shorter than a game would elapse mid-game and
            // fire - e.g. a 10-minute rule clearing the display 10 minutes into a 20-minute game.
            if (_lastKnownStatus.GetValueOrDefault(rule.TriggerClientId) == GameStatus.InGame)
            {
                continue;
            }

            if (!IsIdleThresholdReached(lastTransitionUtc, nowUtc, rule.IdleThresholdMinutes!.Value))
            {
                continue;
            }

            if (_idleFiredForStretchStart.TryGetValue(rule.Id, out DateTime firedForStretchStart)
                && firedForStretchStart == lastTransitionUtc)
            {
                continue;
            }

            // After a failed attempt the rule stays eligible so it can retry - a kiosk that was
            // offline when its idle screen was due should still get it on the next attempt - but
            // not on every tick.
            if (_idleLastAttemptUtc.TryGetValue(rule.Id, out DateTime lastAttemptUtc)
                && !IsIdleThresholdReached(lastAttemptUtc, nowUtc, rule.IdleThresholdMinutes!.Value))
            {
                continue;
            }

            _idleLastAttemptUtc[rule.Id] = nowUtc;

            _logger.LogInformation("Client {ClientId} idle for {ThresholdMinutes} minute(s) - firing rule {RuleId}", rule.TriggerClientId, rule.IdleThresholdMinutes, rule.Id);

            bool succeeded = await ExecuteRuleAsync(rule, rule.TriggerClientId, nameof(AutomationTriggerType.ClientIdle), ct);

            if (succeeded)
            {
                _idleFiredForStretchStart[rule.Id] = lastTransitionUtc;
            }
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

    /// <summary>
    /// Fires any Scheduled rule whose time of day and day of week are due for the local calendar
    /// day.
    /// </summary>
    /// <remarks>
    /// Called on a timer by ScheduledTriggerHostedService, mirroring how ClientIdle is evaluated by
    /// IdleTriggerHostedService. nowLocal is server-local wall-clock time, not UTC - this matches
    /// HallOfFamePeriodExtensions.ToUtcLowerBound and Client.AutoRestartTimeOfDay, the two other
    /// implicitly-server-local values in the app, since there is no venue timezone concept.
    /// </remarks>
    public async Task ProcessScheduledRulesAsync(DateTime nowLocal, CancellationToken ct)
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

        List<AutomationRule> scheduledRules = _ruleCache
            .Where(r => r.TriggerType == AutomationTriggerType.Scheduled && r.ScheduledTimeOfDay.HasValue)
            .ToList();

        if (scheduledRules.Count == 0)
        {
            return;
        }

        DateOnly today = DateOnly.FromDateTime(nowLocal);

        foreach (AutomationRule rule in scheduledRules)
        {
            if (!IsScheduledDayMatched(rule.ScheduledDaysOfWeek, nowLocal.DayOfWeek))
            {
                continue;
            }

            if (!IsScheduledTimeReached(rule.ScheduledTimeOfDay!.Value, nowLocal, ScheduleCatchUpWindow))
            {
                continue;
            }

            await EnsureScheduledRuleSeededAsync(rule.Id, ct);

            if (_scheduledFiredForLocalDate.TryGetValue(rule.Id, out DateOnly firedForDate) && firedForDate == today)
            {
                continue;
            }

            _logger.LogInformation("Scheduled rule {RuleId} due at {ScheduledTimeOfDay} - firing", rule.Id, rule.ScheduledTimeOfDay);

            bool succeeded = await ExecuteRuleAsync(rule, rule.TriggerClientId, nameof(AutomationTriggerType.Scheduled), ct);

            if (succeeded)
            {
                _scheduledFiredForLocalDate[rule.Id] = today;
            }
        }
    }

    /// <summary>
    /// Pure so schedule-time comparison can be unit tested at fixed instants rather than by
    /// waiting on the hosted service's real timer.
    /// </summary>
    /// <remarks>
    /// Deliberately does not wrap around midnight: a rule scheduled for 23:58 with a 5-minute
    /// window does not catch up into the next calendar day - that's a different dedupe key, and
    /// letting it fire there would be surprising.
    /// </remarks>
    public static bool IsScheduledTimeReached(TimeOnly scheduledTimeOfDay, DateTime nowLocal, TimeSpan catchUpWindow)
    {
        TimeSpan elapsed = nowLocal.TimeOfDay - scheduledTimeOfDay.ToTimeSpan();
        return elapsed >= TimeSpan.Zero && elapsed <= catchUpWindow;
    }

    /// <summary>
    /// Pure wrapper around <see cref="AutomationScheduleDays.Matches"/> kept alongside the other
    /// schedule-trigger pure helpers for symmetry with <see cref="IsIdleThresholdReached"/>.
    /// </summary>
    public static bool IsScheduledDayMatched(string? scheduledDaysOfWeek, DayOfWeek day)
    {
        return AutomationScheduleDays.Matches(scheduledDaysOfWeek, day);
    }

    /// <summary>
    /// Reseeds "last fired" from the execution log the first time a scheduled rule is evaluated in
    /// this process, so a server restart shortly after a rule fired doesn't forget that and re-fire
    /// it later the same day.
    /// </summary>
    /// <remarks>
    /// Queries AutomationRuleExecutions directly rather than going through IAutomationLogService:
    /// AutomationEngineService is a singleton but that service is scoped, so injecting it would be
    /// a captive dependency. Only successful executions count - reseeding from a failed row would
    /// suppress the catch-up retry a mid-window restart is supposed to allow.
    /// </remarks>
    private async Task EnsureScheduledRuleSeededAsync(Guid ruleId, CancellationToken ct)
    {
        if (_scheduledSeeded.ContainsKey(ruleId))
        {
            return;
        }

        try
        {
            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(ct);
            AutomationRuleExecution? lastSuccess = await ctx.AutomationRuleExecutions
                .Where(e => e.AutomationRuleId == ruleId && e.OverallSuccess)
                .OrderByDescending(e => e.ExecutedAt)
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(ct);

            if (lastSuccess != null)
            {
                DateOnly lastSuccessLocalDate = DateOnly.FromDateTime(lastSuccess.ExecutedAt.ToLocalTime());
                _scheduledFiredForLocalDate[ruleId] = lastSuccessLocalDate;
            }

            // Only mark as seeded once the query has actually succeeded - marking it earlier would
            // let a transient failure here (e.g. a DB blip right after a restart) permanently skip
            // the reseed for the rest of the process's lifetime instead of retrying next tick.
            _scheduledSeeded.TryAdd(ruleId, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reseed last-fired date for scheduled rule {RuleId}", ruleId);
        }
    }

    /// <summary>Returns whether every action in the rule succeeded.</summary>
    private async Task<bool> ExecuteRuleAsync(AutomationRule rule, Guid clientId, string triggerEventLabel, CancellationToken ct)
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

        return execution.OverallSuccess;
    }
}
