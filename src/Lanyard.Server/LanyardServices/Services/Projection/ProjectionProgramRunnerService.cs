using Lanyard.Application.Services.Projection;
using Lanyard.Application.Services.Projection.Bindings;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Projection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class ProjectionProgramRunnerService(
    ISystemDbContextFactory factory,
    IProjectionBindingResolver bindingResolver,
    ILogger<ProjectionProgramRunnerService> logger,
    ILaserGameStatusStore laserGameStatusStore) : IProjectionProgramRunnerService
{
    private readonly ISystemDbContextFactory _factory = factory;
    private readonly IProjectionBindingResolver _bindingResolver = bindingResolver;
    private readonly ILaserGameStatusStore _laserGameStatusStore = laserGameStatusStore;
    private readonly ILogger<ProjectionProgramRunnerService> _logger = logger;

    private static readonly IReadOnlyDictionary<Guid, string> NoBoundValues = new Dictionary<Guid, string>();
    private static readonly IReadOnlyDictionary<int, ProjectionGameWaitState> NoGameWaits = new Dictionary<int, ProjectionGameWaitState>();

    /// <summary>
    /// How often a held step with data-bound parameters re-resolves them, in wall-clock time (a
    /// paused screen keeps its data current too). Settable for tests.
    /// </summary>
    public TimeSpan BindingRefreshInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// What one unit of a "Wait for Game" step's Give Up After setting is worth: a minute.
    /// Settable so tests can time out in milliseconds.
    /// </summary>
    public TimeSpan GameWaitTimeoutUnit { get; init; } = TimeSpan.FromMinutes(1);

    // Hold, tick and next-step rules live in ProjectionSequence (Lanyard.Shared), so the kiosk's
    // offline player follows the same ones. Paused time doesn't count toward a step's hold, so a
    // plain Task.Delay for the whole hold can't be used - the hold loop re-checks every tick.

    public event Action<Guid, int, Guid>? OnProgramStarted;
    public event Action<Guid, int, int>? OnProgramStepAdvanced;
    public event Action<Guid, int, bool>? OnProgramPauseChanged;
    public event Action<Guid, int, Guid>? OnProgramStopped;
    public event Action<Guid, int, Guid>? OnProgramCompletedNaturally;
    public event Action<Guid, int, int>? OnProgramStepValuesChanged;

    private sealed class RunningProgram
    {
        public required Guid RunId { get; init; }
        public required Guid ProgramId { get; init; }
        public required string ProgramName { get; init; }

        // Snapshot the steps at start: edits mid-run never corrupt playback.
        // CRUD on a running program stops it; restarting picks up the new definition.
        public required List<ProjectionProgramStep> Steps { get; init; }

        public required CancellationTokenSource Cts { get; init; }
        public required bool IsTemporaryTrigger { get; init; }

        // Who data-bound parameters are resolved for: the program's company and the kiosk's location.
        public required ProjectionBindingContext BindingContext { get; init; }

        // "Wait for Game" steps, keyed by step index, with the watched kiosk already checked
        // against the program's company.
        public required IReadOnlyDictionary<int, ProjectionGameWaitState> GameWaits { get; init; }

        public int CurrentStepIndex;

        // The current step's bound values. Kept here, never written onto the step entities: those are
        // shared with every page rendering this run, and with the next pass of the same step.
        // Replaced whole under _lock, never changed in place.
        public IReadOnlyDictionary<Guid, string> BoundValues = NoBoundValues;
        public volatile bool IsPaused;

        // Runs only while the program is unpaused (stopped/started in SetPaused under _lock), so
        // a step's hold is measured in exact played time however a pause lines up with a tick.
        public readonly System.Diagnostics.Stopwatch PlayClock = System.Diagnostics.Stopwatch.StartNew();

        // Set by a skip request, consumed by the run loop when its current hold breaks out.
        public int? PendingStepIndex;
    }

    private readonly Dictionary<(Guid ClientId, int DisplayIndex), RunningProgram> _runningPrograms = [];
    private readonly object _lock = new();

    public async Task<Result<Guid>> StartAsync(Guid clientId, int displayIndex, Guid programId, bool repeatInfinitely, int repeatCount, bool isTemporaryTrigger)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            ProjectionProgram? program = await context.ProjectionPrograms
                .AsNoTracking()
                .TagWithCallSite()
                .Where(p => p.Id == programId && p.IsActive)
                .FirstOrDefaultAsync();

            if (program == null)
            {
                return Result<Guid>.Fail("Projection program not found.");
            }

            // The kiosk page is anonymous and takes both ids from its URL, and this context has no
            // tenant filter, so nothing else stops one company's program playing (and resolving
            // live data) on another company's kiosk.
            var client = await context.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(c => c.Id == clientId)
                .Select(c => new { c.CompanyId, c.LocationId, c.PartyRoom })
                .FirstOrDefaultAsync();

            if (client == null || client.CompanyId != program.CompanyId)
            {
                _logger.LogWarning("Refused to start projection program {ProgramId} (company {ProgramCompanyId}) on client {ClientId} (company {ClientCompanyId}): the companies don't match", programId, program.CompanyId, clientId, client?.CompanyId);
                return Result<Guid>.Fail("Projection program does not belong to this kiosk's company.");
            }

            // ParameterValues.Parameter is included explicitly rather than left to EF's
            // relationship fixup: the kiosk templates read parameters by Parameter.Name,
            // and a no-tracking query does no identity resolution to wire that navigation up.
            List<ProjectionProgramStep> steps = await context.ProjectionProgramSteps
                .AsNoTracking()
                .TagWithCallSite()
                .Where(s => s.ProjectionProgramId == programId && s.IsActive)
                .OrderBy(s => s.SortOrder)
                .Include(s => s.Template)
                    .ThenInclude(t => t!.Parameters.Where(p => p.IsActive))
                .Include(s => s.ParameterValues)
                    .ThenInclude(pv => pv.Parameter)
                .ToListAsync();

            // A step with no template can't be rendered, and leaving it in would make the
            // step indexes the runner reports disagree with what the kiosk page draws.
            int unrenderableSteps = steps.RemoveAll(s => s.Template == null);

            if (unrenderableSteps > 0)
            {
                _logger.LogWarning("Skipped {StepCount} step(s) with no template in projection program {ProgramId}", unrenderableSteps, programId);
            }

            if (steps.Count == 0)
            {
                return Result<Guid>.Fail("Projection program has no playable steps.");
            }

            Result<IReadOnlyDictionary<int, ProjectionGameWaitState>> gameWaits = await LoadGameWaitsAsync(context, program, steps);

            if (!gameWaits.IsSuccess)
            {
                _logger.LogWarning("Refused to start projection program {ProgramId} on client {ClientId}: {Error}", programId, clientId, gameWaits.Error);
                return Result<Guid>.Fail(gameWaits.Error!);
            }

            Guid runId = Guid.NewGuid();
            CancellationTokenSource cts = new();

            RunningProgram run = new()
            {
                RunId = runId,
                ProgramId = programId,
                ProgramName = program.Name,
                Steps = steps,
                Cts = cts,
                IsTemporaryTrigger = isTemporaryTrigger,
                BindingContext = new ProjectionBindingContext(program.CompanyId, client.LocationId, clientId, DateTime.UtcNow, client.PartyRoom),
                GameWaits = gameWaits.Data!
            };

            lock (_lock)
            {
                // Restart if this display is already running something, so playback always
                // reflects the latest start request.
                if (_runningPrograms.TryGetValue((clientId, displayIndex), out RunningProgram? existing))
                {
                    existing.Cts.Cancel();
                }

                _runningPrograms[(clientId, displayIndex)] = run;
            }

            OnProgramStarted?.Invoke(clientId, displayIndex, programId);

            _logger.LogInformation("Started projection program {ProgramId} for client {ClientId} display {DisplayIndex} ({StepCount} steps, repeatInfinitely: {RepeatInfinitely}, repeatCount: {RepeatCount}, temporary: {IsTemporary})", programId, clientId, displayIndex, steps.Count, repeatInfinitely, repeatCount, isTemporaryTrigger);

            _ = Task.Run(() => RunProgramLoopAsync(clientId, displayIndex, run, repeatInfinitely, repeatCount, cts.Token));

            return Result<Guid>.Ok(runId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting projection program {ProgramId} for client {ClientId} display {DisplayIndex}", programId, clientId, displayIndex);
            return Result<Guid>.Fail("An error occurred while starting the projection program.");
        }
    }

    /// <summary>
    /// Reads every "Wait for Game" step's settings and checks each watched kiosk exists and is in
    /// the program's company. This context has no tenant filter, so without the check a program
    /// could follow another company's games. Fails closed: the program doesn't start.
    /// </summary>
    private static async Task<Result<IReadOnlyDictionary<int, ProjectionGameWaitState>>> LoadGameWaitsAsync(ApplicationDbContext context, ProjectionProgram program, List<ProjectionProgramStep> steps)
    {
        Dictionary<int, GameWaitSettings> settingsByIndex = [];

        for (int i = 0; i < steps.Count; i++)
        {
            if (!GameWaitStepNames.IsWaitForGame(steps[i].Template?.Name))
            {
                continue;
            }

            Result<GameWaitSettings> parsed = GameWaitSettings.Parse(ParameterValuesByName(steps[i]));

            if (!parsed.IsSuccess)
            {
                return Result<IReadOnlyDictionary<int, ProjectionGameWaitState>>.Fail($"Step {i + 1} ({GameWaitStepNames.Template}): {parsed.Error}");
            }

            settingsByIndex[i] = parsed.Data!;
        }

        if (settingsByIndex.Count == 0)
        {
            return Result<IReadOnlyDictionary<int, ProjectionGameWaitState>>.Ok(NoGameWaits);
        }

        List<Guid> kioskIds = [.. settingsByIndex.Values.Select(s => s.KioskClientId).Distinct()];

        var kiosks = await context.Clients
            .AsNoTracking()
            .TagWithCallSite()
            .Where(c => kioskIds.Contains(c.Id))
            .Select(c => new { c.Id, c.CompanyId, c.Name })
            .ToDictionaryAsync(c => c.Id);

        Dictionary<int, ProjectionGameWaitState> waits = [];

        foreach ((int index, GameWaitSettings settings) in settingsByIndex)
        {
            if (!kiosks.TryGetValue(settings.KioskClientId, out var kiosk) || kiosk.CompanyId != program.CompanyId)
            {
                return Result<IReadOnlyDictionary<int, ProjectionGameWaitState>>.Fail($"Step {index + 1} ({GameWaitStepNames.Template}) watches a laser kiosk that no longer exists. Choose its kiosk again.");
            }

            waits[index] = new ProjectionGameWaitState(settings.Event, settings.KioskClientId, kiosk.Name, settings.SecondsLeft, settings.GiveUpAfterMinutes);
        }

        return Result<IReadOnlyDictionary<int, ProjectionGameWaitState>>.Ok(waits);
    }

    private static Dictionary<string, string?> ParameterValuesByName(ProjectionProgramStep step)
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal);

        foreach (ProjectionProgramParameterValue value in step.ParameterValues)
        {
            if (value.Parameter is { IsActive: true } parameter)
            {
                values[parameter.Name] = value.Value;
            }
        }

        return values;
    }

    private async Task RunProgramLoopAsync(Guid clientId, int displayIndex, RunningProgram run, bool repeatInfinitely, int repeatCount, CancellationToken token)
    {
        bool completedNaturally = false;

        try
        {
            int index = 0;

            // repeatCount is the number of *extra* passes, so 0 plays the program once -
            // the semantics the kiosk page's own loop already had.
            int completedPasses = 0;

            while (!token.IsCancellationRequested)
            {
                ProjectionProgramStep step = run.Steps[index];

                // Resolved before the step is announced, so the kiosk's first paint of it already
                // shows live data rather than flashing the fallback.
                IReadOnlyDictionary<Guid, string> boundValues = await ResolveBoundValuesAsync(run, step, token);

                lock (_lock)
                {
                    run.CurrentStepIndex = index;
                    run.BoundValues = boundValues;
                }

                OnProgramStepAdvanced?.Invoke(clientId, displayIndex, index);

                await HoldStepAsync(clientId, displayIndex, index, run, step, token);

                int? pending = TakePendingStepIndex(run);

                if (pending.HasValue)
                {
                    index = pending.Value;
                    continue;
                }

                ProjectionStepPosition next = ProjectionSequence.Advance(index, run.Steps.Count, completedPasses, repeatInfinitely, repeatCount);

                if (next.Finished)
                {
                    completedNaturally = true;
                    break;
                }

                index = next.Index;
                completedPasses = next.CompletedPasses;
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped deliberately via Stop, or replaced by a newer run on this display.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Projection program {ProgramId} playback failed for client {ClientId} display {DisplayIndex}", run.ProgramId, clientId, displayIndex);
        }
        finally
        {
            lock (_lock)
            {
                if (_runningPrograms.TryGetValue((clientId, displayIndex), out RunningProgram? running) && running.RunId == run.RunId)
                {
                    _runningPrograms.Remove((clientId, displayIndex));
                }
            }

            run.Cts.Dispose();

            _logger.LogInformation("Stopped projection program {ProgramId} for client {ClientId} display {DisplayIndex} (completed naturally: {CompletedNaturally})", run.ProgramId, clientId, displayIndex, completedNaturally);

            OnProgramStopped?.Invoke(clientId, displayIndex, run.ProgramId);

            if (completedNaturally && run.IsTemporaryTrigger)
            {
                OnProgramCompletedNaturally?.Invoke(clientId, displayIndex, run.ProgramId);
            }
        }
    }

    private static bool HasBindings(ProjectionProgramStep step) => step.ParameterValues.Any(pv => pv.BindingKey != null);

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveBoundValuesAsync(RunningProgram run, ProjectionProgramStep step, CancellationToken token)
    {
        if (!HasBindings(step))
        {
            return NoBoundValues;
        }

        ProjectionBindingContext context = run.BindingContext with { NowUtc = DateTime.UtcNow };

        return await _bindingResolver.ResolveStepAsync(context, step, token);
    }

    /// <summary>
    /// Re-resolves a held step's bound values, and tells the kiosk only if one actually changed, so
    /// an unchanged leaderboard doesn't re-render every refresh.
    /// </summary>
    private async Task RefreshBoundValuesAsync(Guid clientId, int displayIndex, int stepIndex, RunningProgram run, ProjectionProgramStep step, CancellationToken token)
    {
        IReadOnlyDictionary<Guid, string> refreshed = await ResolveBoundValuesAsync(run, step, token);

        lock (_lock)
        {
            if (run.CurrentStepIndex != stepIndex || HaveSameValues(run.BoundValues, refreshed))
            {
                return;
            }

            run.BoundValues = refreshed;
        }

        OnProgramStepValuesChanged?.Invoke(clientId, displayIndex, stepIndex);
    }

    private static bool HaveSameValues(IReadOnlyDictionary<Guid, string> current, IReadOnlyDictionary<Guid, string> refreshed)
    {
        return current.Count == refreshed.Count
            && refreshed.All(kvp => current.TryGetValue(kvp.Key, out string? value) && value == kvp.Value);
    }

    private async Task HoldStepAsync(Guid clientId, int displayIndex, int stepIndex, RunningProgram run, ProjectionProgramStep step, CancellationToken token)
    {
        if (run.GameWaits.TryGetValue(stepIndex, out ProjectionGameWaitState? gameWait))
        {
            await HoldUntilGameEventAsync(clientId, displayIndex, stepIndex, run, gameWait, token);
            return;
        }

        TimeSpan hold = ProjectionSequence.EffectiveHold(step.HoldForMilliseconds);

        bool hasBindings = HasBindings(step);
        System.Diagnostics.Stopwatch sinceResolved = System.Diagnostics.Stopwatch.StartNew();

        // Played time comes from the run's PlayClock, which stops while paused. Paused time
        // therefore never counts toward the hold (resuming continues the step where it left
        // off), and no played time is lost or double-counted when a pause or resume lands
        // part-way through a tick.
        TimeSpan startedAt = GetPlayedTime(run);

        while (true)
        {
            token.ThrowIfCancellationRequested();

            if (HasPendingStepIndex(run))
            {
                return;
            }

            TimeSpan played = GetPlayedTime(run) - startedAt;

            if (played >= hold)
            {
                return;
            }

            // Wall-clock, not played time: the data on a paused screen should stay current too.
            // The hold is measured on PlayClock, so time spent resolving never stretches the step.
            if (hasBindings && sinceResolved.Elapsed >= BindingRefreshInterval)
            {
                sinceResolved.Restart();

                await RefreshBoundValuesAsync(clientId, displayIndex, stepIndex, run, step, token);

                continue;
            }

            await Task.Delay(ComputeHoldDelayMilliseconds(hold, played, run.IsPaused), token);
        }
    }

    /// <summary>
    /// Holds a "Wait for Game" step until its laser kiosk reports the event, the step's timeout
    /// runs out, or a skip or stop arrives. The status store's event sees every publish in order,
    /// so a short Get ready can't slip between two ticks; the loop also reads the store each tick
    /// for the starting status and in case a publish lands while subscribing. The automation
    /// engine's transition channel is never read here: it has exactly one reader.
    /// </summary>
    private async Task HoldUntilGameEventAsync(Guid clientId, int displayIndex, int stepIndex, RunningProgram run, ProjectionGameWaitState gameWait, CancellationToken token)
    {
        GameEventWait wait = new(gameWait.Event, gameWait.KioskClientId, gameWait.SecondsLeft);
        TimeSpan? timeout = gameWait.GiveUpAfterMinutes is > 0 ? GameWaitTimeoutUnit * gameWait.GiveUpAfterMinutes.Value : null;

        // Runs on the SignalR hub's thread for every kiosk's publish, so it only filters and
        // records; it must never throw back into the hub.
        void OnStatusUpdated(LaserGameStatusDTO status)
        {
            try
            {
                wait.Observe(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking a game status publish for the projection program on client {ClientId} display {DisplayIndex}", clientId, displayIndex);
            }
        }

        _laserGameStatusStore.OnStatusUpdated += OnStatusUpdated;

        try
        {
            TimeSpan startedAt = GetPlayedTime(run);

            while (true)
            {
                token.ThrowIfCancellationRequested();

                if (HasPendingStepIndex(run))
                {
                    return;
                }

                _laserGameStatusStore.TryGetStatus(gameWait.KioskClientId, out LaserGameStatusDTO? current);
                wait.Observe(current);

                // A paused step stays on screen: an event that arrives meanwhile is remembered and
                // moves on as soon as playback resumes.
                if (wait.Satisfied && !run.IsPaused)
                {
                    _logger.LogInformation("Projection program {ProgramId} on client {ClientId} display {DisplayIndex}: step {StepIndex} saw {GameEvent} on kiosk {KioskClientId}", run.ProgramId, clientId, displayIndex, stepIndex, gameWait.Event, gameWait.KioskClientId);
                    return;
                }

                TimeSpan played = GetPlayedTime(run) - startedAt;

                if (timeout.HasValue && played >= timeout.Value)
                {
                    _logger.LogInformation("Projection program {ProgramId} on client {ClientId} display {DisplayIndex}: step {StepIndex} gave up waiting for {GameEvent} on kiosk {KioskClientId} after {TimeoutMinutes} minutes", run.ProgramId, clientId, displayIndex, stepIndex, gameWait.Event, gameWait.KioskClientId, gameWait.GiveUpAfterMinutes);
                    return;
                }

                int delay = timeout.HasValue
                    ? ComputeHoldDelayMilliseconds(timeout.Value, played, run.IsPaused)
                    : ProjectionSequence.TickMilliseconds;

                await Task.Delay(delay, token);
            }
        }
        finally
        {
            _laserGameStatusStore.OnStatusUpdated -= OnStatusUpdated;
        }
    }

    /// <inheritdoc cref="ProjectionSequence.ComputeHoldDelayMilliseconds"/>
    public static int ComputeHoldDelayMilliseconds(TimeSpan hold, TimeSpan played, bool isPaused)
    {
        return ProjectionSequence.ComputeHoldDelayMilliseconds(hold, played, isPaused);
    }

    private TimeSpan GetPlayedTime(RunningProgram run)
    {
        lock (_lock)
        {
            return run.PlayClock.Elapsed;
        }
    }

    private bool HasPendingStepIndex(RunningProgram run)
    {
        lock (_lock)
        {
            return run.PendingStepIndex.HasValue;
        }
    }

    private int? TakePendingStepIndex(RunningProgram run)
    {
        lock (_lock)
        {
            int? pending = run.PendingStepIndex;
            run.PendingStepIndex = null;

            return pending;
        }
    }

    public Result<bool> Pause(Guid clientId, int displayIndex) => SetPaused(clientId, displayIndex, true);

    public Result<bool> Resume(Guid clientId, int displayIndex) => SetPaused(clientId, displayIndex, false);

    private Result<bool> SetPaused(Guid clientId, int displayIndex, bool isPaused)
    {
        try
        {
            lock (_lock)
            {
                if (!_runningPrograms.TryGetValue((clientId, displayIndex), out RunningProgram? running))
                {
                    return Result<bool>.Fail("No projection program is running on that display.");
                }

                if (running.IsPaused == isPaused)
                {
                    return Result<bool>.Ok(true);
                }

                running.IsPaused = isPaused;

                if (isPaused)
                {
                    running.PlayClock.Stop();
                }
                else
                {
                    running.PlayClock.Start();
                }
            }

            _logger.LogInformation("Projection program on client {ClientId} display {DisplayIndex} was {PauseState}", clientId, displayIndex, isPaused ? "paused" : "resumed");

            OnProgramPauseChanged?.Invoke(clientId, displayIndex, isPaused);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing pause state for client {ClientId} display {DisplayIndex}", clientId, displayIndex);
            return Result<bool>.Fail("An error occurred while changing the projection program's pause state.");
        }
    }

    public Result<bool> SkipToStep(Guid clientId, int displayIndex, int stepIndex)
    {
        try
        {
            lock (_lock)
            {
                if (!_runningPrograms.TryGetValue((clientId, displayIndex), out RunningProgram? running))
                {
                    return Result<bool>.Fail("No projection program is running on that display.");
                }

                if (stepIndex < 0 || stepIndex >= running.Steps.Count)
                {
                    return Result<bool>.Fail("Step index is outside the running program.");
                }

                // The run loop notices this within one tick, abandons the current hold and
                // jumps - deliberately not a cancellation, which would end the whole run.
                running.PendingStepIndex = stepIndex;
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error skipping to step {StepIndex} for client {ClientId} display {DisplayIndex}", stepIndex, clientId, displayIndex);
            return Result<bool>.Fail("An error occurred while skipping to the requested step.");
        }
    }

    public Result<bool> SkipToNextStep(Guid clientId, int displayIndex) => SkipRelative(clientId, displayIndex, 1);

    public Result<bool> SkipToPreviousStep(Guid clientId, int displayIndex) => SkipRelative(clientId, displayIndex, -1);

    private Result<bool> SkipRelative(Guid clientId, int displayIndex, int offset)
    {
        int target;

        lock (_lock)
        {
            if (!_runningPrograms.TryGetValue((clientId, displayIndex), out RunningProgram? running))
            {
                return Result<bool>.Fail("No projection program is running on that display.");
            }

            // Wraps in both directions so next/previous never dead-ends at either end.
            int stepCount = running.Steps.Count;
            target = ((running.CurrentStepIndex + offset) % stepCount + stepCount) % stepCount;
        }

        return SkipToStep(clientId, displayIndex, target);
    }

    public Result<bool> Stop(Guid clientId, int displayIndex, Guid? runId = null)
    {
        try
        {
            lock (_lock)
            {
                if (_runningPrograms.TryGetValue((clientId, displayIndex), out RunningProgram? running)
                    && (runId == null || running.RunId == runId.Value))
                {
                    running.Cts.Cancel();
                }
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping projection program for client {ClientId} display {DisplayIndex}", clientId, displayIndex);
            return Result<bool>.Fail("An error occurred while stopping the projection program.");
        }
    }

    public ProjectionRunState? GetRunningState(Guid clientId, int displayIndex)
    {
        lock (_lock)
        {
            return _runningPrograms.TryGetValue((clientId, displayIndex), out RunningProgram? running)
                ? ToState(clientId, displayIndex, running)
                : null;
        }
    }

    public List<ProjectionRunState> GetRunningStates()
    {
        lock (_lock)
        {
            return [.. _runningPrograms.Select(kvp => ToState(kvp.Key.ClientId, kvp.Key.DisplayIndex, kvp.Value))];
        }
    }

    private static ProjectionRunState ToState(Guid clientId, int displayIndex, RunningProgram running) => new()
    {
        RunId = running.RunId,
        ClientId = clientId,
        DisplayIndex = displayIndex,
        ProgramId = running.ProgramId,
        ProgramName = running.ProgramName,
        Steps = running.Steps,
        CurrentStepIndex = running.CurrentStepIndex,
        IsPaused = running.IsPaused,
        IsTemporaryTrigger = running.IsTemporaryTrigger,
        BoundValues = running.BoundValues,
        GameWait = running.GameWaits.GetValueOrDefault(running.CurrentStepIndex)
    };
}
