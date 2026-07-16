using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class DmxSceneRunnerService(
    IDbContextFactory<ApplicationDbContext> factory,
    IDmxService dmxService,
    ILogger<DmxSceneRunnerService> logger) : IDmxSceneRunnerService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IDmxService _dmxService = dmxService;
    private readonly ILogger<DmxSceneRunnerService> _logger = logger;

    public event Action<Guid, Guid>? OnSceneStarted;
    public event Action<Guid, Guid>? OnSceneStopped;
    public event Action<Guid, Guid, int>? OnSceneStepAdvanced;

    private sealed class RunningScene
    {
        public required Guid ClientId { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    // Key: sceneId. Multiple scenes may run concurrently per client — the last
    // write to a channel wins, so no channel arbitration is needed.
    private readonly Dictionary<Guid, RunningScene> _runningScenes = [];
    private readonly object _lock = new();

    public async Task<Result<bool>> StartSceneAsync(Guid clientId, Guid sceneId, TimeSpan? holdFor = null)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxScene? scene = await context.DmxScenes
                .AsNoTracking()
                .TagWithCallSite()
                .Where(s => s.Id == sceneId && s.IsActive)
                .FirstOrDefaultAsync();

            if (scene == null)
            {
                return Result<bool>.Fail("Scene not found.");
            }

            // Snapshot the steps at start: edits mid-run never corrupt playback.
            // CRUD on a running scene stops it; restarting picks up the new definition.
            List<DmxSceneStep> steps = await context.DmxSceneSteps
                .AsNoTracking()
                .TagWithCallSite()
                .Where(s => s.SceneId == sceneId)
                .OrderBy(s => s.StepNumber)
                .Include(s => s.ChannelValues)
                .ToListAsync();

            if (steps.Count == 0)
            {
                return Result<bool>.Fail("Scene has no steps.");
            }

            CancellationTokenSource cts = new();

            lock (_lock)
            {
                // Restart if already running so playback always reflects the latest start request.
                if (_runningScenes.TryGetValue(sceneId, out RunningScene? existing))
                {
                    existing.Cts.Cancel();
                    existing.Cts.Dispose();
                }

                _runningScenes[sceneId] = new RunningScene
                {
                    ClientId = clientId,
                    Cts = cts
                };
            }

            // Must happen before the loop starts: the run loop's finally disposes the
            // CTS, and CancelAfter on a disposed CTS throws.
            if (holdFor.HasValue && holdFor.Value > TimeSpan.Zero)
            {
                cts.CancelAfter(holdFor.Value);
            }

            OnSceneStarted?.Invoke(clientId, sceneId);

            _logger.LogInformation("Started DMX scene {SceneId} for client {ClientId} ({StepCount} steps, loop: {Loop}, hold: {HoldFor})", sceneId, clientId, steps.Count, scene.Loop, holdFor);

            _ = Task.Run(() => RunSceneLoopAsync(clientId, sceneId, scene.Loop, steps, cts.Token));

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting DMX scene {SceneId} for client {ClientId}", sceneId, clientId);
            return Result<bool>.Fail("An error occurred while starting the DMX scene.");
        }
    }

    private async Task RunSceneLoopAsync(Guid clientId, Guid sceneId, bool loop, List<DmxSceneStep> steps, CancellationToken token)
    {
        try
        {
            do
            {
                foreach (DmxSceneStep step in steps)
                {
                    token.ThrowIfCancellationRequested();

                    OnSceneStepAdvanced?.Invoke(clientId, sceneId, step.StepNumber);

                    foreach (DmxSceneStepChannelValue channelValue in step.ChannelValues)
                    {
                        await _dmxService.UpdateChannelValue(clientId, channelValue.ChannelNumber, channelValue.Value);
                    }

                    // The step's duration IS the scheduler: hold the values, then move on.
                    await Task.Delay(step.Duration, token);
                }
            } while (loop && !token.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            // Stopped deliberately via StopScene/StopAllScenesForClient.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DMX scene {SceneId} playback failed for client {ClientId}", sceneId, clientId);
        }
        finally
        {
            lock (_lock)
            {
                if (_runningScenes.TryGetValue(sceneId, out RunningScene? running) && running.Cts.Token == token)
                {
                    _runningScenes.Remove(sceneId);
                    running.Cts.Dispose();
                }
            }

            _logger.LogInformation("Stopped DMX scene {SceneId} for client {ClientId}", sceneId, clientId);

            OnSceneStopped?.Invoke(clientId, sceneId);
        }
    }

    public Result<bool> StopScene(Guid sceneId)
    {
        try
        {
            lock (_lock)
            {
                if (_runningScenes.TryGetValue(sceneId, out RunningScene? running))
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
                foreach (RunningScene running in _runningScenes.Values.Where(r => r.ClientId == clientId))
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
