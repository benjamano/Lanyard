using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class ProjectionProgramRunnerService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<ProjectionProgramRunnerService> logger) : IProjectionProgramRunnerService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<ProjectionProgramRunnerService> _logger = logger;

    // How often a held step re-checks for pause/skip/stop. Paused time doesn't count
    // toward the step's hold, so a plain Task.Delay for the whole hold can't be used -
    // it would have to be torn down and its remaining time recomputed on every pause.
    private const int TickMilliseconds = 200;

    // A step with no configured hold falls back to 5s, matching the behaviour the
    // kiosk page's own loop had before playback moved server-side.
    private const int DefaultHoldMilliseconds = 5000;

    public event Action<Guid, int, Guid>? OnProgramStarted;
    public event Action<Guid, int, int>? OnProgramStepAdvanced;
    public event Action<Guid, int, bool>? OnProgramPauseChanged;
    public event Action<Guid, int, Guid>? OnProgramStopped;
    public event Action<Guid, int, Guid>? OnProgramCompletedNaturally;

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

        public int CurrentStepIndex;
        public volatile bool IsPaused;

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
                IsTemporaryTrigger = isTemporaryTrigger
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
                run.CurrentStepIndex = index;

                OnProgramStepAdvanced?.Invoke(clientId, displayIndex, index);

                await HoldStepAsync(run, run.Steps[index], token);

                int? pending = TakePendingStepIndex(run);

                if (pending.HasValue)
                {
                    index = pending.Value;
                    continue;
                }

                index++;

                if (index < run.Steps.Count)
                {
                    continue;
                }

                index = 0;

                if (!repeatInfinitely && completedPasses >= repeatCount)
                {
                    completedNaturally = true;
                    break;
                }

                completedPasses++;
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

    private async Task HoldStepAsync(RunningProgram run, ProjectionProgramStep step, CancellationToken token)
    {
        int holdMilliseconds = step.HoldForMilliseconds == 0 ? DefaultHoldMilliseconds : step.HoldForMilliseconds;
        int elapsed = 0;

        while (elapsed < holdMilliseconds)
        {
            token.ThrowIfCancellationRequested();

            if (HasPendingStepIndex(run))
            {
                return;
            }

            await Task.Delay(TickMilliseconds, token);

            // Paused time doesn't count toward the hold, so resuming continues the step
            // from where it left off rather than restarting it or advancing immediately.
            if (!run.IsPaused)
            {
                elapsed += TickMilliseconds;
            }
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
        IsTemporaryTrigger = running.IsTemporaryTrigger
    };
}
