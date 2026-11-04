using Lanyard.Application.Services.Projection.Bindings;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Projection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class ProjectionProgramRunnerService(
    ISystemDbContextFactory factory,
    IProjectionBindingResolver bindingResolver,
    ILogger<ProjectionProgramRunnerService> logger) : IProjectionProgramRunnerService
{
    private readonly ISystemDbContextFactory _factory = factory;
    private readonly IProjectionBindingResolver _bindingResolver = bindingResolver;
    private readonly ILogger<ProjectionProgramRunnerService> _logger = logger;

    private static readonly IReadOnlyDictionary<Guid, string> NoBoundValues = new Dictionary<Guid, string>();

    /// <summary>
    /// How often a held step with data-bound parameters re-resolves them, in wall-clock time (a
    /// paused screen keeps its data current too). Settable for tests.
    /// </summary>
    public TimeSpan BindingRefreshInterval { get; init; } = TimeSpan.FromSeconds(30);

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
                .Select(c => new { c.CompanyId, c.LocationId })
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
                BindingContext = new ProjectionBindingContext(program.CompanyId, client.LocationId, clientId, DateTime.UtcNow)
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
        BoundValues = running.BoundValues
    };
}
