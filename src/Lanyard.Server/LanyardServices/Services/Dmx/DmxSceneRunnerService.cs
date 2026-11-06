using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class DmxSceneRunnerService(
    ISystemDbContextFactory factory,
    IDmxService dmxService,
    IBeatClockService beatClock,
    ILogger<DmxSceneRunnerService> logger) : IDmxSceneRunnerService
{
    // A step shorter than this would spin the loop (a 0 s step on a looping scene used to hammer
    // the kiosk as fast as the CPU allowed). One DMX frame is ~23 ms.
    public static readonly TimeSpan MinimumStepDelay = TimeSpan.FromMilliseconds(25);

    private readonly ISystemDbContextFactory _factory = factory;
    private readonly IDmxService _dmxService = dmxService;
    private readonly IBeatClockService _beatClock = beatClock;
    private readonly ILogger<DmxSceneRunnerService> _logger = logger;

    public event Action<Guid, Guid>? OnSceneStarted;
    public event Action<Guid, Guid>? OnSceneStopped;
    public event Action<Guid, Guid, int>? OnSceneStepAdvanced;
    public event Action<Guid, Guid, string>? OnSceneFailed;

    // One per StartSceneAsync call. Each run owns (and alone disposes) its CTS; a restart only
    // cancels the run it replaces, so nothing ever touches a CTS another run already disposed.
    private sealed class SceneRun
    {
        public required Guid ClientId { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    // Key: sceneId. Multiple scenes may run concurrently per client - the last
    // write to a channel wins, so no channel arbitration is needed.
    private readonly Dictionary<Guid, SceneRun> _runningScenes = [];
    private readonly object _lock = new();

    public async Task<Result<bool>> StartSceneAsync(Guid clientId, Guid sceneId, TimeSpan? holdFor = null)
    {
        // Registered before anything is awaited. A momentary button released (StopScene) while
        // the scene is still loading used to find nothing to cancel, and the scene then started
        // and ran forever. Now that release cancels this run before it begins.
        SceneRun run = new() { ClientId = clientId, Cts = new CancellationTokenSource() };
        bool loopStarted = false;

        lock (_lock)
        {
            if (_runningScenes.TryGetValue(sceneId, out SceneRun? existing))
            {
                existing.Cts.Cancel();
            }

            _runningScenes[sceneId] = run;
        }

        try
        {
            CancellationToken token = run.Cts.Token;

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync(token);

            DmxScene? scene = await context.DmxScenes
                .AsNoTracking()
                .TagWithCallSite()
                .Where(s => s.Id == sceneId && s.IsActive)
                .FirstOrDefaultAsync(token);

            if (scene == null)
            {
                return Result<bool>.Fail("Scene not found.");
            }

            if (scene.ClientId != clientId)
            {
                _logger.LogWarning("Refused to start DMX scene {SceneId} on client {ClientId}: it belongs to client {SceneClientId}", sceneId, clientId, scene.ClientId);
                return Result<bool>.Fail("That scene belongs to a different kiosk.");
            }

            // Snapshot the steps at start: edits mid-run never corrupt playback.
            // CRUD on a running scene stops it; restarting picks up the new definition.
            List<DmxSceneStep> steps = await context.DmxSceneSteps
                .AsNoTracking()
                .TagWithCallSite()
                .Where(s => s.SceneId == sceneId)
                .OrderBy(s => s.StepNumber)
                .Include(s => s.ChannelValues)
                .ToListAsync(token);

            if (steps.Count == 0)
            {
                return Result<bool>.Fail("Scene has no steps.");
            }

            // Before the loop starts, while this method still owns the CTS.
            if (holdFor.HasValue && holdFor.Value > TimeSpan.Zero)
            {
                run.Cts.CancelAfter(holdFor.Value);
            }

            _logger.LogInformation("Started DMX scene {SceneId} for client {ClientId} ({StepCount} steps, loop: {Loop}, bpmSync: {BpmSync}, hold: {HoldFor})", sceneId, clientId, steps.Count, scene.Loop, scene.BpmSyncEnabled, holdFor);

            Raise(OnSceneStarted, h => h(clientId, sceneId));

            loopStarted = true;
            _ = Task.Run(() => RunSceneLoopAsync(run, sceneId, scene.IsMomentary, scene.Loop, scene.BpmSyncEnabled, steps));

            return Result<bool>.Ok(true);
        }
        catch (OperationCanceledException)
        {
            // Stopped (or replaced) before it got going - exactly what the caller asked for.
            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting DMX scene {SceneId} for client {ClientId}", sceneId, clientId);
            return Result<bool>.Fail("An error occurred while starting the DMX scene.");
        }
        finally
        {
            if (!loopStarted)
            {
                ReleaseRun(sceneId, run);
            }
        }
    }

    private async Task RunSceneLoopAsync(SceneRun run, Guid sceneId, bool isMomentary, bool loop, bool bpmSyncEnabled, List<DmxSceneStep> steps)
    {
        Guid clientId = run.ClientId;
        CancellationToken token = run.Cts.Token;
        string? failure = null;

        try
        {
            do
            {
                for (int index = 0; index < steps.Count; index++)
                {
                    DmxSceneStep step = steps[index];

                    token.ThrowIfCancellationRequested();

                    // The position, not StepNumber: numbers can have gaps after a delete.
                    int position = index + 1;
                    Raise(OnSceneStepAdvanced, h => h(clientId, sceneId, position));

                    // One batched write per step: a single change notification for any open desk
                    // and (coalesced) a single kiosk message.
                    await _dmxService.UpdateChannelValuesAsync(clientId, ToChannelBatch(step.ChannelValues));

                    // The delay IS the scheduler: hold the values, then move on.
                    // BPM sync recomputes from the live playback position every step,
                    // so timing errors never accumulate. Each delay is one step length
                    // nudged toward the beat grid by a bounded correction (see
                    // BeatMath) - the position estimate jitters as client reports
                    // re-anchor it, and an uncapped chase of the next boundary would
                    // double-fire steps. The first step fires immediately on start
                    // (operators start scenes on a downbeat by hand). When nothing is
                    // playing or the song has no known BPM, the step's fixed Duration
                    // applies instead.
                    TimeSpan delay = step.Duration;

                    if (bpmSyncEnabled)
                    {
                        TimeSpan? beatDelay = await _beatClock.GetDelayUntilNextStepAsync(clientId, step.Beats);

                        if (beatDelay.HasValue)
                        {
                            delay = beatDelay.Value;
                        }
                    }

                    await Task.Delay(delay < MinimumStepDelay ? MinimumStepDelay : delay, token);
                }
            } while (loop && !token.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            // Stopped deliberately via StopScene/StopAllScenesForClient/holdFor/restart.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DMX scene {SceneId} playback failed for client {ClientId}", sceneId, clientId);
            failure = "Playback failed: " + ex.Message;
        }
        finally
        {
            // Restarted rather than stopped: a newer run of this scene already owns the slot.
            // Zeroing here would land on top of that run's first step and black it out, and
            // raising Stopped would tell desks a running scene had ended.
            bool superseded;

            lock (_lock)
            {
                superseded = _runningScenes.TryGetValue(sceneId, out SceneRun? current) && current != run;
            }

            if (isMomentary && !superseded)
            {
                // Momentary scenes must not leave a channel stuck at its last value once
                // playback stops for any reason (release, holdFor expiry, scene edited/
                // deleted while running, or an unexpected fault mid-loop).
                List<DmxChannel> resetBatch = steps
                    .SelectMany(s => s.ChannelValues)
                    .Select(cv => cv.ChannelNumber)
                    .Distinct()
                    .Select(channel => new DmxChannel { Address = channel, Value = 0 })
                    .ToList();

                try
                {
                    await _dmxService.UpdateChannelValuesAsync(clientId, resetBatch);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error resetting {ChannelCount} channels to 0 after momentary DMX scene {SceneId} stopped for client {ClientId}", resetBatch.Count, sceneId, clientId);
                }
            }

            ReleaseRun(sceneId, run);

            if (!superseded)
            {
                _logger.LogInformation("Stopped DMX scene {SceneId} for client {ClientId}", sceneId, clientId);

                if (failure != null)
                {
                    Raise(OnSceneFailed, h => h(clientId, sceneId, failure));
                }

                Raise(OnSceneStopped, h => h(clientId, sceneId));
            }
        }
    }

    private void ReleaseRun(Guid sceneId, SceneRun run)
    {
        lock (_lock)
        {
            if (_runningScenes.TryGetValue(sceneId, out SceneRun? current) && current == run)
            {
                _runningScenes.Remove(sceneId);
            }
        }

        run.Cts.Dispose();
    }

    // Outside the lock, one subscriber at a time: a desk that throws mustn't break playback or
    // stop the other desks hearing about it.
    private void Raise<T>(T? handlers, Action<T> invoke) where T : Delegate
    {
        if (handlers == null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                invoke((T)handler);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A DMX scene event subscriber threw; ignoring it");
            }
        }
    }

    private static List<DmxChannel> ToChannelBatch(IEnumerable<DmxSceneStepChannelValue> channelValues)
    {
        return channelValues
            .Select(cv => new DmxChannel { Address = cv.ChannelNumber, Value = cv.Value })
            .ToList();
    }

    public Result<bool> StopScene(Guid sceneId)
    {
        try
        {
            lock (_lock)
            {
                if (_runningScenes.TryGetValue(sceneId, out SceneRun? running))
                {
                    running.Cts.Cancel();
                }
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping DMX scene {SceneId}", sceneId);
            return Result<bool>.Fail("An error occurred while stopping the DMX scene.");
        }
    }

    public Result<bool> StopAllScenesForClient(Guid clientId)
    {
        try
        {
            lock (_lock)
            {
                foreach (SceneRun running in _runningScenes.Values.Where(r => r.ClientId == clientId))
                {
                    running.Cts.Cancel();
                }
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping all DMX scenes for client {ClientId}", clientId);
            return Result<bool>.Fail("An error occurred while stopping DMX scenes.");
        }
    }

    public List<Guid> GetRunningSceneIds(Guid clientId)
    {
        lock (_lock)
        {
            return _runningScenes
                .Where(kvp => kvp.Value.ClientId == clientId)
                .Select(kvp => kvp.Key)
                .ToList();
        }
    }
}
