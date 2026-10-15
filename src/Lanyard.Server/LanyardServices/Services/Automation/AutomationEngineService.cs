#nullable enable

using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Lanyard.Application.Services;

public class AutomationEngineService(
    ISystemDbContextFactory contextFactory,
    IEnumerable<IActionExecutor> actionExecutors,
    ILaserGameStatusStore laserGameStatusStore,
    TimeProvider timeProvider,
    ILogger<AutomationEngineService> logger)
{
    private readonly ISystemDbContextFactory _contextFactory = contextFactory;
    private readonly IEnumerable<IActionExecutor> _actionExecutors = actionExecutors;
    private readonly ILaserGameStatusStore _laserGameStatusStore = laserGameStatusStore;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<AutomationEngineService> _logger = logger;

    private readonly Channel<GameStatusTransitionEvent> _transitionChannel =
        Channel.CreateUnbounded<GameStatusTransitionEvent>();

    private readonly ConcurrentDictionary<Guid, GameStatus> _lastKnownStatus = new();

    // Game data (time remaining, scores) has its own channel and consumer. The kiosk publishes on
    // every score packet and countdown tick, so it's coalesced: only the latest snapshot per kiosk
    // is kept, and at most one event per kiosk waits in the channel however fast it publishes.
    private readonly Channel<GameDataEvent> _gameDataChannel = Channel.CreateUnbounded<GameDataEvent>();
    private readonly ConcurrentDictionary<Guid, GameSnapshot> _latestGameData = new();
    private readonly ConcurrentDictionary<Guid, byte> _gameDataPending = new();

    // Counts each kiosk's games (bumped on every move into InGame). A snapshot carries the number
    // current when it was enqueued, which is what makes "once per game" independent of the order
    // the two channels are drained in.
    private readonly ConcurrentDictionary<Guid, int> _gameNumber = new();

    // Per-rule game-data state, touched only by the single game-data consumer.
    private readonly Dictionary<Guid, int> _gameDataFiredForGame = [];
    private readonly Dictionary<Guid, (int GameNumber, int Seconds)> _timeRemainingObserved = [];

    // Whether any active rule uses a game-data trigger, set on every rule cache reload. While false
    // (and the cache is current) EnqueueGameData does nothing, so venues without these rules see no
    // extra work at all.
    private volatile bool _hasGameDataRules;

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

    // A rule skipped by its conditions is not "fired", so idle and scheduled rules stay eligible
    // and fire on a later tick once the conditions hold. These only stop the skip itself being
    // logged on every 60s tick: once per idle stretch, once per local day for a schedule.
    private readonly ConcurrentDictionary<Guid, DateTime> _idleSkipLoggedForStretch = new();
    private readonly ConcurrentDictionary<Guid, DateOnly> _scheduledSkipLoggedForLocalDate = new();

    // A 60s tick rarely lands on the exact scheduled second, and an unbounded "fire if we're past
    // it" would replay hours-old rules after a restart (a server booting at 22:00 must not turn
    // the venue lights back on for a 09:00 rule). This window bounds both how late a tick can
    // still catch a fire and how many times a failed attempt retries (~5 tries at 60s cadence).
    private static readonly TimeSpan ScheduleCatchUpWindow = TimeSpan.FromMinutes(5);

    private volatile bool _isEnabled = false;
    private volatile bool _ruleCacheDirty = true;
    private List<AutomationRule> _ruleCache = [];

    // Kiosks named by ClientGameStatus conditions, loaded with the rule cache. Includes each one's
    // CompanyId because the engine's context is unfiltered and must enforce that itself.
    private Dictionary<Guid, AutomationClientInfo> _conditionClients = [];

    // Locations named by DuringOpeningHours conditions, with their active opening hours. Reloaded
    // with the rule cache; LocationOpeningHoursService invalidates it when hours are saved.
    private Dictionary<int, AutomationLocationInfo> _conditionLocations = [];
    private readonly SemaphoreSlim _ruleCacheLock = new(1, 1);
    // The one-time read of the enabled flag, shared by every caller. Cleared again if the read
    // fails so a transient DB error at startup is retried on the next tick instead of leaving
    // the engine disabled until the process restarts.
    private Task<bool>? _initTask;

    public ChannelReader<GameStatusTransitionEvent> Reader => _transitionChannel.Reader;
    public ChannelReader<GameDataEvent> GameDataReader => _gameDataChannel.Reader;
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

        if (newStatus == GameStatus.InGame)
        {
            _gameNumber.AddOrUpdate(clientId, 1, (_, n) => n + 1);
        }

        GameStatusTransitionEvent ev = new(clientId, previousStatus, newStatus);
        _transitionChannel.Writer.TryWrite(ev);
    }

    /// <summary>
    /// Hands the engine a kiosk's latest game state for TimeRemainingBelow and ScoreThreshold rules.
    /// Called by the hub on every status publish, after EnqueueTransition for the same publish.
    /// </summary>
    /// <remarks>
    /// Security Issue 9.1: any kiosk can claim any client ID, so these triggers can be spoofed by a
    /// rogue kiosk publishing made-up scores. Not addressed here.
    /// </remarks>
    public void EnqueueGameData(Guid clientId, LaserGameStatusDTO status)
    {
        // A dirty cache hasn't been read yet (or just changed), so it can't say there are no
        // game-data rules; let the consumer reload it and decide.
        if (!_hasGameDataRules && !_ruleCacheDirty)
        {
            return;
        }

        _latestGameData[clientId] = GameSnapshot.From(clientId, _gameNumber.GetValueOrDefault(clientId), status);

        if (_gameDataPending.TryAdd(clientId, 0))
        {
            _gameDataChannel.Writer.TryWrite(new GameDataEvent(clientId));
        }
    }

    /// <returns>True when the setting was read; false when the read failed and should be retried.</returns>
    private async Task<bool> InitializeEnabledAsync(CancellationToken ct)
    {
        try
        {
            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(ct);
            AppSetting? setting = await ctx.AppSettings
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(s => s.Key == "AutomationEngine.Enabled", ct);
            _isEnabled = setting?.Value == "true";
            _logger.LogInformation("AutomationEngine initialized - enabled: {IsEnabled}", _isEnabled);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read AutomationEngine.Enabled setting; treating the engine as disabled and retrying on the next evaluation");
            _isEnabled = false;
            return false;
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
                .Include(r => r.Conditions.Where(c => c.IsActive))
                .AsNoTracking()
                .TagWithCallSite()
                .ToListAsync(ct);

            List<Guid> conditionClientIds = rules
                .SelectMany(r => r.Conditions)
                .Where(c => c.ConditionType == AutomationConditionType.ClientGameStatus)
                .Select(c => AutomationConditionParameters.TryParse(c.ParametersJson, out ClientGameStatusConditionParameters? p) ? p.ClientId : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            Dictionary<Guid, AutomationClientInfo> conditionClients = conditionClientIds.Count == 0
                ? []
                : await ctx.Clients
                    .Where(c => conditionClientIds.Contains(c.Id))
                    .AsNoTracking()
                    .TagWithCallSite()
                    .ToDictionaryAsync(c => c.Id, c => new AutomationClientInfo(c.CompanyId, c.Name), ct);

            List<int> conditionLocationIds = rules
                .SelectMany(r => r.Conditions)
                .Where(c => c.ConditionType == AutomationConditionType.DuringOpeningHours)
                .Select(c => AutomationConditionParameters.TryParse(c.ParametersJson, out OpeningHoursConditionParameters? p) ? p.LocationId : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            // A deactivated location is left out, so its conditions fail closed as "no longer exists".
            Dictionary<int, AutomationLocationInfo> conditionLocations = conditionLocationIds.Count == 0
                ? []
                : await ctx.Locations
                    .Where(l => conditionLocationIds.Contains(l.Id) && l.IsActive)
                    .Select(l => new
                    {
                        l.Id,
                        l.CompanyId,
                        l.Name,
                        Hours = l.OpeningHours.Where(h => h.IsActive).ToList()
                    })
                    .AsNoTracking()
                    .TagWithCallSite()
                    .ToDictionaryAsync(l => l.Id, l => new AutomationLocationInfo(l.CompanyId, l.Name, l.Hours), ct);

            // Kiosks and locations first, so a rule is never visible without what its conditions name.
            _conditionClients = conditionClients;
            _conditionLocations = conditionLocations;
            _hasGameDataRules = rules.Any(r => IsGameDataTrigger(r.TriggerType));
            _ruleCache = rules;
            _ruleCacheDirty = false;
            _logger.LogInformation("AutomationEngine rule cache reloaded - {Count} rules", rules.Count);
        }
        finally
        {
            _ruleCacheLock.Release();
        }
    }

    private async Task EnsureEnabledInitializedAsync(CancellationToken ct)
    {
        // The three hosted services can arrive here together on startup. They all await the
        // same cached task (a lost race at most issues one extra read, which is harmless), and
        // no thread blocks on the DB call. The shared read isn't tied to any one caller's token.
        Task<bool> initTask = LazyInitializer.EnsureInitialized(ref _initTask, () => InitializeEnabledAsync(CancellationToken.None));

        if (!await initTask.WaitAsync(ct))
        {
            Interlocked.CompareExchange(ref _initTask, null, initTask);
        }
    }

    public async Task ProcessTransitionAsync(GameStatusTransitionEvent ev, CancellationToken ct)
    {
        await EnsureEnabledInitializedAsync(ct);

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
            // A status edge happens once, so a skip consumes it - the rule waits for the next edge.
            AutomationConditionCheck check = CheckConditions(rule, _timeProvider.GetUtcNow().UtcDateTime);

            if (!check.Passed)
            {
                await WriteSkippedExecutionAsync(rule, ev.ClientId, ev.NewStatus.ToString(), check, ct);
                continue;
            }

            await ExecuteRuleAsync(rule, ev.ClientId, ev.NewStatus.ToString(), ct);
        }
    }

    private static bool IsGameDataTrigger(AutomationTriggerType triggerType) =>
        triggerType is AutomationTriggerType.TimeRemainingBelow or AutomationTriggerType.ScoreThreshold;

    /// <summary>
    /// Fires TimeRemainingBelow and ScoreThreshold rules for the kiosk's latest snapshot, each at
    /// most once per game.
    /// </summary>
    /// <remarks>
    /// Called by GameDataTriggerHostedService, the only consumer of the game-data channel, so the
    /// per-rule dictionaries need no locking.
    /// </remarks>
    public async Task ProcessGameDataAsync(GameDataEvent ev, CancellationToken ct)
    {
        // Cleared before reading, so a publish that lands while this runs queues another event.
        _gameDataPending.TryRemove(ev.ClientId, out _);

        if (!_latestGameData.TryGetValue(ev.ClientId, out GameSnapshot? snapshot))
        {
            return;
        }

        await EnsureEnabledInitializedAsync(ct);

        if (!_isEnabled)
        {
            return;
        }

        if (_ruleCacheDirty)
        {
            await ReloadRuleCacheAsync(ct);
        }

        // Only a game in progress counts: after a game the kiosk zeroes the countdown and keeps
        // republishing the finished scores, neither of which is a new crossing.
        if (snapshot.Status != GameStatus.InGame || snapshot.GameNumber == 0)
        {
            return;
        }

        List<AutomationRule> rules = _ruleCache
            .Where(r => IsGameDataTrigger(r.TriggerType)
                && r.TriggerClientId == snapshot.ClientId
                && r.TriggerThreshold.HasValue)
            .ToList();

        foreach (AutomationRule rule in rules)
        {
            bool crossed = rule.TriggerType == AutomationTriggerType.TimeRemainingBelow
                ? HasTimeRemainingCrossed(rule, snapshot)
                : IsScoreThresholdReached(rule.TriggerScoreScope ?? AutomationScoreScope.AnyPlayer, rule.TriggerTeam, rule.TriggerThreshold!.Value, snapshot.Players);

            if (!crossed)
            {
                continue;
            }

            if (_gameDataFiredForGame.TryGetValue(rule.Id, out int firedForGame) && firedForGame == snapshot.GameNumber)
            {
                continue;
            }

            // Marked before running: a moment in a game happens once, so neither a skip nor a
            // failed action is retried on the next publish.
            _gameDataFiredForGame[rule.Id] = snapshot.GameNumber;

            string label = rule.TriggerType.ToString();
            AutomationConditionCheck check = CheckConditions(rule, _timeProvider.GetUtcNow().UtcDateTime);

            if (!check.Passed)
            {
                await WriteSkippedExecutionAsync(rule, snapshot.ClientId, label, check, ct);
                continue;
            }

            _logger.LogInformation("Rule {RuleId} {TriggerType} reached on client {ClientId} in game {GameNumber} - firing", rule.Id, rule.TriggerType, snapshot.ClientId, snapshot.GameNumber);

            await ExecuteRuleAsync(rule, snapshot.ClientId, label, ct);
        }
    }

    // Needs a reading above the threshold earlier in the same game: a kiosk reports 0 before its
    // first countdown packet, and without this every rule would fire the moment a game starts.
    private bool HasTimeRemainingCrossed(AutomationRule rule, GameSnapshot snapshot)
    {
        int threshold = rule.TriggerThreshold!.Value;
        int? previous = _timeRemainingObserved.TryGetValue(rule.Id, out (int GameNumber, int Seconds) observed) && observed.GameNumber == snapshot.GameNumber
            ? observed.Seconds
            : null;

        _timeRemainingObserved[rule.Id] = (snapshot.GameNumber, snapshot.TimeRemainingSeconds);

        return previous > threshold && snapshot.TimeRemainingSeconds <= threshold;
    }

    /// <summary>
    /// Pure so the scoring rules can be unit tested without the engine. Scores only go up during a
    /// game, so a coalesced (skipped) publish can't hide a crossing.
    /// </summary>
    public static bool IsScoreThresholdReached(AutomationScoreScope scope, Team? team, int threshold, IReadOnlyList<PlayerScoreSnapshot> players)
    {
        if (scope == AutomationScoreScope.AnyPlayer)
        {
            return players.Any(p => (team == null || p.Team == team) && p.Score >= threshold);
        }

        // A team's total is the sum of its players' scores; players with no team don't count.
        return players
            .Where(p => p.Team != null && (team == null || p.Team == team))
            .GroupBy(p => p.Team)
            .Any(g => g.Sum(p => p.Score) >= threshold);
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
        await EnsureEnabledInitializedAsync(ct);

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

            AutomationConditionCheck check = CheckConditions(rule, nowUtc);

            if (!check.Passed)
            {
                // Not recorded as an attempt, so the rule fires on the first tick the conditions
                // hold while the client is still idle (e.g. "idle 30 minutes, only after 22:00").
                if (!_idleSkipLoggedForStretch.TryGetValue(rule.Id, out DateTime skipLoggedForStretch)
                    || skipLoggedForStretch != lastTransitionUtc)
                {
                    _idleSkipLoggedForStretch[rule.Id] = lastTransitionUtc;
                    await WriteSkippedExecutionAsync(rule, rule.TriggerClientId, nameof(AutomationTriggerType.ClientIdle), check, ct);
                }

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
    /// IdleTriggerHostedService. nowLocal is venue wall-clock time (RotaTime.ToVenueLocal), not UTC
    /// and not the server's own zone.
    /// </remarks>
    public async Task ProcessScheduledRulesAsync(DateTime nowLocal, CancellationToken ct)
    {
        await EnsureEnabledInitializedAsync(ct);

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

            AutomationConditionCheck check = CheckConditions(rule, _timeProvider.GetUtcNow().UtcDateTime);

            if (!check.Passed)
            {
                // Not marked fired, so a later tick inside the catch-up window can still run it.
                if (!_scheduledSkipLoggedForLocalDate.TryGetValue(rule.Id, out DateOnly skipLoggedForDate) || skipLoggedForDate != today)
                {
                    _scheduledSkipLoggedForLocalDate[rule.Id] = today;
                    await WriteSkippedExecutionAsync(rule, rule.TriggerClientId, nameof(AutomationTriggerType.Scheduled), check, ct);
                }

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
                DateOnly lastSuccessLocalDate = RotaTime.LocalDate(lastSuccess.ExecutedAt);
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

    private AutomationConditionCheck CheckConditions(AutomationRule rule, DateTime nowUtc)
    {
        // Every rule saved before conditions existed takes this path, unchanged.
        if (!rule.Conditions.Any(c => c.IsActive))
        {
            return AutomationConditionCheck.Pass;
        }

        Dictionary<Guid, AutomationClientInfo> clients = _conditionClients;
        Dictionary<int, AutomationLocationInfo> locations = _conditionLocations;

        return AutomationConditionEvaluator.Evaluate(
            rule.Conditions,
            rule.CompanyId,
            nowUtc,
            clientId => clients.GetValueOrDefault(clientId),
            IsClientInGame,
            locationId => locations.GetValueOrDefault(locationId));
    }

    // The status store, not _lastKnownStatus: the hub clears the store when a kiosk disconnects,
    // so a kiosk that drops mid-game doesn't stay "in game" forever.
    private bool IsClientInGame(Guid clientId)
    {
        return _laserGameStatusStore.TryGetStatus(clientId, out LaserGameStatusDTO? status)
            && status?.Status == GameStatus.InGame;
    }

    /// <summary>
    /// Records that the trigger fired but a condition stopped the rule, so "why didn't my rule run"
    /// can be answered from the execution log. Does not raise OnRuleExecuted - nothing ran.
    /// </summary>
    private async Task WriteSkippedExecutionAsync(AutomationRule rule, Guid clientId, string triggerEventLabel, AutomationConditionCheck check, CancellationToken ct)
    {
        string reason = check.Reason ?? "A condition wasn't met";

        if (check.IsMisconfigured)
        {
            _logger.LogWarning("Rule {RuleId} skipped - a condition is misconfigured: {Reason}", rule.Id, reason);
        }
        else
        {
            _logger.LogInformation("Rule {RuleId} skipped - condition not met: {Reason}", rule.Id, reason);
        }

        AutomationRuleExecution execution = new()
        {
            Id = Guid.NewGuid(),
            AutomationRuleId = rule.Id,
            RuleName = rule.Name,
            ExecutedAt = DateTime.UtcNow,
            TriggerEvent = triggerEventLabel,
            TriggerClientId = clientId,
            OverallSuccess = false,
            SkipReason = reason
        };

        try
        {
            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(ct);
            ctx.AutomationRuleExecutions.Add(execution);
            await ctx.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write skipped execution log for rule {RuleId}", rule.Id);
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
