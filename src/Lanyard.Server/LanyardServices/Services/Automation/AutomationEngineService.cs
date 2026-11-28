#nullable enable

using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Application.Services.Scheduling;
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
    IOperationsAlertWriter alertWriter,
    TimeProvider timeProvider,
    ILogger<AutomationEngineService> logger,
    IPartyScheduleProvider partySchedule) : IAutomationEngineService
{
    private readonly ISystemDbContextFactory _contextFactory = contextFactory;
    private readonly IEnumerable<IActionExecutor> _actionExecutors = actionExecutors;
    private readonly ILaserGameStatusStore _laserGameStatusStore = laserGameStatusStore;
    private readonly IOperationsAlertWriter _alertWriter = alertWriter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<AutomationEngineService> _logger = logger;
    private readonly IPartyScheduleProvider _partySchedule = partySchedule;

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

    // Party-time rules fire once per (rule, party). The value is when the party's fire time was,
    // so entries for long-gone parties can be dropped. Kept outside the rule cache, so a reload
    // never forgets a firing.
    private readonly ConcurrentDictionary<(Guid RuleId, Guid PartyId), DateTime> _partyFired = new();

    // When each party-time rule last ran successfully. Every party already due at that instant
    // was merged into that run, so after a restart this (reseeded from the log, which only keeps
    // the first party's id) also covers the "+N other" parties.
    private readonly ConcurrentDictionary<Guid, DateTime> _partyLastRunUtc = new();
    private readonly ConcurrentDictionary<Guid, byte> _partySeeded = new();

    // A condition skip is logged once per rule and party, not on every tick of the window.
    private readonly ConcurrentDictionary<(Guid RuleId, Guid PartyId), byte> _partySkipLogged = new();

    // Rules being test-run right now, so a double click can't run the same actions twice at once.
    private readonly ConcurrentDictionary<Guid, byte> _testRunsInProgress = new();

    private volatile bool _isEnabled = false;
    // Every save bumps _ruleCacheVersion; a reload records the version it started from. A save that
    // lands while a reload is still reading the old rules leaves the two different, so the cache
    // stays dirty and the next caller reloads again. A bool cleared at the end of the reload would
    // swallow that save and hide the new rule until some other save came along.
    private long _ruleCacheVersion = 1;
    private long _loadedRuleCacheVersion;
    private bool IsRuleCacheDirty => Interlocked.Read(ref _ruleCacheVersion) != Interlocked.Read(ref _loadedRuleCacheVersion);
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
        Interlocked.Increment(ref _ruleCacheVersion);
    }

    public void NotifyRuleEnabledChanged(Guid ruleId, bool isEnabled)
    {
        OnRuleEnabledChanged?.Invoke(ruleId, isEnabled);
    }

    /// <param name="status">
    /// The full publish, when the caller has it (the hub does). Only used on the edge that ends a
    /// game, to give GameEnded rules the final scores from that same publish.
    /// </param>
    public void EnqueueTransition(Guid clientId, GameStatus newStatus, LaserGameStatusDTO? status = null)
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

        // The kiosk publishes the ended status before clearing anything, and keeps its scores until
        // the next game starts, so this publish carries the finished game's final scores.
        GameSnapshot? finalSnapshot = previousStatus == GameStatus.InGame && newStatus == GameStatus.NotStarted && status != null
            ? GameSnapshot.From(clientId, _gameNumber.GetValueOrDefault(clientId), status)
            : null;

        GameStatusTransitionEvent ev = new(clientId, previousStatus, newStatus, finalSnapshot);
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
        if (!_hasGameDataRules && !IsRuleCacheDirty)
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
            if (!IsRuleCacheDirty) return; // double-check inside lock

            // Read before the query: anything saved from here on bumps the version past this.
            long loadingVersion = Interlocked.Read(ref _ruleCacheVersion);
            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(ct);
            List<AutomationRule> rules = await ctx.AutomationRules
                .Where(r => r.IsActive && r.IsEnabled)
                .Include(r => r.Actions.Where(a => a.IsActive))
                .Include(r => r.Conditions.Where(c => c.IsActive))
                .AsNoTracking()
                .TagWithCallSite()
                .ToListAsync(ct);

            (Dictionary<Guid, AutomationClientInfo> conditionClients, Dictionary<int, AutomationLocationInfo> conditionLocations) =
                await LoadConditionContextAsync(ctx, rules, RotaTime.Today(_timeProvider.GetUtcNow().UtcDateTime), ct);

            // Kiosks and locations first, so a rule is never visible without what its conditions name.
            _conditionClients = conditionClients;
            _conditionLocations = conditionLocations;
            _hasGameDataRules = rules.Any(r => IsGameDataTrigger(r.TriggerType));
            _ruleCache = rules;
            Interlocked.Exchange(ref _loadedRuleCacheVersion, loadingVersion);
            _logger.LogInformation("AutomationEngine rule cache reloaded - {Count} rules", rules.Count);
        }
        finally
        {
            _ruleCacheLock.Release();
        }
    }

    /// <summary>
    /// The kiosks and locations the rules' conditions name. Shared by the rule cache and test runs,
    /// which load one rule that may not be in the cache (it only holds enabled rules).
    /// </summary>
    private static async Task<(Dictionary<Guid, AutomationClientInfo> Clients, Dictionary<int, AutomationLocationInfo> Locations)> LoadConditionContextAsync(
        ApplicationDbContext ctx, List<AutomationRule> rules, DateOnly venueToday, CancellationToken ct)
    {
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
            .Concat(PartyConditionLocationIds(rules.SelectMany(r => r.Conditions)))
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

        // Holidays from yesterday on (a late window can start on a closed day), with no end: the
        // cache lives across days, and past holidays simply stop matching. Saving a holiday or
        // switching Staff Scheduling clears the cache, so new ones are picked up.
        if (conditionLocations.Count > 0)
        {
            Dictionary<int, List<RotaHoliday>> holidays = await RotaHolidayQueries.ForLocationsAsync(
                ctx, conditionLocations.Keys, venueToday.AddDays(-1), DateOnly.MaxValue, ct);

            foreach ((int id, List<RotaHoliday> list) in holidays)
            {
                conditionLocations[id] = conditionLocations[id] with { Holidays = list };
            }
        }

        return (conditionClients, conditionLocations);
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
        if (IsRuleCacheDirty)
        {
            await ReloadRuleCacheAsync(ct);
        }

        // Filter rules matching this transition
        // The TriggerType clause matters: an idle rule keeps whatever stale TriggerEvent it was
        // saved with, and without this it would also fire on a matching transition.
        List<(AutomationRule Rule, string Label)> matchingRules = _ruleCache
            .Where(r => r.TriggerType == AutomationTriggerType.GameStatusTransition
                && r.TriggerClientId == ev.ClientId
                && r.TriggerEvent == ev.NewStatus)
            .Select(r => (r, ev.NewStatus.ToString()))
            .ToList();

        // A game that ended with one team ahead also fires GameEnded rules for that team (or any).
        if (ev.FinalSnapshot is GameSnapshot final && GameWinner.Determine(final.Players) is Team winner)
        {
            string label = $"{nameof(AutomationTriggerType.GameEnded)} ({winner} won)";

            matchingRules.AddRange(_ruleCache
                .Where(r => r.TriggerType == AutomationTriggerType.GameEnded
                    && r.TriggerClientId == ev.ClientId
                    && (r.TriggerTeam == null || r.TriggerTeam == winner))
                .Select(r => (r, label)));
        }

        if (matchingRules.Count == 0)
        {
            return;
        }

        // Process each matching rule
        foreach ((AutomationRule rule, string label) in matchingRules)
        {
            // A status edge happens once, so a skip consumes it - the rule waits for the next edge.
            AutomationConditionCheck check = await CheckConditionsAsync(rule, _timeProvider.GetUtcNow().UtcDateTime, ct);

            if (!check.Passed)
            {
                await WriteSkippedExecutionAsync(rule, ev.ClientId, label, check, ct);
                continue;
            }

            await ExecuteRuleAsync(rule, ev.ClientId, label, ct);
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

        if (IsRuleCacheDirty)
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
            AutomationConditionCheck check = await CheckConditionsAsync(rule, _timeProvider.GetUtcNow().UtcDateTime, ct);

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

        if (IsRuleCacheDirty)
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

            AutomationConditionCheck check = await CheckConditionsAsync(rule, nowUtc, ct);

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

        if (IsRuleCacheDirty)
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

            AutomationConditionCheck check = await CheckConditionsAsync(rule, _timeProvider.GetUtcNow().UtcDateTime, ct);

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
                .Where(e => e.AutomationRuleId == ruleId && e.OverallSuccess && !e.IsTestRun)
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

    /// <summary>
    /// Fires each PartyMilestone rule whose party moment (minus its lead minutes) has just come
    /// round, once per party. Parties reaching it in the same tick fire the rule once.
    /// </summary>
    /// <remarks>
    /// Called every minute by PartyTriggerHostedService. Nothing is scheduled ahead: party times are
    /// read fresh from IPartyScheduleProvider (a minute's cache, cleared on every booking save), so
    /// a party moved or cancelled before its moment simply fires at the new time or not at all. A
    /// party moved after its rule fired doesn't fire it again.
    /// </remarks>
    public async Task ProcessPartyRulesAsync(DateTime nowUtc, CancellationToken ct)
    {
        await EnsureEnabledInitializedAsync(ct);

        if (!_isEnabled)
        {
            return;
        }

        if (IsRuleCacheDirty)
        {
            await ReloadRuleCacheAsync(ct);
        }

        List<AutomationRule> partyRules = _ruleCache
            .Where(r => r.TriggerType == AutomationTriggerType.PartyMilestone
                && r.PartyLocationId is > 0
                && r.PartyMilestone.HasValue
                && r.PartyLeadMinutes.HasValue)
            .ToList();

        if (partyRules.Count == 0)
        {
            return;
        }

        PrunePartyState(nowUtc);

        Dictionary<(int CompanyId, int LocationId), IReadOnlyList<PartySlot>?> partiesByLocation = [];

        foreach (AutomationRule rule in partyRules)
        {
            (int, int) locationKey = (rule.CompanyId, rule.PartyLocationId!.Value);

            if (!partiesByLocation.TryGetValue(locationKey, out IReadOnlyList<PartySlot>? parties))
            {
                Infrastructure.DTO.Result<IReadOnlyList<PartySlot>> loaded = await _partySchedule.GetDayAsync(rule.CompanyId, rule.PartyLocationId.Value, nowUtc, ct);

                if (!loaded.IsSuccess)
                {
                    _logger.LogWarning("Couldn't load parties for location {LocationId}; party rules there wait for the next tick: {Error}", rule.PartyLocationId, loaded.Error);
                }

                parties = loaded.IsSuccess ? loaded.Data : null;
                partiesByLocation[locationKey] = parties;
            }

            if (parties is null || parties.Count == 0)
            {
                continue;
            }

            await EnsurePartyRuleSeededAsync(rule.Id, nowUtc, ct);

            PartyMilestone milestone = rule.PartyMilestone!.Value;
            int lead = rule.PartyLeadMinutes!.Value;
            DateTime? lastRun = _partyLastRunUtc.TryGetValue(rule.Id, out DateTime last) ? last : null;

            // Earliest first, so the run is logged against the party whose moment came first.
            List<(PartySlot Party, DateTime FireAtUtc)> due = parties
                .Where(p => PartyRooms.Matches(rule.PartyRoom, p.Room))
                .Select(p => (Party: p, Milestone: PartyTriggerRules.MilestoneUtc(p, milestone)))
                .Where(x => x.Milestone is DateTime m && PartyTriggerRules.IsDue(m, lead, nowUtc, ScheduleCatchUpWindow))
                .Select(x => (x.Party, FireAtUtc: x.Milestone!.Value.AddMinutes(-lead)))
                .Where(x => !_partyFired.ContainsKey((rule.Id, x.Party.Id)) && !(lastRun is DateTime ran && x.FireAtUtc <= ran))
                .OrderBy(x => x.FireAtUtc)
                .ThenBy(x => x.Party.CreateDate)
                .ToList();

            if (due.Count == 0)
            {
                continue;
            }

            PartySlot first = due[0].Party;
            string label = PartyTriggerRules.ExecutionLabel(milestone, lead, due.Count - 1);
            AutomationConditionCheck check = await CheckConditionsAsync(rule, _timeProvider.GetUtcNow().UtcDateTime, ct);

            if (!check.Passed)
            {
                // Not marked fired: a later tick inside the window can still run it.
                if (_partySkipLogged.TryAdd((rule.Id, first.Id), 0))
                {
                    await WriteSkippedExecutionAsync(rule, rule.TriggerClientId, label, check, ct, first.Id);
                }

                continue;
            }

            _logger.LogInformation("Party rule {RuleId} due for party {PartyBookingId} and {OtherCount} other(s) - firing", rule.Id, first.Id, due.Count - 1);

            bool succeeded = await ExecuteRuleAsync(rule, rule.TriggerClientId, label, ct, first.Id);

            if (succeeded)
            {
                foreach ((PartySlot party, DateTime fireAtUtc) in due)
                {
                    _partyFired[(rule.Id, party.Id)] = fireAtUtc;
                }

                _partyLastRunUtc[rule.Id] = nowUtc;
            }
        }
    }

    // A day's grace past the latest possible catch-up, so nothing still due is ever forgotten.
    private void PrunePartyState(DateTime nowUtc)
    {
        DateTime cutoff = nowUtc.AddDays(-1);

        foreach (KeyValuePair<(Guid RuleId, Guid PartyId), DateTime> entry in _partyFired)
        {
            if (entry.Value < cutoff)
            {
                _partyFired.TryRemove(entry.Key, out _);
                _partySkipLogged.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>
    /// Reseeds the last successful run from the execution log the first time a party rule is
    /// evaluated in this process, so a restart straight after a party rule fired doesn't fire it
    /// again inside the catch-up window. Mirrors EnsureScheduledRuleSeededAsync.
    /// </summary>
    private async Task EnsurePartyRuleSeededAsync(Guid ruleId, DateTime nowUtc, CancellationToken ct)
    {
        if (_partySeeded.ContainsKey(ruleId))
        {
            return;
        }

        try
        {
            DateTime since = nowUtc.AddDays(-1);

            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(ct);
            List<(Guid PartyBookingId, DateTime ExecutedAt)> runs = (await ctx.AutomationRuleExecutions
                .Where(e => e.AutomationRuleId == ruleId && e.OverallSuccess && e.PartyBookingId != null && e.ExecutedAt >= since)
                .Select(e => new { PartyBookingId = e.PartyBookingId!.Value, e.ExecutedAt })
                .AsNoTracking()
                .TagWithCallSite()
                .ToListAsync(ct))
                .Select(e => (e.PartyBookingId, e.ExecutedAt))
                .ToList();

            foreach ((Guid partyId, DateTime executedAt) in runs)
            {
                _partyFired.TryAdd((ruleId, partyId), executedAt);
            }

            if (runs.Count > 0)
            {
                DateTime latest = runs.Max(r => r.ExecutedAt);
                _partyLastRunUtc.AddOrUpdate(ruleId, latest, (_, current) => current > latest ? current : latest);
            }

            // Only after the query succeeded, so a DB blip is retried on the next tick.
            _partySeeded.TryAdd(ruleId, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to reseed party runs for rule {RuleId}", ruleId);
        }
    }

    private async Task<AutomationConditionCheck> CheckConditionsAsync(AutomationRule rule, DateTime nowUtc, CancellationToken ct)
    {
        return await CheckConditionsAsync(rule, nowUtc, _conditionClients, _conditionLocations, ct);
    }

    private async Task<AutomationConditionCheck> CheckConditionsAsync(
        AutomationRule rule,
        DateTime nowUtc,
        Dictionary<Guid, AutomationClientInfo> clients,
        Dictionary<int, AutomationLocationInfo> locations,
        CancellationToken ct)
    {
        // Every rule saved before conditions existed takes this path, unchanged.
        if (!rule.Conditions.Any(c => c.IsActive))
        {
            return AutomationConditionCheck.Pass;
        }

        // The evaluator is pure and synchronous, so today's parties for any party conditions are
        // read first (from the provider's one-minute cache, so this is rarely a query). A failed
        // read leaves the location out, which the evaluator treats as "couldn't be loaded".
        Dictionary<int, IReadOnlyList<PartySlot>> parties = [];

        foreach (int locationId in PartyConditionLocationIds(rule.Conditions))
        {
            Infrastructure.DTO.Result<IReadOnlyList<PartySlot>> loaded = await _partySchedule.GetDayAsync(rule.CompanyId, locationId, nowUtc, ct);

            if (loaded.IsSuccess && loaded.Data is not null)
            {
                parties[locationId] = loaded.Data;
            }
            else
            {
                _logger.LogWarning("Couldn't load parties at location {LocationId} for rule {RuleId}'s condition: {Error}", locationId, rule.Id, loaded.Error);
            }
        }

        return CheckConditions(rule, nowUtc, clients, locations, parties);
    }

    private AutomationConditionCheck CheckConditions(
        AutomationRule rule,
        DateTime nowUtc,
        Dictionary<Guid, AutomationClientInfo> clients,
        Dictionary<int, AutomationLocationInfo> locations,
        Dictionary<int, IReadOnlyList<PartySlot>> parties)
    {
        // Every rule saved before conditions existed takes this path, unchanged.
        if (!rule.Conditions.Any(c => c.IsActive))
        {
            return AutomationConditionCheck.Pass;
        }

        return AutomationConditionEvaluator.Evaluate(
            rule.Conditions,
            rule.CompanyId,
            nowUtc,
            clientId => clients.GetValueOrDefault(clientId),
            IsClientInGame,
            locationId => locations.GetValueOrDefault(locationId),
            locationId => parties.GetValueOrDefault(locationId));
    }

    private static IEnumerable<int> PartyConditionLocationIds(IEnumerable<AutomationRuleCondition> conditions) => conditions
        .Where(c => c.IsActive && c.ConditionType == AutomationConditionType.PartyInProgress)
        .Select(c => AutomationConditionParameters.TryParse(c.ParametersJson, out PartyInProgressConditionParameters? p) ? p.LocationId : 0)
        .Where(id => id > 0)
        .Distinct();

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
    /// <returns>The log row's id.</returns>
    private async Task<Guid> WriteSkippedExecutionAsync(
        AutomationRule rule,
        Guid clientId,
        string triggerEventLabel,
        AutomationConditionCheck check,
        CancellationToken ct,
        Guid? partyBookingId = null,
        string? testRunByUserId = null)
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
            SkipReason = reason,
            PartyBookingId = partyBookingId,
            IsTestRun = testRunByUserId != null,
            TestRunByUserId = testRunByUserId
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

        return execution.Id;
    }

    /// <summary>Returns whether every action in the rule succeeded.</summary>
    private async Task<bool> ExecuteRuleAsync(AutomationRule rule, Guid clientId, string triggerEventLabel, CancellationToken ct, Guid? partyBookingId = null)
    {
        List<AutomationRuleActionExecution> actionExecutions = await RunActionsAsync(rule, clientId, ct);

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
            ActionExecutions = actionExecutions,
            PartyBookingId = partyBookingId
        };

        if (await WriteExecutionAsync(execution, ct))
        {
            OnRuleExecuted?.Invoke(rule.Id, execution.ExecutedAt, execution.OverallSuccess);
        }

        await UpdateAlertsPageAsync(rule, clientId, actionExecutions, ct);

        return execution.OverallSuccess;
    }

    private async Task<bool> WriteExecutionAsync(AutomationRuleExecution execution, CancellationToken ct)
    {
        try
        {
            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(ct);
            ctx.AutomationRuleExecutions.Add(execution);
            await ctx.SaveChangesAsync(ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write execution log for rule {RuleId}", execution.AutomationRuleId);

            return false;
        }
    }

    /// <summary>
    /// Runs the rule's actions in order, each one isolated from the others' failures, and returns a
    /// result row per action. Shared by real triggers and test runs.
    /// </summary>
    private async Task<List<AutomationRuleActionExecution>> RunActionsAsync(AutomationRule rule, Guid clientId, CancellationToken ct)
    {
        List<AutomationRuleActionExecution> actionExecutions = [];
        List<AutomationRuleAction> actions = rule.Actions.Where(a => a.IsActive).OrderBy(a => a.SortOrder).ToList();
        ActionTargetCompanies companies = await LoadActionTargetCompaniesAsync(actions, ct);

        foreach (AutomationRuleAction action in actions)
        {
            // An action naming another company's kiosk, playlist, program, scene or clip never
            // reaches its executor. The executors look these up through the unfiltered system
            // context, so this is the check that keeps a rule inside its own company.
            string? refused = CheckActionTargets(rule, action, companies);

            if (refused != null)
            {
                actionExecutions.Add(new AutomationRuleActionExecution
                {
                    Id = Guid.NewGuid(),
                    AutomationRuleActionId = action.Id,
                    Success = false,
                    ErrorMessage = refused
                });
                _logger.LogWarning("Refused action {ActionId} on rule {RuleId}: {Reason}", action.Id, rule.Id, refused);
                continue;
            }

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

        return actionExecutions;
    }

    private sealed record ActionTargetCompanies(
        Dictionary<Guid, int> Clients,
        Dictionary<(AutomationActionTargetKind Kind, Guid Id), int> References);

    // One query per kind of row, for the whole rule, rather than one per action.
    private async Task<ActionTargetCompanies> LoadActionTargetCompaniesAsync(List<AutomationRuleAction> actions, CancellationToken ct)
    {
        List<AutomationActionTarget> targets = actions
            .Select(a => AutomationActionTargets.TryRead(a.ActionType, a.ParametersJson, out AutomationActionTarget t) ? t : null)
            .OfType<AutomationActionTarget>()
            .ToList();

        List<Guid> clientIds = targets.Select(t => t.ClientId).Where(id => id != Guid.Empty).Distinct().ToList();

        List<Guid> IdsOf(AutomationActionTargetKind kind) => targets
            .SelectMany(t => t.References)
            .Where(r => r.Kind == kind)
            .Select(r => r.Id)
            .Distinct()
            .ToList();

        List<Guid> playlistIds = IdsOf(AutomationActionTargetKind.Playlist);
        List<Guid> programIds = IdsOf(AutomationActionTargetKind.ProjectionProgram);
        List<Guid> sceneIds = IdsOf(AutomationActionTargetKind.DmxScene);
        List<Guid> clipIds = IdsOf(AutomationActionTargetKind.AudioClip);

        Dictionary<Guid, int> clients = [];
        Dictionary<(AutomationActionTargetKind, Guid), int> references = [];

        if (clientIds.Count == 0)
        {
            return new ActionTargetCompanies(clients, references);
        }

        await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(ct);

        clients = await ctx.Clients
            .Where(c => clientIds.Contains(c.Id))
            .AsNoTracking()
            .TagWithCallSite()
            .ToDictionaryAsync(c => c.Id, c => c.CompanyId, ct);

        if (playlistIds.Count > 0)
        {
            foreach (var row in await ctx.Playlists.Where(x => playlistIds.Contains(x.Id)).Select(x => new { x.Id, x.CompanyId }).AsNoTracking().TagWithCallSite().ToListAsync(ct))
            {
                references[(AutomationActionTargetKind.Playlist, row.Id)] = row.CompanyId;
            }
        }

        if (programIds.Count > 0)
        {
            foreach (var row in await ctx.ProjectionPrograms.Where(x => programIds.Contains(x.Id)).Select(x => new { x.Id, x.CompanyId }).AsNoTracking().TagWithCallSite().ToListAsync(ct))
            {
                references[(AutomationActionTargetKind.ProjectionProgram, row.Id)] = row.CompanyId;
            }
        }

        if (sceneIds.Count > 0)
        {
            foreach (var row in await ctx.DmxScenes.Where(x => sceneIds.Contains(x.Id)).Select(x => new { x.Id, x.CompanyId }).AsNoTracking().TagWithCallSite().ToListAsync(ct))
            {
                references[(AutomationActionTargetKind.DmxScene, row.Id)] = row.CompanyId;
            }
        }

        if (clipIds.Count > 0)
        {
            foreach (var row in await ctx.AudioClips.Where(x => clipIds.Contains(x.Id)).Select(x => new { x.Id, x.CompanyId }).AsNoTracking().TagWithCallSite().ToListAsync(ct))
            {
                references[(AutomationActionTargetKind.AudioClip, row.Id)] = row.CompanyId;
            }
        }

        return new ActionTargetCompanies(clients, references);
    }

    /// <returns>Why the action mustn't run, or null when it may.</returns>
    /// <remarks>
    /// Only refuses what is definitely another company's. A missing kiosk or row is left to the
    /// executor, which already reports it ("Client not connected", "no longer exists"...).
    /// </remarks>
    private static string? CheckActionTargets(AutomationRule rule, AutomationRuleAction action, ActionTargetCompanies companies)
    {
        if (!AutomationActionTargets.TryRead(action.ActionType, action.ParametersJson, out AutomationActionTarget target))
        {
            return "The action's settings couldn't be read. Remove it and add it again.";
        }

        if (target.ClientId != Guid.Empty
            && companies.Clients.TryGetValue(target.ClientId, out int clientCompanyId)
            && clientCompanyId != rule.CompanyId)
        {
            return "The action's kiosk belongs to a different company";
        }

        foreach ((AutomationActionTargetKind kind, Guid id) in target.References)
        {
            if (companies.References.TryGetValue((kind, id), out int companyId) && companyId != rule.CompanyId)
            {
                return $"The action's {AutomationActionTargets.Describe(kind)} belongs to a different company";
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<Result<AutomationTestRunResult>> TestRunRuleAsync(AutomationTestRunRequest request, CancellationToken ct = default)
    {
        if (!_testRunsInProgress.TryAdd(request.RuleId, 0))
        {
            return Result<AutomationTestRunResult>.Fail("A test run of this rule is already in progress.");
        }

        try
        {
            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(ct);

            // The system context isn't tenant-filtered, so the caller's company is checked here
            // rather than trusting a rule id from a signed-in circuit.
            AutomationRule? rule = await ctx.AutomationRules
                .Where(r => r.Id == request.RuleId && r.IsActive)
                .Include(r => r.Actions.Where(a => a.IsActive))
                .Include(r => r.Conditions.Where(c => c.IsActive))
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(ct);

            if (rule == null || rule.CompanyId != request.CallerCompanyId)
            {
                return Result<AutomationTestRunResult>.Fail("Rule not found.");
            }

            if (!rule.IsEnabled)
            {
                return Result<AutomationTestRunResult>.Fail("Switch the rule on before testing it.");
            }

            if (rule.Actions.Count == 0)
            {
                return Result<AutomationTestRunResult>.Fail("This rule has no actions to run.");
            }

            string label = $"Manual test by {request.UserDisplayName}";

            if (request.CheckConditions)
            {
                (Dictionary<Guid, AutomationClientInfo> clients, Dictionary<int, AutomationLocationInfo> locations) =
                    await LoadConditionContextAsync(ctx, [rule], RotaTime.Today(_timeProvider.GetUtcNow().UtcDateTime), ct);

                AutomationConditionCheck check = await CheckConditionsAsync(rule, _timeProvider.GetUtcNow().UtcDateTime, clients, locations, ct);

                if (!check.Passed)
                {
                    Guid skippedId = await WriteSkippedExecutionAsync(
                        rule,
                        rule.TriggerClientId,
                        label,
                        check,
                        ct,
                        testRunByUserId: request.UserId);

                    return Result<AutomationTestRunResult>.Ok(new AutomationTestRunResult(
                        skippedId, check.Reason ?? "A condition wasn't met", []));
                }
            }

            // Deliberately none of the idle, schedule or game-data bookkeeping, and no alerts or
            // OnRuleExecuted: a test mustn't stop the real trigger firing, or look like it fired.
            List<AutomationRuleActionExecution> actionExecutions = await RunActionsAsync(rule, rule.TriggerClientId, ct);

            AutomationRuleExecution execution = new()
            {
                Id = Guid.NewGuid(),
                AutomationRuleId = rule.Id,
                RuleName = rule.Name,
                ExecutedAt = DateTime.UtcNow,
                TriggerEvent = label,
                TriggerClientId = rule.TriggerClientId,
                OverallSuccess = actionExecutions.All(a => a.Success),
                IsTestRun = true,
                TestRunByUserId = request.UserId,
                ActionExecutions = actionExecutions
            };

            await WriteExecutionAsync(execution, ct);

            int passed = actionExecutions.Count(a => a.Success);
            _logger.LogInformation("Rule {RuleId} test run by {UserId}: {Passed}/{Total} actions passed", rule.Id, request.UserId, passed, actionExecutions.Count);

            Dictionary<Guid, AutomationRuleAction> actionsById = rule.Actions.ToDictionary(a => a.Id);

            List<AutomationTestActionResult> results = actionExecutions
                .Select(ae => new AutomationTestActionResult(
                    ae.AutomationRuleActionId,
                    actionsById[ae.AutomationRuleActionId].ActionType,
                    actionsById[ae.AutomationRuleActionId].ParametersJson,
                    ae.Success,
                    ae.ErrorMessage))
                .ToList();

            return Result<AutomationTestRunResult>.Ok(new AutomationTestRunResult(execution.Id, null, results));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Test run of rule {RuleId} failed", request.RuleId);
            return Result<AutomationTestRunResult>.Fail($"The test couldn't run: {ex.Message}");
        }
        finally
        {
            _testRunsInProgress.TryRemove(request.RuleId, out _);
        }
    }

    // A rule that ran with a failed action is raised on the Operations alerts page (one alert per
    // rule, refreshed while it keeps failing) and cleared the next time it runs cleanly. Skipped
    // runs never get here: a condition not holding isn't a failure. The page is a side effect, so
    // nothing that goes wrong here can stop the engine.
    private async Task UpdateAlertsPageAsync(AutomationRule rule, Guid clientId, List<AutomationRuleActionExecution> actionExecutions, CancellationToken ct)
    {
        string key = $"automation-failed:{rule.Id:N}";

        try
        {
            if (actionExecutions.All(a => a.Success))
            {
                Infrastructure.DTO.Result<bool> cleared = await _alertWriter.ResolveAsync(rule.CompanyId, key, "The rule ran without errors.", ct);

                if (!cleared.IsSuccess)
                {
                    _logger.LogWarning("Couldn't clear the failure alert for rule {RuleId}: {Error}", rule.Id, cleared.Error);
                }

                return;
            }

            // The rule has no location of its own; the kiosk that triggered it says where it is.
            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(ct);

            int? locationId = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == clientId && x.CompanyId == rule.CompanyId)
                .Select(x => x.LocationId)
                .FirstOrDefaultAsync(ct);

            int failed = actionExecutions.Count(a => !a.Success);
            string? firstError = actionExecutions.FirstOrDefault(a => !a.Success)?.ErrorMessage;
            string detail = failed == 1
                ? $"1 of {actionExecutions.Count} actions failed: {firstError ?? "no reason given"}"
                : $"{failed} of {actionExecutions.Count} actions failed. First: {firstError ?? "no reason given"}";

            Infrastructure.DTO.Result<OperationsAlertRaiseOutcome> raised = await _alertWriter.RaiseAsync(new OperationsAlertRaise(
                rule.CompanyId, locationId, OperationsAlertType.AutomationRuleFailed, OperationsAlertSeverity.Warning,
                key, $"Automation rule \"{rule.Name}\" failed", detail, "/automation",
                SubjectClientId: clientId, SubjectRuleId: rule.Id, NotifyTopic: NotificationTopic.AutomationRuleFailed), ct);

            if (!raised.IsSuccess)
            {
                _logger.LogWarning("Couldn't raise a failure alert for rule {RuleId}: {Error}", rule.Id, raised.Error);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error updating the alerts page for rule {RuleId}", rule.Id);
        }
    }
}
