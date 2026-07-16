using Lanyard.Application.Services.Authentication;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Dmx;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class DmxSceneService(
    IDbContextFactory<ApplicationDbContext> factory,
    IHubContext<SignalRControlHub> hubContext,
    ILogger<DmxSceneService> logger,
    IMemoryCache cache,
    IDmxService dmxService,
    IDmxSceneRunnerService sceneRunner,
    ISecurityService securityService) : IDmxSceneService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IHubContext<SignalRControlHub> _hubContext = hubContext;
    private readonly ILogger<DmxSceneService> _logger = logger;
    private readonly IMemoryCache _cache = cache;
    private readonly IDmxService _dmxService = dmxService;
    private readonly IDmxSceneRunnerService _sceneRunner = sceneRunner;
    private readonly ISecurityService _securityService = securityService;

    public async Task<Result<IEnumerable<DmxSceneDTO>>> GetScenesForClientAsync(Guid clientId)
    {
        try
        {
            using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<Guid> runningSceneIds = (await GetRunningSceneIdsForClientAsync(clientId)).Data ?? [];

            IEnumerable<DmxSceneDTO> scenes = await context.DmxScenes
                .Where(s => s.ClientId == clientId)
                .Where(x=> x.IsActive == true)
                .Include(x=> x.Steps)
                .Select(s => new DmxSceneDTO
                {
                    Id = s.Id,
                    Name = s.Name,
                    ClientId = s.ClientId,
                    Loop = s.Loop,
                    IsMomentary = s.IsMomentary,
                    KeyBindings = s.KeyBindings,
                    Steps = s.Steps,
                    IsActive = s.IsActive,
                    CreateByUserId = s.CreateByUserId,
                    CreateDate = s.CreateDate,
                    IsRunning = runningSceneIds.Contains(s.Id),
                    StepCount = s.Steps.Count
                })
                .ToListAsync();

            return Result<IEnumerable<DmxSceneDTO>>.Ok(scenes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving DMX scenes for client {ClientId}", clientId);
            return Result<IEnumerable<DmxSceneDTO>>.Fail("An error occurred while retrieving DMX scenes.");
        }
    }

    private Task<Result<List<Guid>>> GetRunningSceneIdsForClientAsync(Guid clientId)
    {
        try
        {
            return Task.FromResult(Result<List<Guid>>.Ok(_sceneRunner.GetRunningSceneIds(clientId)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving running DMX scene IDs for client {ClientId}", clientId);
            return Task.FromResult(Result<List<Guid>>.Fail("An error occurred while retrieving running DMX scene IDs."));
        }
    }

    public async Task<Result<DmxScene>> CreateSceneAsync(Guid clientId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<DmxScene>.Fail("Scene name cannot be empty.");
        }

        string currentUserId = await _securityService.GetCurrentUserIdAsync().ContinueWith(x => x.Result.Data!);
        
        try
        {
            using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxScene newScene = new()
            {
                Name = name,
                ClientId = clientId,
                IsActive = true,
                CreateByUserId = currentUserId!,
                CreateDate = DateTime.UtcNow,
            };

            context.DmxScenes.Add(newScene);

            await context.SaveChangesAsync();

            return Result<DmxScene>.Ok(newScene);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating DMX scene for client {ClientId}", clientId);

            return Result<DmxScene>.Fail("An error occurred while creating the DMX scene.");
        }
    }

    public async Task<Result<DmxSceneDTO>> GetSceneByIdAsync(Guid sceneId, Guid? clientId = null)
    {
        try
        {
            using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<Guid> runningSceneIds = (await GetRunningSceneIdsForClientAsync(clientId ?? Guid.Empty)).Data ?? [];

            DmxSceneDTO? scene = await context.DmxScenes
                .TagWithCallSite()
                .Where(s => s.Id == sceneId && s.IsActive)
                .Include(x=> x.Steps)
                .Select(s => new DmxSceneDTO
                {
                    Id = s.Id,
                    Name = s.Name,
                    ClientId = s.ClientId,
                    Loop = s.Loop,
                    IsMomentary = s.IsMomentary,
                    KeyBindings = s.KeyBindings,
                    Steps = s.Steps,
                    IsActive = s.IsActive,
                    CreateByUserId = s.CreateByUserId,
                    CreateDate = s.CreateDate,
                    IsRunning = runningSceneIds.Contains(s.Id),
                    StepCount = s.Steps.Count
                })
                .FirstOrDefaultAsync();

            if (scene == null)
            {
                return Result<DmxSceneDTO>.Fail("Scene not found.");
            }

            return Result<DmxSceneDTO>.Ok(scene);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving DMX scene {SceneId}", sceneId);
            return Result<DmxSceneDTO>.Fail("An error occurred while retrieving the DMX scene.");
        }
    }

    public async Task<Result<bool>> UpdateSceneAsync(DmxScene scene)
    {
        try
        {
            using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxScene? existingScene = await context.DmxScenes
                .TagWithCallSite()
                .Where(s => s.Id == scene.Id && s.IsActive)
                .FirstOrDefaultAsync();

            if (existingScene == null)
            {
                return Result<bool>.Fail("Scene not found.");
            }

            existingScene.Name = scene.Name;
            existingScene.Loop = scene.Loop;
            existingScene.IsMomentary = scene.IsMomentary;
            existingScene.KeyBindings = scene.KeyBindings;

            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating DMX scene {SceneId}", scene.Id);
            return Result<bool>.Fail("An error occurred while updating the DMX scene.");
        }
    }

    public async Task<Result<bool>> DeleteSceneAsync(Guid sceneId)
    {
        try
        {
            // A deleted scene must not keep playing.
            _sceneRunner.StopScene(sceneId);

            using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxScene? scene = await context.DmxScenes
                .TagWithCallSite()
                .Where(s => s.Id == sceneId && s.IsActive)
                .FirstOrDefaultAsync();

            if (scene == null)
            {
                return Result<bool>.Fail("Scene not found.");
            }

            scene.IsActive = false;
            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting DMX scene {SceneId}", sceneId);
            return Result<bool>.Fail("An error occurred while deleting the DMX scene.");
        }
    }

    public async Task<Result<List<DmxSceneStep>>> GetSceneStepsAsync(Guid sceneId)
    {
        try
        {
            using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<DmxSceneStep> steps = await context.DmxSceneSteps
                .TagWithCallSite()
                .Where(s => s.SceneId == sceneId)
                .OrderBy(s => s.StepNumber)
                .Include(s => s.ChannelValues)
                .ToListAsync();

            return Result<List<DmxSceneStep>>.Ok(steps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving DMX scene steps for scene {SceneId}", sceneId);
            return Result<List<DmxSceneStep>>.Fail("An error occurred while retrieving DMX scene steps.");
        }
    }

    public async Task<Result<bool>> DeleteSceneStepAsync(Guid stepId)
    {
        try
        {
            using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxSceneStep? step = await context.DmxSceneSteps
                .TagWithCallSite()
                .Where(s => s.Id == stepId)
                .FirstOrDefaultAsync();

            if (step == null)
            {
                return Result<bool>.Fail("Scene step not found.");
            }

            // The runner snapshots steps at start, so stop the scene rather than let
            // it keep playing a stale definition; the operator restarts to pick up the change.
            _sceneRunner.StopScene(step.SceneId);

            context.DmxSceneSteps.Remove(step);

            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting DMX scene step {StepId}", stepId);
            return Result<bool>.Fail("An error occurred while deleting the DMX scene step.");
        }
    }

    public async Task<Result<DmxSceneStep>> CreateSceneStepAsync(Guid sceneId)
    {
        try
        {
            // The runner snapshots steps at start, so stop the scene rather than let
            // it keep playing a stale definition; the operator restarts to pick up the change.
            _sceneRunner.StopScene(sceneId);

            using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxScene? scene = await context.DmxScenes
                .TagWithCallSite()
                .Where(s => s.Id == sceneId && s.IsActive)
                .FirstOrDefaultAsync();

            if (scene == null)
            {
                return Result<DmxSceneStep>.Fail("Scene not found.");
            }

            int nextStepNumber = await context.DmxSceneSteps
                .TagWithCallSite()
                .Where(s => s.SceneId == sceneId)
                .MaxAsync(s => (int?)s.StepNumber) ?? 0;

            DmxSceneStep newStep = new()
            {
                SceneId = sceneId,
                StepNumber = nextStepNumber + 1,
                Name = $"Step {nextStepNumber + 1}",
                Duration = TimeSpan.FromSeconds(5),
                CreateByUserId = await _securityService.GetCurrentUserIdAsync().ContinueWith(x => x.Result.Data!),
                CreateDate = DateTime.UtcNow
            };

            context.DmxSceneSteps.Add(newStep);
            await context.SaveChangesAsync();

            return Result<DmxSceneStep>.Ok(newStep);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating DMX scene step for scene {SceneId}", sceneId);
            return Result<DmxSceneStep>.Fail("An error occurred while creating the DMX scene step.");
        }
    }

    public async Task<Result<bool>> UpdateSceneStepDurationAsync(Guid stepId, TimeSpan newDuration)
    {
        try
        {
            using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxSceneStep? step = await context.DmxSceneSteps
                .TagWithCallSite()
                .Where(s => s.Id == stepId)
                .FirstOrDefaultAsync();

            if (step == null)
            {
                return Result<bool>.Fail("Scene step not found.");
            }

            step.Duration = newDuration;
            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating duration for DMX scene step {StepId}", stepId);
            return Result<bool>.Fail("An error occurred while updating the DMX scene step duration.");
        }
    }

    public async Task<Result<bool>> SaveSceneStepChannelValueAsync(Guid stepId, int channelNumber, byte value)
    {
        try
        {
            string currentUserId = await _securityService.GetCurrentUserIdAsync().ContinueWith(x => x.Result.Data!);

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxSceneStepChannelValue? existing = await context.DmxSceneStepChannelValues
                .TagWithCallSite()
                .Where(c => c.SceneStepId == stepId && c.ChannelNumber == channelNumber)
                .FirstOrDefaultAsync();

            if (existing == null)
            {
                DmxSceneStepChannelValue newChannelValue = new()
                {
                    SceneStepId = stepId,
                    ChannelNumber = channelNumber,
                    Value = value,
                    CreateByUserId = currentUserId,
                    CreateDate = DateTime.UtcNow
                };

                context.DmxSceneStepChannelValues.Add(newChannelValue);
            }
            else
            {
                existing.Value = value;
                existing.UpdateByUserId = currentUserId;
                existing.UpdateDate = DateTime.UtcNow;
            }

            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving DMX scene step channel value for step {StepId}, channel {ChannelNumber}", stepId, channelNumber);
            return Result<bool>.Fail("An error occurred while saving the DMX scene step channel value.");
        }
    }

    public Task<Result<bool>> StopAllScenesForClientAsync(Guid clientId)
    {
        return Task.FromResult(_sceneRunner.StopAllScenesForClient(clientId));
    }
}